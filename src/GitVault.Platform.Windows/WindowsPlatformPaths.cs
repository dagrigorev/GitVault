using System.Runtime.Versioning;
using System.Security.Principal;
using GitVault.Core.Platform;

namespace GitVault.Platform.Windows;

/// <summary>Windows filesystem locations for GitVault.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPlatformPaths : PlatformPathsBase
{
    /// <inheritdoc/>
    public override string AppDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GitVault");

    /// <inheritdoc/>
    public override IReadOnlyList<string> SystemGitConfigCandidates
    {
        get
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

            return
            [
                Path.Combine(programData, "Git", "config"),
                Path.Combine(programFiles, "Git", "etc", "gitconfig"),
                Path.Combine(programFiles, "Git", "mingw64", "etc", "gitconfig"),
                Path.Combine(programFilesX86, "Git", "etc", "gitconfig"),
                Path.Combine(programFilesX86, "Git", "mingw64", "etc", "gitconfig"),
            ];
        }
    }

    /// <inheritdoc/>
    public override IReadOnlyList<string> AdditionalKeyDirectories => ExistingDirectories(
    [
        // PuTTY and TortoiseGit users commonly keep .ppk files here.
        // VERIFY: against a real PuTTY install — PuTTY itself has no fixed key directory,
        // these are the conventional locations rather than documented ones.
        Path.Combine(HomeDirectory, "Documents", "PuTTY"),
        Path.Combine(HomeDirectory, "keys"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PuTTY"),
    ]);
}

/// <summary>Windows platform facts.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPlatformInfo : PlatformInfoBase
{
    /// <inheritdoc/>
    public override string PlatformId => "windows";

    /// <inheritdoc/>
    public override bool SupportsPosixPermissions => false;

    /// <inheritdoc/>
    public override bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}
