using GitVault.Core.Models;

namespace GitVault.Core.Ssh;

/// <summary>Everything a private key file reveals without its passphrase.</summary>
/// <param name="Path">File that was read.</param>
/// <param name="Format">Container format.</param>
/// <param name="IsEncrypted">True when a passphrase is required.</param>
/// <param name="KdfRounds">KDF work factor, when the container declares one.</param>
/// <param name="PublicKey">Public half, when it could be recovered without the passphrase.</param>
/// <param name="Comment">Comment, when the container stores one in the clear.</param>
/// <param name="IntegrityIsValid">
/// Result of the container's own integrity check, for formats that have one. Null when the
/// format has no check or it cannot be evaluated without the passphrase.
/// </param>
public sealed record SshKeyFileInfo(
    string Path,
    SshKeyFormat Format,
    bool IsEncrypted,
    int? KdfRounds,
    SshPublicKey? PublicKey,
    string? Comment,
    bool? IntegrityIsValid);

/// <summary>
/// Sniffs a private key file and dispatches to the right container reader. Never decrypts and
/// never asks for a passphrase: everything here is metadata that is stored in the clear.
/// </summary>
public static class SshKeyReader
{
    /// <summary>Reads a private key file's metadata.</summary>
    /// <param name="path">File to read.</param>
    /// <param name="info">The parsed metadata.</param>
    /// <returns><see langword="true"/> when the file was recognised as a key.</returns>
    public static bool TryReadPrivateKeyFile(string path, out SshKeyFileInfo? info)
    {
        info = null;

        string text;
        try
        {
            var length = new FileInfo(path).Length;

            // A private key is a few kilobytes. Refusing anything larger keeps a stray binary
            // in ~/.ssh from being slurped into memory during a scan.
            if (length is 0 or > 512 * 1024)
            {
                return false;
            }

            text = File.ReadAllText(path);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        return TryReadPrivateKeyText(text, path, out info);
    }

    /// <summary>Reads private key metadata from text already in memory.</summary>
    /// <param name="text">File contents.</param>
    /// <param name="path">Path to report in the result.</param>
    /// <param name="info">The parsed metadata.</param>
    /// <returns><see langword="true"/> when the text was recognised as a key.</returns>
    public static bool TryReadPrivateKeyText(string text, string path, out SshKeyFileInfo? info)
    {
        info = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (OpenSshKeyFile.Matches(text))
        {
            if (!OpenSshKeyFile.TryParse(text, out var openssh) || openssh is null)
            {
                return false;
            }

            info = new SshKeyFileInfo(
                path,
                SshKeyFormat.OpenSsh,
                openssh.IsEncrypted,
                openssh.KdfRounds,
                openssh.PublicKeys.FirstOrDefault(),
                openssh.Comment,
                null);

            return true;
        }

        if (PuttyKeyFile.Matches(text))
        {
            if (!PuttyKeyFile.TryParse(text, out var putty) || putty is null)
            {
                return false;
            }

            info = new SshKeyFileInfo(
                path,
                putty.Format,
                putty.IsEncrypted,
                // Argon2's pass count is the v3 work factor, the counterpart of bcrypt's rounds.
                putty.Argon2Parameters?.Passes is > 0 ? putty.Argon2Parameters.Passes : null,
                putty.PublicKey,
                string.IsNullOrEmpty(putty.Comment) ? null : putty.Comment,
                putty.MacIsValid);

            return true;
        }

        if (PemKeyFile.Matches(text))
        {
            if (!PemKeyFile.TryParse(text, out var pem) || pem is null)
            {
                return false;
            }

            info = new SshKeyFileInfo(
                path,
                pem.Format,
                pem.IsEncrypted,
                null,
                pem.PublicKey,
                null,
                null);

            return true;
        }

        return false;
    }

    /// <summary>Reads a public key file.</summary>
    /// <param name="path">File to read.</param>
    /// <param name="key">The parsed key.</param>
    /// <returns><see langword="true"/> when the file held a key.</returns>
    public static bool TryReadPublicKeyFile(string path, out SshPublicKey? key)
    {
        key = null;

        try
        {
            if (new FileInfo(path).Length > 64 * 1024)
            {
                return false;
            }

            return SshPublicKeyReader.TryParseFile(File.ReadAllText(path), out key);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Builds the domain model for a key, combining what the private container revealed with a
    /// paired <c>.pub</c> file when one exists.
    /// </summary>
    /// <param name="info">Private key metadata, or null for a public-only key.</param>
    /// <param name="publicKey">Public key from a <c>.pub</c> file, when present.</param>
    /// <param name="publicPath">Path of the <c>.pub</c> file, when present.</param>
    /// <param name="permissions">Permission snapshot of the private key file.</param>
    /// <returns>The assembled key.</returns>
    public static SshKey ToModel(
        SshKeyFileInfo? info,
        SshPublicKey? publicKey,
        string? publicPath,
        FilePermissionInfo? permissions)
    {
        var effectivePublic = info?.PublicKey ?? publicKey;
        var comment = info?.Comment ?? effectivePublic?.Comment ?? publicKey?.Comment;

        return new SshKey(
            Guid.NewGuid(),
            info?.Path,
            publicPath,
            effectivePublic?.Algorithm ?? SshKeyAlgorithm.Unknown,
            effectivePublic?.BitLength,
            effectivePublic?.FingerprintSha256 ?? string.Empty,
            effectivePublic?.FingerprintMd5 ?? string.Empty,
            comment,
            info?.Format ?? SshKeyFormat.PublicOnly,
            info?.IsEncrypted ?? false,
            info?.KdfRounds,
            effectivePublic?.IsHardwareBacked ?? false)
        {
            Permissions = permissions,
            PublicKeyBlob = effectivePublic?.Blob ?? [],
        };
    }
}
