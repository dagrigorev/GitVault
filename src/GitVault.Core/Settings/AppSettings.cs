using System.Text.Json.Serialization;

namespace GitVault.Core.Settings;

/// <summary>Application theme preference.</summary>
public enum ThemePreference
{
    /// <summary>Follow the operating system.</summary>
    System = 0,

    /// <summary>Always light.</summary>
    Light,

    /// <summary>Always dark.</summary>
    Dark,
}

/// <summary>How far below a scan root the repository search descends.</summary>
public enum ScanDepth
{
    /// <summary>Walk the whole tree below the root, within the scanner's depth limit.</summary>
    Recursive = 0,

    /// <summary>Look only at the root's immediate children.</summary>
    TopLevel,
}

/// <summary>Which halves of a key pair a folder is expected to hold.</summary>
public enum KeyFolderMode
{
    /// <summary>Private keys and their public halves.</summary>
    PrivateAndPublic = 0,

    /// <summary>Public keys only, such as a folder of published signing keys.</summary>
    PublicOnly,
}

/// <summary>A folder searched for Git repositories.</summary>
/// <remarks>
/// This is application configuration. Adding, editing or removing a root changes what GitVault
/// looks at; it never changes a repository, a key or a credential.
/// </remarks>
public sealed class ScanRoot
{
    /// <summary>Absolute path of the root.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>How deep to search below it.</summary>
    public ScanDepth Depth { get; set; } = ScanDepth.Recursive;

    /// <summary>When false the root is kept in the list but skipped.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Creates an independent copy.</summary>
    /// <returns>A copy of this root.</returns>
    public ScanRoot Clone() => new() { Path = Path, Depth = Depth, Enabled = Enabled };
}

/// <summary>An extra folder searched for SSH keys.</summary>
/// <remarks>
/// Same rule as <see cref="ScanRoot"/>: editing this list changes discovery, never key files.
/// </remarks>
public sealed class KeyFolder
{
    /// <summary>Absolute path of the folder.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>What the folder is expected to hold.</summary>
    public KeyFolderMode Mode { get; set; } = KeyFolderMode.PrivateAndPublic;

    /// <summary>When false the folder is kept in the list but skipped.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Creates an independent copy.</summary>
    /// <returns>A copy of this folder.</returns>
    public KeyFolder Clone() => new() { Path = Path, Mode = Mode, Enabled = Enabled };
}

/// <summary>Policy governing how revealed secrets behave in the UI.</summary>
/// <param name="AutoHideSeconds">Seconds a revealed secret stays visible.</param>
/// <param name="ClipboardClearSeconds">Seconds before a secret copy is cleared from the clipboard.</param>
/// <param name="RequireConfirmation">Whether a confirmation dialog precedes every reveal.</param>
public sealed record SecretRevealPolicy(
    int AutoHideSeconds = 30,
    int ClipboardClearSeconds = 60,
    bool RequireConfirmation = true);

/// <summary>User-visible application settings, persisted as JSON.</summary>
public sealed class AppSettings
{
    /// <summary>BCP-47 culture name of the UI language.</summary>
    public string Language { get; set; } = "en-US";

    /// <summary>Theme preference.</summary>
    public ThemePreference Theme { get; set; } = ThemePreference.System;

    /// <summary>
    /// Extra directories to scan for SSH keys, as bare paths. Superseded by
    /// <see cref="KeyFolders"/>; retained so a settings file written by an older build still
    /// loads. <see cref="MigrateLegacyEntries"/> moves the entries across and empties this.
    /// </summary>
    public List<string> CustomKeyDirectories { get; set; } = [];

    /// <summary>
    /// Root folders scanned for repositories, as bare paths. Superseded by
    /// <see cref="ScanRoots"/>; see <see cref="CustomKeyDirectories"/>.
    /// </summary>
    public List<string> RepositoryScanRoots { get; set; } = [];

    /// <summary>Root folders searched for repositories, with their depth and enabled state.</summary>
    public List<ScanRoot> ScanRoots { get; set; } = [];

    /// <summary>Extra folders searched for SSH keys, with their mode and enabled state.</summary>
    public List<KeyFolder> KeyFolders { get; set; } = [];

    /// <summary>When true, the first activation of a session is always a dry run.</summary>
    public bool DryRunByDefault { get; set; } = true;

    /// <summary>When true, a filesystem watcher triggers a debounced rescan.</summary>
    public bool WatchForChanges { get; set; } = true;

    /// <summary>Minimum log level name, e.g. <c>Information</c>.</summary>
    public string LogLevel { get; set; } = "Information";

    /// <summary>Secret reveal policy.</summary>
    public SecretRevealPolicy RevealPolicy { get; set; } = new();

    /// <summary>Paths of the key folders that are switched on.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> EnabledKeyDirectories =>
        [.. KeyFolders.Where(IsUsable).Select(f => f.Path)];

    /// <summary>Paths of the enabled scan roots that are searched recursively.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> EnabledRecursiveScanRoots =>
        [.. ScanRoots.Where(r => IsUsable(r) && r.Depth == ScanDepth.Recursive).Select(r => r.Path)];

    /// <summary>Paths of the enabled scan roots searched only one level deep.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> EnabledTopLevelScanRoots =>
        [.. ScanRoots.Where(r => IsUsable(r) && r.Depth == ScanDepth.TopLevel).Select(r => r.Path)];

    /// <summary>True when no scan root is configured at all.</summary>
    [JsonIgnore]
    public bool HasNoScanRoots => ScanRoots.Count == 0;

    /// <summary>Creates an independent copy.</summary>
    /// <returns>A deep copy of these settings.</returns>
    public AppSettings Clone() => new()
    {
        Language = Language,
        Theme = Theme,
        CustomKeyDirectories = [.. CustomKeyDirectories],
        RepositoryScanRoots = [.. RepositoryScanRoots],
        ScanRoots = [.. ScanRoots.Select(r => r.Clone())],
        KeyFolders = [.. KeyFolders.Select(f => f.Clone())],
        DryRunByDefault = DryRunByDefault,
        WatchForChanges = WatchForChanges,
        LogLevel = LogLevel,
        RevealPolicy = RevealPolicy with { },
    };

    /// <summary>
    /// Folds entries written by an older build into the structured lists.
    /// </summary>
    /// <remarks>
    /// A bare path carries no depth or enabled state, so it becomes an enabled recursive root and
    /// an enabled private-and-public key folder — which is exactly how the older build treated
    /// it. Migration is idempotent: a path already present in the structured list is left alone,
    /// keeping whatever the user has since configured for it.
    /// </remarks>
    /// <returns><see langword="true"/> when something was migrated.</returns>
    public bool MigrateLegacyEntries()
    {
        var migrated = false;

        foreach (var path in RepositoryScanRoots.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            if (!ScanRoots.Any(r => PathsMatch(r.Path, path)))
            {
                ScanRoots.Add(new ScanRoot { Path = path });
            }

            migrated = true;
        }

        foreach (var path in CustomKeyDirectories.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            if (!KeyFolders.Any(f => PathsMatch(f.Path, path)))
            {
                KeyFolders.Add(new KeyFolder { Path = path });
            }

            migrated = true;
        }

        RepositoryScanRoots.Clear();
        CustomKeyDirectories.Clear();

        return migrated;
    }

    private static bool IsUsable(ScanRoot root) => root.Enabled && !string.IsNullOrWhiteSpace(root.Path);

    private static bool IsUsable(KeyFolder folder) => folder.Enabled && !string.IsNullOrWhiteSpace(folder.Path);

    private static bool PathsMatch(string left, string right) =>
        string.Equals(left.TrimEnd('/', '\\'), right.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);
}

/// <summary>Source-generated JSON context for settings persistence.</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
public sealed partial class AppSettingsJsonContext : JsonSerializerContext;
