using System.Runtime.Versioning;
using GitVault.Core.Ssh;

namespace GitVault.Platform.Linux;

/// <summary>Distribution packages, Nix and Homebrew-on-Linux.</summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxSshToolHints : ISshToolHints
{
    /// <inheritdoc/>
    public IReadOnlyList<string> SshKeygenCandidates =>
    [
        "/usr/bin/ssh-keygen",
        "/usr/local/bin/ssh-keygen",
        "/bin/ssh-keygen",
        "/run/current-system/sw/bin/ssh-keygen",
        "/home/linuxbrew/.linuxbrew/bin/ssh-keygen",
    ];

    /// <inheritdoc/>
    public IReadOnlyList<string> SshAddCandidates =>
    [
        "/usr/bin/ssh-add",
        "/usr/local/bin/ssh-add",
        "/bin/ssh-add",
        "/run/current-system/sw/bin/ssh-add",
        "/home/linuxbrew/.linuxbrew/bin/ssh-add",
    ];
}
