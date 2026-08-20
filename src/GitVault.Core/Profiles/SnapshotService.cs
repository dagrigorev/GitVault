using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using GitVault.Core.Abstractions;

namespace GitVault.Core.Profiles;

/// <summary>What a snapshot was taken for.</summary>
/// <param name="OperationId">
/// Stable operation identifier, used as a localization key suffix. Never display text: the
/// snapshot list must render in whatever language the user is reading today, not the one they
/// happened to be using when the snapshot was taken.
/// </param>
/// <param name="ProfileName">Name of the profile involved, as the user wrote it.</param>
/// <param name="Target">Scope and repository the operation addressed, for display.</param>
public sealed record SnapshotMetadata(string OperationId, string ProfileName, string Target)
{
    /// <summary>Metadata for a snapshot taken outside any profile operation.</summary>
    public static SnapshotMetadata Unknown { get; } = new("Unknown", string.Empty, string.Empty);
}

/// <summary>Serialized form of <see cref="SnapshotMetadata"/> plus the sequence number.</summary>
public sealed class SnapshotManifestHeader
{
    /// <summary>Monotonic display number, unique across retained snapshots.</summary>
    public int Sequence { get; set; }

    /// <summary>When the snapshot was taken.</summary>
    public DateTimeOffset TakenUtc { get; set; }

    /// <summary>Operation identifier; a localization key suffix, not display text.</summary>
    public string OperationId { get; set; } = SnapshotMetadata.Unknown.OperationId;

    /// <summary>Profile the operation involved.</summary>
    public string ProfileName { get; set; } = string.Empty;

    /// <summary>Scope and repository the operation addressed.</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>How many files the snapshot preserves.</summary>
    public int FileCount { get; set; }
}

/// <summary>Source-generated JSON context for snapshot headers.</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(SnapshotManifestHeader))]
public sealed partial class SnapshotJsonContext : JsonSerializerContext;

/// <summary>A copy of the files GitVault is about to change.</summary>
/// <param name="Path">Directory holding the copies.</param>
/// <param name="TakenUtc">When it was taken.</param>
/// <param name="Files">Original path to copied path.</param>
public sealed record Snapshot(string Path, DateTimeOffset TakenUtc, IReadOnlyDictionary<string, string> Files)
{
    /// <summary>Display number assigned at capture.</summary>
    public int Sequence { get; init; }
}

/// <summary>A retained snapshot, as the snapshots page lists it.</summary>
/// <param name="Path">Directory holding the copies.</param>
/// <param name="Sequence">Display number.</param>
/// <param name="TakenUtc">When it was taken.</param>
/// <param name="OperationId">Operation identifier; a localization key suffix.</param>
/// <param name="ProfileName">Profile the operation involved.</param>
/// <param name="Target">Scope and repository the operation addressed.</param>
/// <param name="FileCount">How many files it preserves.</param>
/// <param name="IsRestorable">True when the manifest is present and readable.</param>
public sealed record SnapshotInfo(
    string Path,
    int Sequence,
    DateTimeOffset TakenUtc,
    string OperationId,
    string ProfileName,
    string Target,
    int FileCount,
    bool IsRestorable);

/// <summary>What restoring one file from a snapshot would do.</summary>
/// <param name="OriginalPath">The file's real location.</param>
/// <param name="WillBeDeleted">True when the file did not exist and restoring removes it again.</param>
/// <param name="ExistsNow">True when the file is present on disk right now.</param>
/// <param name="DiffersFromSnapshot">True when the current bytes differ from the preserved copy.</param>
public sealed record SnapshotFileState(
    string OriginalPath,
    bool WillBeDeleted,
    bool ExistsNow,
    bool DiffersFromSnapshot);

/// <summary>Copies files aside before they are modified, and puts them back on request.</summary>
public interface ISnapshotService
{
    /// <summary>Copies the given files into a new timestamped snapshot directory.</summary>
    /// <param name="filePaths">Files to preserve. Ones that do not exist are recorded as absent.</param>
    /// <param name="metadata">What the snapshot is being taken for.</param>
    /// <param name="cancellationToken">Cancels the copy.</param>
    /// <returns>The snapshot that was taken.</returns>
    Task<Snapshot> CaptureAsync(
        IReadOnlyList<string> filePaths,
        SnapshotMetadata metadata,
        CancellationToken cancellationToken);

    /// <summary>Restores every file in a snapshot to its original location.</summary>
    /// <param name="snapshotPath">Snapshot directory.</param>
    /// <param name="cancellationToken">Cancels the restore.</param>
    /// <returns>The files that were restored.</returns>
    Task<IReadOnlyList<string>> RestoreAsync(string snapshotPath, CancellationToken cancellationToken);

    /// <summary>Lists retained snapshots, newest first.</summary>
    /// <returns>Snapshot directories.</returns>
    IReadOnlyList<string> ListSnapshots();

    /// <summary>Lists retained snapshots with their metadata, newest first.</summary>
    /// <returns>Snapshot descriptions.</returns>
    IReadOnlyList<SnapshotInfo> ListSnapshotsDetailed();

    /// <summary>
    /// Works out what restoring a snapshot would do, without touching anything.
    /// </summary>
    /// <param name="snapshotPath">Snapshot directory.</param>
    /// <param name="cancellationToken">Cancels the comparison.</param>
    /// <returns>One entry per file the snapshot covers.</returns>
    Task<IReadOnlyList<SnapshotFileState>> DescribeAsync(string snapshotPath, CancellationToken cancellationToken);

    /// <summary>The number the next snapshot will be given.</summary>
    /// <returns>The next display number.</returns>
    int PeekNextSequence();
}

/// <summary>
/// Filesystem snapshots under <c>&lt;AppData&gt;/GitVault/snapshots/&lt;utc-timestamp&gt;/</c>.
/// </summary>
/// <remarks>
/// A snapshot is taken before every mutation, so any change GitVault makes can be undone even if
/// the process dies halfway through. A manifest next to the copies records which original each
/// file came from, including files that did not exist — restoring then means deleting them again.
/// A small JSON header beside it records what the snapshot was for, so the snapshots page can say
/// more than a timestamp, and carries the display number so it stays stable as older snapshots
/// are pruned.
/// </remarks>
public sealed class SnapshotService : ISnapshotService
{
    /// <summary>How many snapshots are kept before the oldest are pruned.</summary>
    public const int RetainedSnapshots = 50;

    /// <summary>Name of the file mapping copies back to their originals.</summary>
    public const string ManifestFileName = "manifest.tsv";

    /// <summary>Name of the file describing what the snapshot was taken for.</summary>
    public const string HeaderFileName = "snapshot.json";

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
    public async Task<Snapshot> CaptureAsync(
        IReadOnlyList<string> filePaths,
        SnapshotMetadata metadata,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        ArgumentNullException.ThrowIfNull(metadata);

        var takenUtc = DateTimeOffset.UtcNow;
        var sequence = PeekNextSequence();
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

        var header = new SnapshotManifestHeader
        {
            Sequence = sequence,
            TakenUtc = takenUtc,
            OperationId = metadata.OperationId,
            ProfileName = metadata.ProfileName,
            Target = metadata.Target,
            FileCount = manifest.Count,
        };

        await using (var stream = File.Create(Path.Combine(directory, HeaderFileName)))
        {
            await JsonSerializer
                .SerializeAsync(stream, header, SnapshotJsonContext.Default.SnapshotManifestHeader, cancellationToken)
                .ConfigureAwait(false);
        }

        Prune();
        return new Snapshot(directory, takenUtc, files) { Sequence = sequence };
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

    /// <inheritdoc/>
    public IReadOnlyList<SnapshotInfo> ListSnapshotsDetailed() => [.. ListSnapshots().Select(Describe)];

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SnapshotFileState>> DescribeAsync(
        string snapshotPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotPath);

        var manifestPath = Path.Combine(snapshotPath, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return [];
        }

        var states = new List<SnapshotFileState>();

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
            var existsNow = File.Exists(original);

            if (string.Equals(copyName, AbsentMarker, StringComparison.Ordinal))
            {
                states.Add(new SnapshotFileState(original, WillBeDeleted: existsNow, existsNow, DiffersFromSnapshot: existsNow));
                continue;
            }

            var copyPath = Path.Combine(snapshotPath, copyName);
            states.Add(new SnapshotFileState(
                original,
                WillBeDeleted: false,
                existsNow,
                DiffersFromSnapshot: !FilesAreIdentical(original, copyPath)));
        }

        return states;
    }

    /// <inheritdoc/>
    public int PeekNextSequence()
    {
        var highest = 0;

        foreach (var directory in ListSnapshots())
        {
            var header = ReadHeader(directory);
            if (header is not null && header.Sequence > highest)
            {
                highest = header.Sequence;
            }
        }

        return highest + 1;
    }

    /// <summary>Compares two files byte for byte, treating any read failure as "differs".</summary>
    private static bool FilesAreIdentical(string left, string right)
    {
        try
        {
            if (!File.Exists(left) || !File.Exists(right))
            {
                return false;
            }

            var leftInfo = new FileInfo(left);
            var rightInfo = new FileInfo(right);
            if (leftInfo.Length != rightInfo.Length)
            {
                return false;
            }

            // Config files are small; a straight read is simpler than streaming and comparing.
            return File.ReadAllBytes(left).AsSpan().SequenceEqual(File.ReadAllBytes(right));
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static SnapshotManifestHeader? ReadHeader(string directory)
    {
        var path = Path.Combine(directory, HeaderFileName);

        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, SnapshotJsonContext.Default.SnapshotManifestHeader);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A snapshot whose header we cannot read is still restorable; it just lists thinly.
            return null;
        }
    }

    /// <summary>Builds the listing entry for one snapshot directory.</summary>
    private static SnapshotInfo Describe(string directory)
    {
        var header = ReadHeader(directory);
        var restorable = File.Exists(Path.Combine(directory, ManifestFileName));

        if (header is not null)
        {
            return new SnapshotInfo(
                directory,
                header.Sequence,
                header.TakenUtc,
                header.OperationId,
                header.ProfileName,
                header.Target,
                header.FileCount,
                restorable);
        }

        // Snapshots written before headers existed: recover the timestamp from the folder name.
        var name = Path.GetFileName(directory);
        var taken = DateTimeOffset.TryParseExact(
            name.Length >= 16 ? name[..16] : name,
            "yyyyMMdd'T'HHmmss'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;

        return new SnapshotInfo(
            directory,
            0,
            taken,
            SnapshotMetadata.Unknown.OperationId,
            string.Empty,
            string.Empty,
            0,
            restorable);
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
