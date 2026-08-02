using System.Text.Json;
using System.Text.Json.Serialization;
using GitVault.Core.Abstractions;
using GitVault.Core.Models;

namespace GitVault.Core.Profiles;

/// <summary>One configuration key GitVault set, and what was there before.</summary>
/// <param name="Key">Configuration key.</param>
/// <param name="Scope">Scope it was written at.</param>
/// <param name="RepositoryPath">Repository, for local and worktree scopes.</param>
/// <param name="PreviousValue">Value before the write, or null when the key was unset.</param>
public sealed record WrittenSetting(
    string Key,
    GitConfigScope Scope,
    string? RepositoryPath,
    string? PreviousValue);

/// <summary>What one activation changed, so that deactivation can undo exactly that much.</summary>
/// <param name="ProfileId">Profile that was activated.</param>
/// <param name="ProfileName">Profile name, for the <c>~/.ssh/config</c> markers.</param>
/// <param name="Scope">Scope it was applied at.</param>
/// <param name="RepositoryPath">Repository, when the scope was repository-local.</param>
/// <param name="ActivatedUtc">When it happened.</param>
/// <param name="SnapshotPath">Snapshot taken immediately before.</param>
public sealed record ActivationRecord(
    Guid ProfileId,
    string ProfileName,
    ActivationScope Scope,
    string? RepositoryPath,
    DateTimeOffset ActivatedUtc,
    string? SnapshotPath)
{
    /// <summary>Configuration keys this activation wrote.</summary>
    public IReadOnlyList<WrittenSetting> Settings { get; init; } = [];

    /// <summary>Whether a managed block was written into <c>~/.ssh/config</c>.</summary>
    public bool WroteSshConfigBlock { get; init; }

    /// <summary>Whether the <c>~/.ssh/config</c> file existed before this activation.</summary>
    public bool SshConfigExistedBefore { get; init; }
}

/// <summary>Everything GitVault has activated and not yet deactivated.</summary>
public sealed class ActivationState
{
    /// <summary>Active records, most recent last.</summary>
    [JsonPropertyName("activations")]
    public List<ActivationRecord> Activations { get; set; } = [];
}

/// <summary>Source-generated JSON context for the activation state.</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ActivationState))]
public sealed partial class ActivationStateJsonContext : JsonSerializerContext;

/// <summary>Persists what GitVault has changed.</summary>
public interface IActivationStateStore
{
    /// <summary>Reads the recorded activations.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The state, empty when nothing has been activated.</returns>
    Task<ActivationState> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Records an activation.</summary>
    /// <param name="record">What was changed.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes once the record is persisted.</returns>
    Task RecordAsync(ActivationRecord record, CancellationToken cancellationToken);

    /// <summary>Removes the record for a profile, after it has been deactivated.</summary>
    /// <param name="profileId">Profile to forget.</param>
    /// <param name="scope">Scope to forget.</param>
    /// <param name="repositoryPath">Repository, for repository scope.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The record that was removed, or null when there was none.</returns>
    Task<ActivationRecord?> ForgetAsync(
        Guid profileId,
        ActivationScope scope,
        string? repositoryPath,
        CancellationToken cancellationToken);

    /// <summary>Finds the record for a profile at a scope.</summary>
    /// <param name="profileId">Profile to look for.</param>
    /// <param name="scope">Scope to look at.</param>
    /// <param name="repositoryPath">Repository, for repository scope.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The record, or null.</returns>
    Task<ActivationRecord?> FindAsync(
        Guid profileId,
        ActivationScope scope,
        string? repositoryPath,
        CancellationToken cancellationToken);
}

/// <summary>
/// JSON-file-backed store under the application's data directory.
/// </summary>
/// <remarks>
/// This file is what makes deactivation safe. Without it, "undo the profile" would mean guessing
/// which keys a profile owns and unsetting values the user may have set themselves. With it,
/// GitVault removes exactly the keys it wrote and restores exactly the values it replaced.
/// </remarks>
public sealed class ActivationStateStore : IActivationStateStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates the store.</summary>
    /// <param name="paths">Platform paths, for locating the state file.</param>
    public ActivationStateStore(IPlatformPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        StateFilePath = Path.Combine(paths.AppDataDirectory, "state.json");
    }

    /// <summary>Absolute path of the state file.</summary>
    public string StateFilePath { get; }

    /// <inheritdoc/>
    public async Task<ActivationState> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task RecordAsync(ActivationRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false);

            // Re-activating the same profile at the same scope replaces the old record; keeping
            // both would make deactivation restore a stale "previous" value.
            state.Activations.RemoveAll(a => Matches(a, record.ProfileId, record.Scope, record.RepositoryPath));
            state.Activations.Add(record);

            await SaveUnlockedAsync(state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<ActivationRecord?> ForgetAsync(
        Guid profileId,
        ActivationScope scope,
        string? repositoryPath,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            var record = state.Activations.FirstOrDefault(a => Matches(a, profileId, scope, repositoryPath));

            if (record is not null)
            {
                state.Activations.Remove(record);
                await SaveUnlockedAsync(state, cancellationToken).ConfigureAwait(false);
            }

            return record;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<ActivationRecord?> FindAsync(
        Guid profileId,
        ActivationScope scope,
        string? repositoryPath,
        CancellationToken cancellationToken)
    {
        var state = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return state.Activations.FirstOrDefault(a => Matches(a, profileId, scope, repositoryPath));
    }

    private static bool Matches(ActivationRecord record, Guid profileId, ActivationScope scope, string? repositoryPath) =>
        record.ProfileId == profileId
        && record.Scope == scope
        && string.Equals(record.RepositoryPath, repositoryPath, StringComparison.OrdinalIgnoreCase);

    private async Task<ActivationState> LoadUnlockedAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(StateFilePath))
            {
                return new ActivationState();
            }

            await using var stream = File.OpenRead(StateFilePath);
            return await JsonSerializer
                .DeserializeAsync(stream, ActivationStateJsonContext.Default.ActivationState, cancellationToken)
                .ConfigureAwait(false) ?? new ActivationState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A corrupt state file must not stop the app; it does mean deactivation can no
            // longer undo those activations, which the UI surfaces as "nothing recorded".
            return new ActivationState();
        }
    }

    private async Task SaveUnlockedAsync(ActivationState state, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(StateFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = StateFilePath + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer
                .SerializeAsync(stream, state, ActivationStateJsonContext.Default.ActivationState, cancellationToken)
                .ConfigureAwait(false);
        }

        File.Move(temp, StateFilePath, overwrite: true);
    }
}
