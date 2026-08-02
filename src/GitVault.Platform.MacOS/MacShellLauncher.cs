using System.Runtime.Versioning;
using GitVault.Core.Platform;

namespace GitVault.Platform.MacOS;

/// <summary>Opens Finder windows.</summary>
[SupportedOSPlatform("macos")]
public sealed class MacShellLauncher : ShellLauncherBase
{
    /// <inheritdoc/>
    protected override (string FileName, IReadOnlyList<string> Arguments) BuildOpenDirectoryCommand(
        string directoryPath) => ("/usr/bin/open", [directoryPath]);

    /// <inheritdoc/>
    protected override (string FileName, IReadOnlyList<string> Arguments) BuildRevealFileCommand(
        string filePath) => ("/usr/bin/open", ["-R", filePath]);
}
