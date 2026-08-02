using System.Runtime.Versioning;
using GitVault.Core.Platform;

namespace GitVault.Platform.MacOS;

/// <summary>macOS filesystem locations for GitVault.</summary>
[SupportedOSPlatform("macos")]
public sealed class MacPlatformPaths : PlatformPathsBase
{
    /// <inheritdoc/>
    public override string AppDataDirectory => Path.Combine(
        HomeDirectory,
        "Library",
        "Application Support",
        "GitVault");

    /// <inheritdoc/>
    public override IReadOnlyList<string> SystemGitConfigCandidates =>
    [
        "/usr/local/etc/gitconfig",
        "/opt/homebrew/etc/gitconfig",
        "/etc/gitconfig",
        "/Library/Developer/CommandLineTools/usr/share/git-core/gitconfig",
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<string> AdditionalKeyDirectories => ExistingDirectories(
    [
        Path.Combine(HomeDirectory, ".ssh"),
        Path.Combine(HomeDirectory, "keys"),
        Path.Combine(HomeDirectory, ".secrets"),
    ]);
}

/// <summary>macOS platform facts.</summary>
[SupportedOSPlatform("macos")]
public sealed class MacPlatformInfo : PlatformInfoBase
{
    /// <inheritdoc/>
    public override string PlatformId => "macos";

    /// <inheritdoc/>
    public override bool SupportsPosixPermissions => true;

    /// <inheritdoc/>
    public override bool IsElevated => PosixElevation.IsRoot();
}
