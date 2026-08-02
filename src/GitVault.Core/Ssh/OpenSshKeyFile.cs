using System.Text;
using GitVault.Core.Models;

namespace GitVault.Core.Ssh;

/// <summary>What an OpenSSH v1 private key container declares about itself.</summary>
/// <param name="CipherName">Cipher protecting the private section, <c>none</c> when unprotected.</param>
/// <param name="KdfName">Key derivation function name, <c>none</c> or <c>bcrypt</c>.</param>
/// <param name="KdfRounds">Work factor declared by the KDF, when it declares one.</param>
/// <param name="PublicKeys">Public keys carried in the clear part of the container.</param>
/// <param name="Comment">Comment, readable only when the container is unencrypted.</param>
public sealed record OpenSshKeyContainer(
    string CipherName,
    string KdfName,
    int? KdfRounds,
    IReadOnlyList<SshPublicKey> PublicKeys,
    string? Comment)
{
    /// <summary>True when a passphrase is needed to read the private half.</summary>
    public bool IsEncrypted => !string.Equals(CipherName, "none", StringComparison.Ordinal);
}

/// <summary>
/// Reader for the OpenSSH v1 private key container
/// (<c>-----BEGIN OPENSSH PRIVATE KEY-----</c>).
/// </summary>
/// <remarks>
/// The public half and the KDF parameters live in the clear, so encryption state, algorithm,
/// fingerprint and work factor are all readable <em>without</em> a passphrase. The private half
/// is only decoded when the container declares <c>none</c>; GitVault does not implement
/// <c>bcrypt_pbkdf</c> and delegates passphrase-protected operations to <c>ssh-keygen</c>.
/// </remarks>
public static class OpenSshKeyFile
{
    /// <summary>Header line that identifies the container.</summary>
    public const string BeginMarker = "-----BEGIN OPENSSH PRIVATE KEY-----";

    /// <summary>Footer line that closes the container.</summary>
    public const string EndMarker = "-----END OPENSSH PRIVATE KEY-----";

    private static readonly byte[] Magic = "openssh-key-v1\0"u8.ToArray();

    /// <summary>True when the text looks like an OpenSSH v1 container.</summary>
    /// <param name="text">File contents.</param>
    /// <returns><see langword="true"/> when the begin marker is present.</returns>
    public static bool Matches(string text) =>
        text is not null && text.Contains(BeginMarker, StringComparison.Ordinal);

    /// <summary>Parses the container.</summary>
    /// <param name="text">File contents.</param>
    /// <param name="container">The parsed container.</param>
    /// <returns><see langword="true"/> when the container was understood.</returns>
    public static bool TryParse(string text, out OpenSshKeyContainer? container)
    {
        container = null;

        if (!Matches(text) || !TryDecodeBody(text, out var body))
        {
            return false;
        }

        try
        {
            var reader = new SshWireReader(body);

            var magic = new byte[Magic.Length];
            for (var i = 0; i < Magic.Length; i++)
            {
                magic[i] = reader.ReadByte();
            }

            if (!magic.AsSpan().SequenceEqual(Magic))
            {
                return false;
            }

            var cipherName = reader.ReadText();
            var kdfName = reader.ReadText();
            var kdfOptions = reader.ReadString().ToArray();
            var keyCount = reader.ReadUInt32();

            if (keyCount is 0 or > 16)
            {
                // OpenSSH writes exactly one key; anything else is corrupt or hostile.
                return false;
            }

            var publicKeys = new List<SshPublicKey>((int)keyCount);
            for (var i = 0; i < keyCount; i++)
            {
                publicKeys.Add(SshPublicKeyReader.FromBlob(reader.ReadString()));
            }

            var privateSection = reader.ReadString();
            var rounds = ReadKdfRounds(kdfName, kdfOptions);
            var encrypted = !string.Equals(cipherName, "none", StringComparison.Ordinal);

            string? comment = null;
            if (!encrypted && TryReadPrivateSection(privateSection, out var readComment, out var publicFromPrivate))
            {
                comment = readComment;

                // The private section repeats the public key; prefer that copy's comment but
                // keep the blob from the clear section, which is what OpenSSH fingerprints.
                if (publicFromPrivate is not null && publicKeys.Count == 1
                    && !publicFromPrivate.Blob.AsSpan().SequenceEqual(publicKeys[0].Blob))
                {
                    return false;
                }
            }

            if (comment is not null && publicKeys.Count > 0)
            {
                publicKeys[0] = publicKeys[0] with { Comment = comment };
            }

            container = new OpenSshKeyContainer(cipherName, kdfName, rounds, publicKeys, comment);
            return true;
        }
        catch (SshWireException)
        {
            return false;
        }
    }

    /// <summary>Extracts and base64-decodes the body between the markers.</summary>
    /// <param name="text">File contents.</param>
    /// <param name="body">Decoded bytes.</param>
    /// <returns><see langword="true"/> when the body decoded.</returns>
    internal static bool TryDecodeBody(string text, out byte[] body)
    {
        body = [];

        var start = text.IndexOf(BeginMarker, StringComparison.Ordinal);
        if (start < 0)
        {
            return false;
        }

        start += BeginMarker.Length;
        var end = text.IndexOf(EndMarker, start, StringComparison.Ordinal);
        var encoded = end < 0 ? text[start..] : text[start..end];

        var builder = new StringBuilder(encoded.Length);
        foreach (var c in encoded)
        {
            if (!char.IsWhiteSpace(c))
            {
                builder.Append(c);
            }
        }

        try
        {
            body = Convert.FromBase64String(builder.ToString());
            return body.Length > Magic.Length;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Reads the bcrypt work factor out of the KDF options blob.</summary>
    /// <param name="kdfName">Declared KDF name.</param>
    /// <param name="kdfOptions">Declared KDF options.</param>
    /// <returns>The number of rounds, or null when the KDF declares none.</returns>
    internal static int? ReadKdfRounds(string kdfName, ReadOnlySpan<byte> kdfOptions)
    {
        if (!string.Equals(kdfName, "bcrypt", StringComparison.Ordinal) || kdfOptions.IsEmpty)
        {
            return null;
        }

        try
        {
            var reader = new SshWireReader(kdfOptions);
            reader.SkipString();                    // salt
            return (int)reader.ReadUInt32();
        }
        catch (SshWireException)
        {
            return null;
        }
    }

    /// <summary>
    /// Decodes the unencrypted private section: two check integers that must match, then the
    /// private key itself, then the comment.
    /// </summary>
    /// <param name="section">The private section bytes.</param>
    /// <param name="comment">Comment stored with the key.</param>
    /// <param name="publicKey">Public key as repeated inside the private section.</param>
    /// <returns><see langword="true"/> when the section was well formed.</returns>
    internal static bool TryReadPrivateSection(
        ReadOnlySpan<byte> section,
        out string? comment,
        out SshPublicKey? publicKey)
    {
        comment = null;
        publicKey = null;

        try
        {
            var reader = new SshWireReader(section);
            var check1 = reader.ReadUInt32();
            var check2 = reader.ReadUInt32();

            // OpenSSH writes the same random value twice; a mismatch means a wrong passphrase.
            if (check1 != check2)
            {
                return false;
            }

            var keyType = reader.ReadText();
            var writer = new SshWireWriter();
            writer.WriteText(keyType);

            switch (keyType)
            {
                case "ssh-ed25519":
                {
                    var pk = reader.ReadString();
                    writer.WriteString(pk);
                    reader.SkipString();            // private scalar, deliberately not retained
                    break;
                }

                case "ssh-rsa":
                {
                    var n = reader.ReadMpint().ToArray();
                    var e = reader.ReadMpint().ToArray();
                    writer.WriteMpint(e);
                    writer.WriteMpint(n);
                    reader.ReadMpint();             // d
                    reader.ReadMpint();             // iqmp
                    reader.ReadMpint();             // p
                    reader.ReadMpint();             // q
                    break;
                }

                case "ssh-dss":
                {
                    writer.WriteMpint(reader.ReadMpint());
                    writer.WriteMpint(reader.ReadMpint());
                    writer.WriteMpint(reader.ReadMpint());
                    writer.WriteMpint(reader.ReadMpint());
                    reader.ReadMpint();             // private exponent
                    break;
                }

                default:
                {
                    if (keyType.StartsWith("ecdsa-sha2-", StringComparison.Ordinal))
                    {
                        writer.WriteString(reader.ReadString());   // curve name
                        writer.WriteString(reader.ReadString());   // public point
                        reader.ReadMpint();                        // private scalar
                        break;
                    }

                    // sk-* keys and anything else: the comment still follows the private half,
                    // but we cannot reconstruct the blob, so stop here without failing.
                    comment = null;
                    return true;
                }
            }

            comment = reader.ReadText();
            publicKey = SshPublicKeyReader.FromBlob(writer.ToArray(), comment);
            return true;
        }
        catch (SshWireException)
        {
            return false;
        }
    }
}
