using System.Runtime.Versioning;
using GitVault.Core.Platform;

namespace GitVault.Platform.Linux;

/// <summary>
/// Opens the desktop environment's file manager through <c>xdg-open</c>. There is no portable
/// "select this file" verb, so revealing a file opens its parent directory instead.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxShellLauncher : ShellLauncherBase
{
    /// <inheritdoc/>
    protected override (string FileName, IReadOnlyList<string> Arguments) BuildOpenDirectoryCommand(
        string directoryPath) => ("xdg-open", [directoryPath]);

    /// <inheritdoc/>
    protected override (string FileName, IReadOnlyList<string> Arguments) BuildRevealFileCommand(
        string filePath)
    {
        var parent = Path.GetDirectoryName(filePath);
        return ("xdg-open", [string.IsNullOrEmpty(parent) ? filePath : parent]);
    }
}
