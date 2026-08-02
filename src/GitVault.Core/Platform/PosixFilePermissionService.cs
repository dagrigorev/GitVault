using System.Runtime.Versioning;
using GitVault.Core.Abstractions;
using GitVault.Core.Models;

namespace GitVault.Core.Platform;

/// <summary>
/// POSIX permission handling, shared by the macOS and Linux platform projects. Instantiating it
/// on Windows is a wiring bug: <see cref="File.GetUnixFileMode(string)"/> is not supported there.
/// </summary>
[UnsupportedOSPlatform("windows")]
public abstract class PosixFilePermissionService : IFilePermissionService
{
    /// <summary>Mode a private key must have: read and write for the owner only.</summary>
    public const UnixFileMode PrivateKeyMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>Mode a public key may have: world readable, owner writable.</summary>
    public const UnixFileMode PublicKeyMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    /// <inheritdoc/>
    public bool CanRestrictPermissions => true;

    /// <inheritdoc/>
    public FilePermissionInfo? Read(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var mode = File.GetUnixFileMode(path);

            return new FilePermissionInfo(
                path,
                (int)mode,
                OwnerOf(path),
                IsWorldReadable: mode.HasFlag(UnixFileMode.OtherRead),
                IsGroupReadable: mode.HasFlag(UnixFileMode.GroupRead));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public Task<bool> HardenAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (!File.Exists(path))
            {
                return Task.FromResult(false);
            }

            File.SetUnixFileMode(path, PrivateKeyMode);
            return Task.FromResult(File.GetUnixFileMode(path) == PrivateKeyMode);
        }
        catch (IOException)
        {
            return Task.FromResult(false);
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(false);
        }
        catch (PlatformNotSupportedException)
        {
            return Task.FromResult(false);
        }
    }

    /// <summary>Returns the owning account name, when the platform can name it cheaply.</summary>
    /// <param name="path">File to inspect.</param>
    /// <returns>An owner name, or null.</returns>
    protected virtual string? OwnerOf(string path)
    {
        _ = path;

        // Resolving a uid to a name means reading the password database, which is more work than
        // the UI needs. Callers that care can shell out; the mode bits are the actionable part.
        return null;
    }
}
