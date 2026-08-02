using System.Security.Cryptography;
using System.Text;

namespace GitVault.Core.Ssh;

/// <summary>
/// Computes OpenSSH key fingerprints. Both forms are taken over the public key blob exactly as
/// it appears on the wire, which is why the same key fingerprints identically whether it was
/// read from a <c>.pub</c> file, an OpenSSH container, a <c>.ppk</c> or an agent.
/// </summary>
public static class SshFingerprint
{
    /// <summary>Prefix OpenSSH puts on SHA-256 fingerprints.</summary>
    public const string Sha256Prefix = "SHA256:";

    /// <summary>Prefix OpenSSH puts on MD5 fingerprints.</summary>
    public const string Md5Prefix = "MD5:";

    /// <summary>
    /// Canonical OpenSSH fingerprint: <c>SHA256:</c> followed by unpadded base64 of the digest.
    /// </summary>
    /// <param name="publicKeyBlob">Public key blob in SSH wire format.</param>
    /// <returns>The fingerprint, matching <c>ssh-keygen -lf</c>.</returns>
    public static string Sha256(ReadOnlySpan<byte> publicKeyBlob)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(publicKeyBlob, digest);

        // OpenSSH strips the base64 padding.
        return Sha256Prefix + Convert.ToBase64String(digest).TrimEnd('=');
    }

    /// <summary>
    /// Legacy fingerprint: colon-separated lower-case hex of the MD5 digest, as PuTTY and
    /// TortoiseGit still display it.
    /// </summary>
    /// <param name="publicKeyBlob">Public key blob in SSH wire format.</param>
    /// <param name="includePrefix">Whether to prepend <c>MD5:</c> as <c>ssh-keygen -E md5</c> does.</param>
    /// <returns>The fingerprint.</returns>
    public static string Md5(ReadOnlySpan<byte> publicKeyBlob, bool includePrefix = true)
    {
        Span<byte> digest = stackalloc byte[16];

        // MD5 is used here only to reproduce a display format other tools still show. It is
        // never used to make a security decision.
#pragma warning disable CA5351
        MD5.HashData(publicKeyBlob, digest);
#pragma warning restore CA5351

        var builder = new StringBuilder(includePrefix ? Md5Prefix : string.Empty, 4 + (16 * 3));
        for (var i = 0; i < digest.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(':');
            }

            builder.Append(digest[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}
