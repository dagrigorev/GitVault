using System.Runtime.Versioning;
using GitVault.Core.Abstractions;

namespace GitVault.Platform.Linux;

/// <summary>Distribution packages, Nix and Homebrew-on-Linux.</summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxGitInstallHints : IGitInstallHints
{
    /// <inheritdoc/>
    public string GitExecutableName => "git";

    /// <inheritdoc/>
    public IReadOnlyList<string> CandidateGitPaths =>
    [
        "/usr/bin/git",
        "/usr/local/bin/git",
        "/bin/git",
        "/run/current-system/sw/bin/git",
        "/home/linuxbrew/.linuxbrew/bin/git",
        "/snap/bin/git",
    ];
}
