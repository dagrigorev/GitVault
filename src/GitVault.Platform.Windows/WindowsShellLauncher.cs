using System.Runtime.Versioning;
using GitVault.Core.Platform;

namespace GitVault.Platform.Windows;

/// <summary>Opens Explorer windows.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsShellLauncher : ShellLauncherBase
{
    /// <inheritdoc/>
    protected override (string FileName, IReadOnlyList<string> Arguments) BuildOpenDirectoryCommand(
        string directoryPath) => ("explorer.exe", [directoryPath]);

    /// <inheritdoc/>
    protected override (string FileName, IReadOnlyList<string> Arguments) BuildRevealFileCommand(
        string filePath) => ("explorer.exe", ["/select,", filePath]);
}
