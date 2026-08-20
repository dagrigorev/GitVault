using GitVault.Core.Abstractions;

namespace GitVault.App.Tests;

/// <summary>
/// Platform paths rooted in a throwaway directory.
/// </summary>
/// <remarks>
/// The real paths point at the user's profile. Without this, a test that saves a profile or takes
/// a snapshot would write into the running developer's <c>profiles.json</c> and snapshot store —
/// which is exactly the class of accident this application exists to prevent. Every path is
/// therefore redirected, including the git configuration paths, so nothing under test can reach
/// a real configuration file even by mistake.
/// </remarks>
internal sealed class TempPlatformPaths : IPlatformPaths, IDisposable
{
    private bool _disposed;

    /// <summary>Creates a fresh temporary tree.</summary>
    public TempPlatformPaths()
    {
        HomeDirectory = Path.Combine(
            Path.GetTempPath(), "gitvault-tests-" + Guid.NewGuid().ToString("N")[..10]);

        Directory.CreateDirectory(AppDataDirectory);
        Directory.CreateDirectory(DefaultSshDirectory);
    }

    /// <inheritdoc/>
    public string HomeDirectory { get; }

    /// <inheritdoc/>
    public string AppDataDirectory => Path.Combine(HomeDirectory, "appdata");

    /// <inheritdoc/>
    public string LogDirectory => Path.Combine(AppDataDirectory, "logs");

    /// <inheritdoc/>
    public string SnapshotDirectory => Path.Combine(AppDataDirectory, "snapshots");

    /// <inheritdoc/>
    public string DefaultSshDirectory => Path.Combine(HomeDirectory, ".ssh");

    /// <inheritdoc/>
    public string GlobalGitConfigPath => Path.Combine(HomeDirectory, ".gitconfig");

    /// <inheritdoc/>
    public IReadOnlyList<string> SystemGitConfigCandidates => [Path.Combine(HomeDirectory, "system.gitconfig")];

    /// <inheritdoc/>
    public IReadOnlyList<string> AdditionalKeyDirectories => [];

    /// <inheritdoc/>
    public string Expand(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        // Same contract as the real implementations: a leading ~ means the home directory, which
        // here is the temporary tree rather than the developer's profile.
        return path.StartsWith('~')
            ? Path.Combine(HomeDirectory, path.TrimStart('~').TrimStart('/', '\\'))
            : path;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (Directory.Exists(HomeDirectory))
            {
                Directory.Delete(HomeDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A file still held open by a finished test is not worth failing the run over.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }
}
