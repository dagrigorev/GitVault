namespace GitVault.Core.Platform;

/// <summary>
/// Every location GitVault uses, moved wholesale under a directory of the caller's choosing.
/// </summary>
/// <remarks>
/// Windows resolves the user profile and the application-data folder through the operating system
/// rather than the environment, so redirecting <c>USERPROFILE</c> and <c>APPDATA</c> does not move
/// the application anywhere. That left two jobs with no honest way to do them: producing
/// screenshots that contain nobody's real identity, and exercising the destructive paths of the
/// manual test plan without the tester's own keys within reach.
///
/// This answers both. Nothing else changes: the same discovery, the same editors, the same writes
/// — rooted somewhere disposable. The machine's system git configuration is deliberately dropped
/// rather than inherited, because a relocated run that still reads one file from outside its root
/// is not the self-contained thing it claims to be.
/// </remarks>
public sealed class RelocatedPlatformPaths : PlatformPathsBase
{
    /// <summary>Creates the paths.</summary>
    /// <param name="root">Directory to treat as the user's home.</param>
    public RelocatedPlatformPaths(string root)
        : base(Rooted(root))
    {
    }

    /// <inheritdoc/>
    public override string AppDataDirectory => Path.Combine(HomeDirectory, "AppData", "GitVault");

    /// <summary>None: a relocated run reads no machine-wide configuration.</summary>
    public override IReadOnlyList<string> SystemGitConfigCandidates => [];

    /// <summary>None: only the <c>.ssh</c> directory under the root is scanned.</summary>
    public override IReadOnlyList<string> AdditionalKeyDirectories => [];

    private static string Rooted(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        return Path.GetFullPath(root);
    }
}
