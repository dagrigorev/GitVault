using GitVault.Core.Models;

namespace GitVault.Core.Abstractions;

/// <summary>Reads and tightens the permissions of private key files.</summary>
public interface IFilePermissionService
{
    /// <summary>Reads the permission and ownership state of a file.</summary>
    /// <param name="path">File to inspect.</param>
    /// <returns>A snapshot, or null when the file could not be inspected.</returns>
    FilePermissionInfo? Read(string path);

    /// <summary>
    /// Restricts a private key file to its owner: mode <c>0600</c> on POSIX, an explicit
    /// owner-only ACL on Windows.
    /// </summary>
    /// <param name="path">File to restrict.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><see langword="true"/> when the file now grants access to the owner only.</returns>
    Task<bool> HardenAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// True when writing a private key here can be made safe. On a filesystem with no permission
    /// model at all, callers must warn instead of silently writing an unprotected key.
    /// </summary>
    bool CanRestrictPermissions { get; }
}
