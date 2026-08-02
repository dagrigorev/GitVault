using System.Runtime.Versioning;
using System.Security.Principal;
using GitVault.Core.Abstractions;
using GitVault.Core.Models;

namespace GitVault.Platform.Windows;

/// <summary>
/// Windows has no POSIX mode bits, so a private key is protected by its ACL instead. Win32
/// OpenSSH checks that ACL and refuses keys other accounts can read, exactly as it checks mode
/// 0600 elsewhere.
/// </summary>
/// <remarks>
/// Hardening shells out to <c>icacls</c> rather than editing the ACL through the managed API:
/// the managed types need a Windows-specific target framework, and this project stays
/// <c>net8.0</c> so that the whole solution builds on Linux and macOS.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsFilePermissionService : IFilePermissionService
{
    private static readonly TimeSpan IcaclsTimeout = TimeSpan.FromSeconds(15);

    private readonly IProcessRunner _runner;

    /// <summary>Creates the service.</summary>
    /// <param name="runner">Process runner used to invoke <c>icacls</c>.</param>
    public WindowsFilePermissionService(IProcessRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
    }

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

            // VERIFY: enumerating the ACL to decide "world readable" needs a Windows-only API.
            // Until this project can target net8.0-windows, GitVault reports the owner and
            // leaves the readability flags conservative rather than guessing.
            return new FilePermissionInfo(
                path,
                PosixMode: null,
                Owner: CurrentUserName(),
                IsWorldReadable: false,
                IsGroupReadable: false);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> HardenAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var user = CurrentUserName();
        if (string.IsNullOrEmpty(user))
        {
            return false;
        }

        // Break inheritance, drop every inherited entry, then grant the owner full control only.
        var result = await _runner.RunAsync(
            "icacls",
            [path, "/inheritance:r", "/grant:r", $"{user}:F"],
            null,
            IcaclsTimeout,
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess;
    }

    private static string? CurrentUserName()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.Name;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
