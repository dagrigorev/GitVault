namespace GitVault.Core.Abstractions;

/// <summary>
/// Filesystem locations that differ per operating system. Implemented once per platform so
/// that business logic never branches on the OS.
/// </summary>
public interface IPlatformPaths
{
    /// <summary>Current user's home directory.</summary>
    string HomeDirectory { get; }

    /// <summary>Directory GitVault stores its own settings, cache, snapshots and logs in.</summary>
    string AppDataDirectory { get; }

    /// <summary>Directory holding rolling log files.</summary>
    string LogDirectory { get; }

    /// <summary>Directory holding pre-change snapshots.</summary>
    string SnapshotDirectory { get; }

    /// <summary>Default <c>.ssh</c> directory for the current user.</summary>
    string DefaultSshDirectory { get; }

    /// <summary>Path to the per-user git configuration file.</summary>
    string GlobalGitConfigPath { get; }

    /// <summary>Candidate paths for the machine-wide git configuration file.</summary>
    IReadOnlyList<string> SystemGitConfigCandidates { get; }

    /// <summary>Extra directories worth scanning for SSH keys on this platform.</summary>
    IReadOnlyList<string> AdditionalKeyDirectories { get; }

    /// <summary>
    /// Expands <c>~</c> and environment variable references in a user-supplied path.
    /// </summary>
    /// <param name="path">Path to expand. May be null or empty.</param>
    /// <returns>An absolute path, or the input unchanged when it cannot be expanded.</returns>
    string Expand(string path);
}

/// <summary>Facts about the operating system GitVault is running on.</summary>
public interface IPlatformInfo
{
    /// <summary>Short platform identifier: <c>windows</c>, <c>macos</c> or <c>linux</c>.</summary>
    string PlatformId { get; }

    /// <summary>Human-readable OS description, e.g. from <c>RuntimeInformation.OSDescription</c>.</summary>
    string OsDescription { get; }

    /// <summary>Process architecture, e.g. <c>X64</c> or <c>Arm64</c>.</summary>
    string Architecture { get; }

    /// <summary>True when the platform enforces POSIX permission bits on files.</summary>
    bool SupportsPosixPermissions { get; }

    /// <summary>True when the current process is running with elevated privileges.</summary>
    bool IsElevated { get; }
}
