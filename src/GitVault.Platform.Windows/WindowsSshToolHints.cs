using System.Runtime.Versioning;
using GitVault.Core.Ssh;

namespace GitVault.Platform.Windows;

/// <summary>Win32 OpenSSH, plus the copy that ships inside Git for Windows.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSshToolHints : ISshToolHints
{
    /// <inheritdoc/>
    public IReadOnlyList<string> SshKeygenCandidates => Candidates("ssh-keygen.exe");

    /// <inheritdoc/>
    public IReadOnlyList<string> SshAddCandidates => Candidates("ssh-add.exe");

    private static IReadOnlyList<string> Candidates(string executable)
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        return
        [
            Path.Combine(system, "OpenSSH", executable),
            Path.Combine(programFiles, "OpenSSH", executable),
            Path.Combine(programFiles, "Git", "usr", "bin", executable),
            Path.Combine(programFilesX86, "Git", "usr", "bin", executable),
        ];
    }
}
