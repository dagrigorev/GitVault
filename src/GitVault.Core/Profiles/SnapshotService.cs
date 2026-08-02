using System.Globalization;
using GitVault.Core.Abstractions;

namespace GitVault.Core.Profiles;

/// <summary>A copy of the files GitVault is about to change.</summary>
/// <param name="Path">Directory holding the copies.</param>
/// <param name="TakenUtc">When it was taken.</param>
/// <param name="Files">Original path to copied path.</param>
public sealed record Snapshot(string Path, DateTimeOffset TakenUtc, IReadOnlyDictionary<string, string> Files);

/// <summary>Copies files aside before they are modified, and puts them back on request.</summary>
public interface ISnapshotService
{
    /// <summary>Copies the given files into a new timestamped snapshot directory.</summary>
    /// <param name="filePaths">Files to preserve. Ones that do not exist are recorded as absent.</param>
    /// <param name="cancellationToken">Cancels the copy.</param>
    /// <returns>The snapshot that was taken.</returns>
    Task<Snapshot> CaptureAsync(IReadOnlyList<string> filePaths, CancellationToken cancellationToken);

    /// <summary>Restores every file in a snapshot to its original location.</summary>
    /// <param name="snapshotPath">Snapshot directory.</param>
    /// <param name="cancellationToken">Cancels the restore.</param>
    /// <returns>The files that were restored.</returns>
    Task<IReadOnlyList<string>> RestoreAsync(string snapshotPath, CancellationToken cancellationToken);

    /// <summary>Lists retained snapshots, newest first.</summary>
    /// <returns>Snapshot directories.</returns>
    IReadOnlyList<string> ListSnapshots();
}

/// <summary>
/// Filesystem snapshots under <c>&lt;AppData&gt;/GitVault/snapshots/&lt;utc-timestamp&gt;/</c>.
/// </summary>
/// <remarks>
/// A snapshot is taken before every mutation, so any change GitVault makes can be undone even if
/// the process dies halfway through. A manifest next to the copies records which original each
/// file came from, including files that did not exist — restoring then means deleting them again.
/// </remarks>
public sealed class SnapshotService : ISnapshotService
{
    /// <summary>How many snapshots are kept before the oldest are pruned.</summary>
    public const int RetainedSnapshots = 50;

    /// <summary>Name of the file mapping copies back to their originals.</summary>
    public const string ManifestFileName = "manifest.tsv";

    /// <summary>Marker recorded for a file that did not exist when the snapshot was taken.</summary>
    private const string AbsentMarker = "<absent>";

    private readonly IPlatformPaths _paths;

    /// <summary>Creates the service.</summary>
    /// <param name="paths">Platform paths, for the snapshot directory.</param>
    public SnapshotService(IPlatformPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    /// <inheritdoc/>
    public async Task<Snapshot> CaptureAsync(IReadOnlyList<string> filePaths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filePaths);

        var takenUtc = DateTimeOffset.UtcNow;
        var directory = Path.Combine(
            _paths.SnapshotDirectory,
            takenUtc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6]);

        Directory.CreateDirectory(directory);

        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var manifest = new List<string>();
        var index = 0;

        foreach (var original in filePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(original))
            {
                continue;
            }

            var copyName = index++.ToString(CultureInfo.InvariantCulture) + "-" + Path.GetFileName(original);
            var copyPath = Path.Combine(directory, copyName);

            if (File.Exists(original))
            {
                File.Copy(original, copyPath, overwrite: true);
                files[original] = copyPath;
                manifest.Add(original + "\t" + copyName);
            }
            else
            {
                // Recording absence matters: restoring must delete a file we created.
                manifest.Add(original + "\t" + AbsentMarker);
            }
        }

        await File.WriteAllLinesAsync(
            Path.Combine(directory, ManifestFileName), manifest, cancellationToken).ConfigureAwait(false);

        Prune();
        return new Snapshot(directory, takenUtc, files);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> RestoreAsync(string snapshotPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotPath);

        var manifestPath = Path.Combine(snapshotPath, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException($"No snapshot manifest at {manifestPath}");
        }

        var restored = new List<string>();

        foreach (var line in await File.ReadAllLinesAsync(manifestPath, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var separator = line.LastIndexOf('\t');
            if (separator <= 0)
            {
                continue;
            }

            var original = line[..separator];
            var copyName = line[(separator + 1)..];

            if (string.Equals(copyName, AbsentMarker, StringComparison.Ordinal))
            {
                if (File.Exists(original))
                {
                    File.Delete(original);
                    restored.Add(original);
                }

                continue;
            }

            var copyPath = Path.Combine(snapshotPath, copyName);
            if (!File.Exists(copyPath))
            {
                continue;
            }

            var directory = Path.GetDirectoryName(original);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.Copy(copyPath, original, overwrite: true);
            restored.Add(original);
        }

        return restored;
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> ListSnapshots()
    {
        try
        {
            return Directory.Exists(_paths.SnapshotDirectory)
                ? [.. Directory.EnumerateDirectories(_paths.SnapshotDirectory)
                    .OrderByDescending(d => d, StringComparer.Ordinal)]
                : [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Deletes the oldest snapshots beyond <see cref="RetainedSnapshots"/>.</summary>
    private void Prune()
    {
        foreach (var stale in ListSnapshots().Skip(RetainedSnapshots))
        {
            try
            {
                Directory.Delete(stale, recursive: true);
            }
            catch (IOException)
            {
                // A snapshot we cannot delete is harmless; it just uses a little disk.
            }
            catch (UnauthorizedAccessException)
            {
                // Same.
            }
        }
    }
}
