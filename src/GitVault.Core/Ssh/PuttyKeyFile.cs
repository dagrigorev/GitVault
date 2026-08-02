using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GitVault.Core.Models;

namespace GitVault.Core.Ssh;

/// <summary>What a PuTTY <c>.ppk</c> file declares.</summary>
/// <param name="Version">Container version, 2 or 3.</param>
/// <param name="KeyType">Algorithm name, e.g. <c>ssh-rsa</c>.</param>
/// <param name="Encryption">Declared encryption, <c>none</c> or <c>aes256-cbc</c>.</param>
/// <param name="Comment">Comment stored in the file.</param>
/// <param name="PublicKey">Public half, always readable.</param>
/// <param name="MacIsValid">
/// Whether the stored MAC matched. Only meaningful for unencrypted v2 and v3 files; for an
/// encrypted key GitVault cannot check it without the passphrase and leaves it null.
/// </param>
/// <param name="Argon2Parameters">KDF parameters, for v3 files that declare them.</param>
public sealed record PuttyKeyContainer(
    int Version,
    string KeyType,
    string Encryption,
    string Comment,
    SshPublicKey PublicKey,
    bool? MacIsValid,
    PuttyArgon2Parameters? Argon2Parameters)
{
    /// <summary>True when a passphrase is required to read the private half.</summary>
    public bool IsEncrypted => !string.Equals(Encryption, "none", StringComparison.OrdinalIgnoreCase);

    /// <summary>The container format this file uses.</summary>
    public SshKeyFormat Format => Version >= 3 ? SshKeyFormat.Ppk3 : SshKeyFormat.Ppk2;
}

/// <summary>Argon2 parameters declared by a PPK v3 file.</summary>
/// <param name="Flavour">Argon2 variant name, e.g. <c>Argon2id</c>.</param>
/// <param name="MemoryKib">Memory cost in kibibytes.</param>
/// <param name="Passes">Time cost.</param>
/// <param name="Parallelism">Lanes.</param>
/// <param name="SaltHex">Salt, as the hex string stored in the file.</param>
public sealed record PuttyArgon2Parameters(
    string Flavour,
    int MemoryKib,
    int Passes,
    int Parallelism,
    string SaltHex);

/// <summary>
/// Reader for PuTTY private key files, versions 2 and 3.
/// </summary>
/// <remarks>
/// The public blob, algorithm, comment, encryption state and KDF parameters are all in the clear,
/// so a <c>.ppk</c> can be inventoried and fingerprinted without its passphrase — which is what
/// TortoiseGit users need in order to see which key is bound to which remote.
///
/// The v3 MAC key for an unencrypted key is zero-length; that was confirmed against .ppk files
/// written by a real PuTTY, whose stored MAC this implementation reproduces exactly.
///
/// VERIFY: the v2 MAC is taken to cover the plaintext private blob, which cannot be checked
/// without decrypting, so the encrypted-v2 path is still unverified against a real PuTTY.
/// </remarks>
public static class PuttyKeyFile
{
    private const string HeaderPrefix = "PuTTY-User-Key-File-";
    private const string MacKeySalt = "putty-private-key-file-mac-key";

    /// <summary>True when the text looks like a PuTTY key file.</summary>
    /// <param name="text">File contents.</param>
    /// <returns><see langword="true"/> when the header line is present.</returns>
    public static bool Matches(string text) =>
        text is not null && text.TrimStart().StartsWith(HeaderPrefix, StringComparison.Ordinal);

    /// <summary>Parses the file.</summary>
    /// <param name="text">File contents.</param>
    /// <param name="container">The parsed container.</param>
    /// <returns><see langword="true"/> when the file was understood.</returns>
    public static bool TryParse(string text, out PuttyKeyContainer? container)
    {
        container = null;
        if (!Matches(text))
        {
            return false;
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        byte[]? publicBlob = null;
        byte[]? privateBlob = null;
        var version = 0;
        string? keyType = null;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();

            if (name.StartsWith(HeaderPrefix, StringComparison.Ordinal))
            {
                if (!int.TryParse(name[HeaderPrefix.Length..], CultureInfo.InvariantCulture, out version))
                {
                    return false;
                }

                keyType = value;
                continue;
            }

            if (string.Equals(name, "Public-Lines", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadBase64Block(lines, ref i, value, out publicBlob))
                {
                    return false;
                }

                continue;
            }

            if (string.Equals(name, "Private-Lines", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadBase64Block(lines, ref i, value, out privateBlob))
                {
                    return false;
                }

                continue;
            }

            headers[name] = value;
        }

        if (version is < 2 or > 3 || keyType is null || publicBlob is null)
        {
            return false;
        }

        SshPublicKey publicKey;
        try
        {
            publicKey = SshPublicKeyReader.FromBlob(publicBlob, Comment(headers));
        }
        catch (SshWireException)
        {
            return false;
        }

        if (!string.Equals(publicKey.KeyType, keyType, StringComparison.Ordinal))
        {
            return false;
        }

        var encryption = headers.GetValueOrDefault("Encryption", "none");
        var argon2 = ReadArgon2(headers);

        bool? macIsValid = null;
        var storedMac = headers.GetValueOrDefault("Private-MAC");
        if (storedMac is not null
            && privateBlob is not null
            && string.Equals(encryption, "none", StringComparison.OrdinalIgnoreCase))
        {
            macIsValid = VerifyMac(version, keyType, encryption, Comment(headers), publicBlob, privateBlob, storedMac);
        }

        container = new PuttyKeyContainer(
            version, keyType, encryption, Comment(headers), publicKey, macIsValid, argon2);

        return true;
    }

    /// <summary>Computes the MAC over a PPK's declared fields.</summary>
    /// <param name="version">Container version.</param>
    /// <param name="keyType">Algorithm name.</param>
    /// <param name="encryption">Declared encryption.</param>
    /// <param name="comment">Comment.</param>
    /// <param name="publicBlob">Public blob.</param>
    /// <param name="privateBlob">Private blob as stored in the file.</param>
    /// <param name="macKey">MAC key; zero length for an unencrypted v3 file.</param>
    /// <returns>The MAC as lower-case hex.</returns>
    internal static string ComputeMac(
        int version,
        string keyType,
        string encryption,
        string comment,
        ReadOnlySpan<byte> publicBlob,
        ReadOnlySpan<byte> privateBlob,
        byte[] macKey)
    {
        var writer = new SshWireWriter();
        writer.WriteText(keyType);
        writer.WriteText(encryption);
        writer.WriteText(comment);
        writer.WriteString(publicBlob);
        writer.WriteString(privateBlob);

        byte[] mac;
        if (version >= 3)
        {
            using var hmac = new HMACSHA256(macKey);
            mac = hmac.ComputeHash(writer.ToArray());
        }
        else
        {
            // SHA-1 here reproduces PuTTY's v2 format. It is an interoperability requirement,
            // not a security choice, and it only ever confirms a file's own integrity claim.
#pragma warning disable CA5350
            using var hmac = new HMACSHA1(macKey);
#pragma warning restore CA5350
            mac = hmac.ComputeHash(writer.ToArray());
        }

        return Convert.ToHexString(mac).ToLowerInvariant();
    }

    /// <summary>Derives the v2 MAC key from a passphrase.</summary>
    /// <param name="passphrase">Passphrase bytes; empty for an unencrypted file.</param>
    /// <returns>The 20-byte MAC key.</returns>
    internal static byte[] DeriveV2MacKey(ReadOnlySpan<byte> passphrase)
    {
        var salt = Encoding.ASCII.GetBytes(MacKeySalt);
        var buffer = new byte[salt.Length + passphrase.Length];
        salt.CopyTo(buffer.AsSpan());
        passphrase.CopyTo(buffer.AsSpan(salt.Length));

        try
        {
            // SHA-1 again: PuTTY's format, not our choice.
#pragma warning disable CA5350
            return SHA1.HashData(buffer);
#pragma warning restore CA5350
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static string Comment(IReadOnlyDictionary<string, string> headers) =>
        headers.GetValueOrDefault("Comment", string.Empty);

    private static bool VerifyMac(
        int version,
        string keyType,
        string encryption,
        string comment,
        byte[] publicBlob,
        byte[] privateBlob,
        string storedMac)
    {
        var macKey = version >= 3 ? [] : DeriveV2MacKey([]);
        var computed = ComputeMac(version, keyType, encryption, comment, publicBlob, privateBlob, macKey);

        return string.Equals(computed, storedMac.Trim().ToLowerInvariant(), StringComparison.Ordinal);
    }

    private static PuttyArgon2Parameters? ReadArgon2(IReadOnlyDictionary<string, string> headers)
    {
        var flavour = headers.GetValueOrDefault("Key-Derivation");
        if (string.IsNullOrEmpty(flavour))
        {
            return null;
        }

        return new PuttyArgon2Parameters(
            flavour,
            ParseInt(headers.GetValueOrDefault("Argon2-Memory")),
            ParseInt(headers.GetValueOrDefault("Argon2-Passes")),
            ParseInt(headers.GetValueOrDefault("Argon2-Parallelism")),
            headers.GetValueOrDefault("Argon2-Salt", string.Empty));

        static int ParseInt(string? value) =>
            int.TryParse(value, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    private static bool TryReadBase64Block(string[] lines, ref int index, string countText, out byte[]? blob)
    {
        blob = null;
        if (!int.TryParse(countText, CultureInfo.InvariantCulture, out var count) || count < 0 || count > 4096)
        {
            return false;
        }

        var builder = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            index++;
            if (index >= lines.Length)
            {
                return false;
            }

            builder.Append(lines[index].Trim());
        }

        try
        {
            blob = Convert.FromBase64String(builder.ToString());
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
