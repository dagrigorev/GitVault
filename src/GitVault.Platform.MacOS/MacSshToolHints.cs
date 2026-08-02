using System.Runtime.Versioning;
using GitVault.Core.Ssh;

namespace GitVault.Platform.MacOS;

/// <summary>The system OpenSSH, and Homebrew's newer build when it is installed.</summary>
[SupportedOSPlatform("macos")]
public sealed class MacSshToolHints : ISshToolHints
{
    /// <inheritdoc/>
    public IReadOnlyList<string> SshKeygenCandidates =>
    [
        "/opt/homebrew/bin/ssh-keygen",
        "/usr/local/bin/ssh-keygen",
        "/usr/bin/ssh-keygen",
    ];

    /// <inheritdoc/>
    public IReadOnlyList<string> SshAddCandidates =>
    [
        "/opt/homebrew/bin/ssh-add",
        "/usr/local/bin/ssh-add",
        "/usr/bin/ssh-add",
    ];
}
