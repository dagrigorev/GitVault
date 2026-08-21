using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitVault.Core.Repository;

/// <summary>One ref as it stood before an operation.</summary>
/// <param name="RefName">Full ref name, e.g. <c>refs/heads/main</c>.</param>
/// <param name="Commit">Commit it pointed at.</param>
public sealed record RefBackupEntry(string RefName, string Commit);

/// <summary>A set of refs preserved before an operation that would change them.</summary>
public sealed class RefBackup
{
    /// <summary>Identifier, also the directory the backup refs live under.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>When the backup was taken.</summary>
    public DateTimeOffset TakenUtc { get; set; }

    /// <summary>Operation identifier; a localization key suffix, not display text.</summary>
    public string OperationId { get; set; } = string.Empty;

    /// <summary>What the operation addressed, for display.</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>The refs, with the commits they pointed at.</summary>
    public List<RefBackupEntry> Refs { get; set; } = [];
}

/// <summary>Source-generated JSON context for ref backups.</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(RefBackup))]
public sealed partial class RefBackupJsonContext : JsonSerializerContext;

/// <summary>Preserves refs before an operation changes them, and puts them back.</summary>
public interface IRefBackupService
{
    /// <summary>Records where the given refs point, and pins those commits.</summary>
    /// <param name="repositoryPath">Working tree.</param>
    /// <param name="refNames">Full ref names to preserve.</param>
    /// <param name="operationId">Operation identifier, recorded on the backup.</param>
    /// <param name="target">What the operation addressed, for display.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The backup that was taken.</returns>
    Task<RefBackup> BackupAsync(
        string repositoryPath,
        IReadOnlyList<string> refNames,
        string operationId,
        string target,
        CancellationToken cancellationToken);

    /// <summary>Lists the backups a repository holds, newest first.</summary>
    /// <param name="repositoryPath">Working tree.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The backups.</returns>
    Task<IReadOnlyList<RefBackup>> ListAsync(string repositoryPath, CancellationToken cancellationToken);

    /// <summary>Puts every ref in a backup back where it was.</summary>
    /// <param name="repositoryPath">Working tree.</param>
    /// <param name="backupId">Backup to restore.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The refs that were restored.</returns>
    Task<IReadOnlyList<string>> RestoreAsync(
        string repositoryPath,
        string backupId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Ref backups, kept as refs.
/// </summary>
/// <remarks>
/// A file snapshot is the wrong instrument for a ref. Refs live in loose files, in
/// <c>packed-refs</c>, or in both at once, and copying whichever file happens to hold one today
/// preserves an implementation detail rather than the fact worth preserving — which commit the
/// ref pointed at.
///
/// So the backup is itself a ref, under <c>refs/gitvault/backup/</c>. That has a property the
/// file copy does not: it makes the old commits reachable, so git's garbage collection will not
/// discard the history an operation just orphaned. Restoring is one <c>update-ref</c> per entry.
///
/// The manifest beside it exists to describe the backup, not to make it work. Deleting the
/// manifest loses the description; deleting the refs loses the safety net.
/// </remarks>
public sealed class RefBackupService : IRefBackupService
{
    /// <summary>Ref namespace every backup lives under.</summary>
    public const string BackupNamespace = "refs/gitvault/backup";

    /// <summary>Directory inside the git directory holding the manifests.</summary>
    private const string ManifestDirectory = "gitvault/backups";

    private readonly IGitCommandRunner _git;

    /// <summary>Creates the service.</summary>
    /// <param name="git">Command runner.</param>
    public RefBackupService(IGitCommandRunner git)
    {
        ArgumentNullException.ThrowIfNull(git);
        _git = git;
    }

    /// <inheritdoc/>
    public async Task<RefBackup> BackupAsync(
        string repositoryPath,
        IReadOnlyList<string> refNames,
        string operationId,
        string target,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(refNames);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);

        var takenUtc = DateTimeOffset.UtcNow;
        var id = takenUtc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)
            + "-" + Guid.NewGuid().ToString("N")[..6];

        var backup = new RefBackup
        {
            Id = id,
            TakenUtc = takenUtc,
            OperationId = operationId,
            Target = target,
        };

        foreach (var refName in refNames.Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var commit = await _git
                .ReadAsync(repositoryPath, ["rev-parse", "--verify", "--quiet", refName], cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(commit))
            {
                // A ref that does not exist yet is worth recording as absent: restoring then
                // means deleting whatever the operation created.
                backup.Refs.Add(new RefBackupEntry(refName, string.Empty));
                continue;
            }

            await _git
                .RunAsync(repositoryPath, ["update-ref", BackupRef(id, refName), commit], cancellationToken)
                .ConfigureAwait(false);

            backup.Refs.Add(new RefBackupEntry(refName, commit));
        }

        await WriteManifestAsync(repositoryPath, backup, cancellationToken).ConfigureAwait(false);
        return backup;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<RefBackup>> ListAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);

        var directory = await ManifestDirectoryAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        if (directory is null || !Directory.Exists(directory))
        {
            return [];
        }

        var backups = new List<RefBackup>();

        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await using var stream = File.OpenRead(file);
                var backup = await JsonSerializer
                    .DeserializeAsync(stream, RefBackupJsonContext.Default.RefBackup, cancellationToken)
                    .ConfigureAwait(false);

                if (backup is not null)
                {
                    backups.Add(backup);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // An unreadable manifest costs the description, not the backup: the refs are
                // still there and still reachable.
            }
        }

        return [.. backups.OrderByDescending(b => b.TakenUtc)];
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> RestoreAsync(
        string repositoryPath,
        string backupId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupId);

        var backups = await ListAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        var backup = backups.FirstOrDefault(b => b.Id == backupId)
            ?? throw new InvalidOperationException($"No ref backup with id {backupId}");

        var restored = new List<string>();

        foreach (var entry in backup.Refs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = entry.Commit.Length == 0
                ? await _git
                    .RunAsync(repositoryPath, ["update-ref", "-d", entry.RefName], cancellationToken)
                    .ConfigureAwait(false)
                : await _git
                    .RunAsync(repositoryPath, ["update-ref", entry.RefName, entry.Commit], cancellationToken)
                    .ConfigureAwait(false);

            if (result.IsSuccess)
            {
                restored.Add(entry.RefName);
            }
        }

        return restored;
    }

    /// <summary>Where a preserved ref is parked.</summary>
    /// <remarks>
    /// The original ref path is kept intact under the backup namespace, so a backup of
    /// <c>refs/heads/main</c> becomes <c>refs/gitvault/backup/&lt;id&gt;/heads/main</c>. Keeping
    /// the shape means a person reading <c>git for-each-ref</c> can see what a backup holds
    /// without consulting the manifest.
    /// </remarks>
    private static string BackupRef(string id, string refName)
    {
        var suffix = refName.StartsWith("refs/", StringComparison.Ordinal) ? refName[5..] : refName;
        return $"{BackupNamespace}/{id}/{suffix}";
    }

    private async Task<string?> ManifestDirectoryAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        var gitDirectory = await _git
            .ReadAsync(repositoryPath, ["rev-parse", "--absolute-git-dir"], cancellationToken)
            .ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(gitDirectory)
            ? null
            : Path.Combine(gitDirectory, ManifestDirectory);
    }

    private async Task WriteManifestAsync(
        string repositoryPath,
        RefBackup backup,
        CancellationToken cancellationToken)
    {
        var directory = await ManifestDirectoryAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        if (directory is null)
        {
            return;
        }

        Directory.CreateDirectory(directory);

        await using var stream = File.Create(Path.Combine(directory, backup.Id + ".json"));
        await JsonSerializer
            .SerializeAsync(stream, backup, RefBackupJsonContext.Default.RefBackup, cancellationToken)
            .ConfigureAwait(false);
    }
}
