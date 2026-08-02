using System.Runtime.Versioning;
using GitVault.Core.Abstractions;

namespace GitVault.Platform.MacOS;

/// <summary>Homebrew, MacPorts and the Xcode command line tools, in that order of preference.</summary>
[SupportedOSPlatform("macos")]
public sealed class MacGitInstallHints : IGitInstallHints
{
    /// <inheritdoc/>
    public string GitExecutableName => "git";

    /// <inheritdoc/>
    public IReadOnlyList<string> CandidateGitPaths =>
    [
        "/opt/homebrew/bin/git",
        "/usr/local/bin/git",
        "/opt/local/bin/git",
        "/usr/bin/git",
        "/Library/Developer/CommandLineTools/usr/bin/git",
        "/Applications/Xcode.app/Contents/Developer/usr/bin/git",
    ];
}
