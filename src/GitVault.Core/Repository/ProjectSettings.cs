using GitVault.Core.Abstractions;
using GitVault.Core.Git;
using GitVault.Core.Models;

namespace GitVault.Core.Repository;

/// <summary>
/// GitVault's own settings for one repository, kept in that repository's configuration.
/// </summary>
/// <remarks>
/// Stored under a <c>[gitvault]</c> section in <c>.git/config</c> rather than in GitVault's
/// application data. The trade was deliberate: the settings then sit beside the thing they
/// describe and survive the folder being moved or renamed, at the cost of GitVault writing to a
/// repository's configuration for its own purposes rather than only at the user's request.
///
/// That cost is paid the same way every other write is: the change is planned, previewed and
/// snapshotted before it happens. Saving project settings is not a special case with a quieter
/// path — it goes through <see cref="IConfigEditor"/> like any other configuration edit.
///
/// Nothing here is a secret. The key is a path, the helper is a command name, and the identity is
/// the same information git already stores in the clear two lines further up the same file.
/// </remarks>
/// <param name="RepositoryPath">Repository these settings belong to.</param>
public sealed record ProjectSettings(string RepositoryPath)
{
    /// <summary>Configuration section GitVault keeps its per-repository settings in.</summary>
    public const string Section = "gitvault";

    /// <summary>Profile pinned to this repository, if any.</summary>
    public Guid? ProfileId { get; init; }

    /// <summary>Name of the pinned profile, kept so the UI can name it before a scan.</summary>
    public string? ProfileName { get; init; }

    /// <summary>SSH key this repository should use, as an absolute path.</summary>
    public string? SshKeyPath { get; init; }

    /// <summary>Credential helper this repository should use.</summary>
    public string? CredentialHelper { get; init; }

    /// <summary>Free-text note, shown on the project settings page.</summary>
    public string? Note { get; init; }

    /// <summary>When true, GitVault's repository scans skip this repository.</summary>
    public bool ExcludeFromScans { get; init; }

    /// <summary>True when nothing has been configured for this repository.</summary>
    public bool IsEmpty =>
        ProfileId is null
        && string.IsNullOrWhiteSpace(ProfileName)
        && string.IsNullOrWhiteSpace(SshKeyPath)
        && string.IsNullOrWhiteSpace(CredentialHelper)
        && string.IsNullOrWhiteSpace(Note)
        && !ExcludeFromScans;
}

/// <summary>Reads and plans writes of the per-repository GitVault settings.</summary>
public interface IProjectSettingsStore
{
    /// <summary>Reads the settings stored in a repository.</summary>
    /// <param name="repositoryPath">Repository to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The settings, empty when the repository has none.</returns>
    Task<ProjectSettings> LoadAsync(string repositoryPath, CancellationToken cancellationToken);

    /// <summary>Works out what saving these settings would change. Writes nothing.</summary>
    /// <param name="settings">Settings to save.</param>
    /// <param name="cancellationToken">Cancels the planning.</param>
    /// <returns>The plan.</returns>
    Task<GitOperationPlan> PlanSaveAsync(ProjectSettings settings, CancellationToken cancellationToken);

    /// <summary>Works out what removing GitVault's section would change. Writes nothing.</summary>
    /// <param name="repositoryPath">Repository to clear.</param>
    /// <param name="cancellationToken">Cancels the planning.</param>
    /// <returns>The plan.</returns>
    Task<GitOperationPlan> PlanClearAsync(string repositoryPath, CancellationToken cancellationToken);
}

/// <summary>Per-repository settings kept in the repository's own configuration.</summary>
public sealed class ProjectSettingsStore : IProjectSettingsStore
{
    /// <summary>Operation identifier recorded on the snapshot when settings are saved.</summary>
    public const string SaveOperationId = "ProjectSettingsSave";

    /// <summary>Operation identifier recorded when the section is removed.</summary>
    public const string ClearOperationId = "ProjectSettingsClear";

    private const string ProfileIdKey = Section + ".profileid";
    private const string ProfileNameKey = Section + ".profilename";
    private const string SshKeyKey = Section + ".sshkeypath";
    private const string HelperKey = Section + ".credentialhelper";
    private const string NoteKey = Section + ".note";
    private const string ExcludeKey = Section + ".excludefromscans";

    private const string Section = ProjectSettings.Section;

    private static readonly string[] AllKeys =
        [ProfileIdKey, ProfileNameKey, SshKeyKey, HelperKey, NoteKey, ExcludeKey];

    private readonly IGitConfigService _config;
    private readonly IConfigEditor _editor;

    /// <summary>Creates the store.</summary>
    /// <param name="config">Configuration service used to read.</param>
    /// <param name="editor">Editor used to plan and apply writes.</param>
    public ProjectSettingsStore(IGitConfigService config, IConfigEditor editor)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(editor);

        _config = config;
        _editor = editor;
    }

    /// <inheritdoc/>
    public async Task<ProjectSettings> LoadAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);

        var all = await _config.ListAsync(repositoryPath, cancellationToken).ConfigureAwait(false);

        // Only what this repository declares. A [gitvault] section inherited from the user's
        // global configuration would otherwise appear to belong to every repository at once.
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in all.Where(v => v.Scope == GitConfigScope.Local))
        {
            values[value.Key] = value.Value;
        }

        return new ProjectSettings(repositoryPath)
        {
            ProfileId = values.TryGetValue(ProfileIdKey, out var id) && Guid.TryParse(id, out var parsed)
                ? parsed
                : null,
            ProfileName = Read(values, ProfileNameKey),
            SshKeyPath = Read(values, SshKeyKey),
            CredentialHelper = Read(values, HelperKey),
            Note = Read(values, NoteKey),
            ExcludeFromScans = values.TryGetValue(ExcludeKey, out var exclude) && IsTrue(exclude),
        };
    }

    /// <inheritdoc/>
    public Task<GitOperationPlan> PlanSaveAsync(ProjectSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // A cleared field removes its key rather than writing an empty string, so a repository
        // with nothing configured ends up with no [gitvault] section at all instead of a section
        // full of blanks.
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [ProfileIdKey] = settings.ProfileId?.ToString(),
            [ProfileNameKey] = Blank(settings.ProfileName),
            [SshKeyKey] = Blank(settings.SshKeyPath),
            [HelperKey] = Blank(settings.CredentialHelper),
            [NoteKey] = Blank(settings.Note),
            [ExcludeKey] = settings.ExcludeFromScans ? "true" : null,
        };

        return _editor.PlanBatchAsync(
            SaveOperationId,
            values,
            GitConfigScope.Local,
            settings.RepositoryPath,
            cancellationToken);
    }

    /// <inheritdoc/>
    public Task<GitOperationPlan> PlanClearAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in AllKeys)
        {
            values[key] = null;
        }

        return _editor.PlanBatchAsync(
            ClearOperationId,
            values,
            GitConfigScope.Local,
            repositoryPath,
            cancellationToken);
    }

    private static string? Read(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Git's own notion of a true boolean, which is broader than <c>bool.Parse</c>.</summary>
    private static bool IsTrue(string value) =>
        value.Equals("true", StringComparison.OrdinalIgnoreCase)
        || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
        || value.Equals("on", StringComparison.OrdinalIgnoreCase)
        || value == "1"
        || value.Length == 0;
}
