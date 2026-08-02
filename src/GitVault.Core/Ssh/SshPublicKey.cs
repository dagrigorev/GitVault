using System.Text;
using GitVault.Core.Models;

namespace GitVault.Core.Ssh;

/// <summary>A parsed SSH public key.</summary>
/// <param name="Blob">The wire-format blob the fingerprints are taken over.</param>
/// <param name="KeyType">Algorithm name as it appears in the blob, e.g. <c>ssh-ed25519</c>.</param>
/// <param name="Algorithm">Algorithm family.</param>
/// <param name="BitLength">Key size in bits, where the algorithm has one.</param>
/// <param name="Comment">Comment from the source file, when it carried one.</param>
public sealed record SshPublicKey(
    byte[] Blob,
    string KeyType,
    SshKeyAlgorithm Algorithm,
    int? BitLength,
    string? Comment)
{
    /// <summary>Canonical OpenSSH SHA-256 fingerprint.</summary>
    public string FingerprintSha256 => SshFingerprint.Sha256(Blob);

    /// <summary>Legacy MD5 fingerprint.</summary>
    public string FingerprintMd5 => SshFingerprint.Md5(Blob);

    /// <summary>True for FIDO2-backed key types.</summary>
    public bool IsHardwareBacked => Algorithm is SshKeyAlgorithm.Ed25519Sk or SshKeyAlgorithm.EcdsaSk;

    /// <summary>Renders the key as a single <c>.pub</c> line.</summary>
    /// <returns>The authorized-keys style line, without a trailing newline.</returns>
    public string ToOpenSshLine() =>
        string.IsNullOrEmpty(Comment)
            ? $"{KeyType} {Convert.ToBase64String(Blob)}"
            : $"{KeyType} {Convert.ToBase64String(Blob)} {Comment}";

    /// <summary>Renders the key in the RFC 4716 exchange format.</summary>
    /// <returns>The multi-line representation.</returns>
    public string ToRfc4716()
    {
        var builder = new StringBuilder();
        builder.Append("---- BEGIN SSH2 PUBLIC KEY ----\n");

        if (!string.IsNullOrEmpty(Comment))
        {
            // RFC 4716 headers wrap at 72 characters with a trailing backslash; a comment that
            // long is rare enough that we emit it whole and let readers cope.
            builder.Append("Comment: \"").Append(Comment).Append("\"\n");
        }

        var base64 = Convert.ToBase64String(Blob);
        for (var i = 0; i < base64.Length; i += 70)
        {
            builder.Append(base64, i, Math.Min(70, base64.Length - i)).Append('\n');
        }

        builder.Append("---- END SSH2 PUBLIC KEY ----\n");
        return builder.ToString();
    }
}

/// <summary>Reads public keys from blobs, <c>.pub</c> lines and RFC 4716 files.</summary>
public static class SshPublicKeyReader
{
    /// <summary>Parses a wire-format public key blob.</summary>
    /// <param name="blob">Blob bytes.</param>
    /// <param name="comment">Comment to attach, when known from elsewhere.</param>
    /// <returns>The parsed key.</returns>
    /// <exception cref="SshWireException">The blob is not a well-formed public key.</exception>
    public static SshPublicKey FromBlob(ReadOnlySpan<byte> blob, string? comment = null)
    {
        var reader = new SshWireReader(blob);
        var keyType = reader.ReadText();

        var (algorithm, bits) = keyType switch
        {
            "ssh-ed25519" => (SshKeyAlgorithm.Ed25519, 256),
            "ssh-ed448" => (SshKeyAlgorithm.Ed448, 448),
            "sk-ssh-ed25519@openssh.com" => (SshKeyAlgorithm.Ed25519Sk, 256),
            "ssh-rsa" or "rsa-sha2-256" or "rsa-sha2-512" => (SshKeyAlgorithm.Rsa, RsaBits(ref reader)),
            "ssh-dss" => (SshKeyAlgorithm.Dsa, DsaBits(ref reader)),
            _ when keyType.StartsWith("ecdsa-sha2-", StringComparison.Ordinal) =>
                (SshKeyAlgorithm.Ecdsa, CurveBits(keyType)),
            _ when keyType.StartsWith("sk-ecdsa-sha2-", StringComparison.Ordinal) =>
                (SshKeyAlgorithm.EcdsaSk, CurveBits(keyType)),
            _ => (SshKeyAlgorithm.Unknown, 0),
        };

        return new SshPublicKey(blob.ToArray(), keyType, algorithm, bits == 0 ? null : bits, comment);
    }

    /// <summary>
    /// Parses a single <c>.pub</c> line of the form
    /// <c>&lt;type&gt; &lt;base64&gt; [comment]</c>, tolerating an <c>authorized_keys</c> options prefix.
    /// </summary>
    /// <param name="line">The line to parse.</param>
    /// <param name="key">The parsed key.</param>
    /// <returns><see langword="true"/> when the line held a key.</returns>
    public static bool TryParseOpenSshLine(string line, out SshPublicKey? key)
    {
        key = null;

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var trimmed = line.Trim();
        if (trimmed.StartsWith('#'))
        {
            return false;
        }

        var parts = trimmed.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        // An authorized_keys line may start with options; the key type is then the second field.
        var typeIndex = LooksLikeKeyType(parts[0]) ? 0 : 1;
        if (typeIndex == 1)
        {
            parts = trimmed.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || !LooksLikeKeyType(parts[1]))
            {
                return false;
            }
        }

        var keyType = parts[typeIndex];
        var encoded = parts[typeIndex + 1];
        var comment = parts.Length > typeIndex + 2 ? parts[typeIndex + 2].Trim() : null;

        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            return false;
        }

        try
        {
            var parsed = FromBlob(blob, string.IsNullOrEmpty(comment) ? null : comment);

            // The declared type and the type inside the blob must agree; if they do not, the
            // file is either corrupt or crafted, and either way we do not trust it.
            if (!string.Equals(parsed.KeyType, keyType, StringComparison.Ordinal))
            {
                return false;
            }

            key = parsed;
            return true;
        }
        catch (SshWireException)
        {
            return false;
        }
    }

    /// <summary>Parses an RFC 4716 public key file.</summary>
    /// <param name="text">File contents.</param>
    /// <param name="key">The parsed key.</param>
    /// <returns><see langword="true"/> when the text held a key.</returns>
    public static bool TryParseRfc4716(string text, out SshPublicKey? key)
    {
        key = null;
        if (string.IsNullOrWhiteSpace(text) || !text.Contains("BEGIN SSH2 PUBLIC KEY", StringComparison.Ordinal))
        {
            return false;
        }

        var body = new StringBuilder();
        string? comment = null;
        var inBody = false;
        var continuation = false;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            if (line.Contains("BEGIN SSH2 PUBLIC KEY", StringComparison.Ordinal))
            {
                inBody = true;
                continue;
            }

            if (line.Contains("END SSH2 PUBLIC KEY", StringComparison.Ordinal))
            {
                break;
            }

            if (!inBody)
            {
                continue;
            }

            if (continuation || line.Contains(':', StringComparison.Ordinal))
            {
                if (line.StartsWith("Comment:", StringComparison.OrdinalIgnoreCase))
                {
                    comment = line["Comment:".Length..].Trim().Trim('"');
                }

                // A header line ending in a backslash continues onto the next line.
                continuation = line.EndsWith('\\');
                continue;
            }

            body.Append(line.Trim());
        }

        try
        {
            key = FromBlob(Convert.FromBase64String(body.ToString()), comment);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (SshWireException)
        {
            return false;
        }
    }

    /// <summary>Reads the first key from a <c>.pub</c> or RFC 4716 file.</summary>
    /// <param name="text">File contents.</param>
    /// <param name="key">The parsed key.</param>
    /// <returns><see langword="true"/> when a key was found.</returns>
    public static bool TryParseFile(string text, out SshPublicKey? key)
    {
        if (TryParseRfc4716(text, out key))
        {
            return true;
        }

        foreach (var line in (text ?? string.Empty).Split('\n'))
        {
            if (TryParseOpenSshLine(line, out key))
            {
                return true;
            }
        }

        key = null;
        return false;
    }

    private static bool LooksLikeKeyType(string value) =>
        value.StartsWith("ssh-", StringComparison.Ordinal)
        || value.StartsWith("ecdsa-", StringComparison.Ordinal)
        || value.StartsWith("sk-", StringComparison.Ordinal)
        || value.StartsWith("rsa-sha2-", StringComparison.Ordinal);

    private static int RsaBits(ref SshWireReader reader)
    {
        reader.ReadMpint();                 // public exponent
        return BitLength(reader.ReadMpint());
    }

    private static int DsaBits(ref SshWireReader reader) => BitLength(reader.ReadMpint());

    private static int CurveBits(string keyType) => keyType switch
    {
        _ when keyType.EndsWith("nistp256", StringComparison.Ordinal) => 256,
        _ when keyType.EndsWith("nistp384", StringComparison.Ordinal) => 384,
        _ when keyType.EndsWith("nistp521", StringComparison.Ordinal) => 521,
        _ when keyType.Contains("nistp256", StringComparison.Ordinal) => 256,
        _ when keyType.Contains("nistp384", StringComparison.Ordinal) => 384,
        _ when keyType.Contains("nistp521", StringComparison.Ordinal) => 521,
        _ => 0,
    };

    /// <summary>Counts the significant bits of a big-endian magnitude.</summary>
    /// <param name="magnitude">Big-endian bytes.</param>
    /// <returns>The bit length, as <c>ssh-keygen</c> reports it.</returns>
    internal static int BitLength(ReadOnlySpan<byte> magnitude)
    {
        var index = 0;
        while (index < magnitude.Length && magnitude[index] == 0)
        {
            index++;
        }

        if (index == magnitude.Length)
        {
            return 0;
        }

        var bits = (magnitude.Length - index - 1) * 8;
        var top = magnitude[index];
        while (top != 0)
        {
            bits++;
            top >>= 1;
        }

        return bits;
    }
}
