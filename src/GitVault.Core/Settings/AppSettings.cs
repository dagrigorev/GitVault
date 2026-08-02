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

    /// <summary>Extra directories to scan for SSH keys.</summary>
    public List<string> CustomKeyDirectories { get; set; } = [];

    /// <summary>Root folders scanned recursively for repositories.</summary>
    public List<string> RepositoryScanRoots { get; set; } = [];

    /// <summary>When true, the first activation of a session is always a dry run.</summary>
    public bool DryRunByDefault { get; set; } = true;

    /// <summary>When true, a filesystem watcher triggers a debounced rescan.</summary>
    public bool WatchForChanges { get; set; } = true;

    /// <summary>Minimum log level name, e.g. <c>Information</c>.</summary>
    public string LogLevel { get; set; } = "Information";

    /// <summary>Secret reveal policy.</summary>
    public SecretRevealPolicy RevealPolicy { get; set; } = new();

    /// <summary>Creates an independent copy.</summary>
    /// <returns>A deep copy of these settings.</returns>
    public AppSettings Clone() => new()
    {
        Language = Language,
        Theme = Theme,
        CustomKeyDirectories = [.. CustomKeyDirectories],
        RepositoryScanRoots = [.. RepositoryScanRoots],
        DryRunByDefault = DryRunByDefault,
        WatchForChanges = WatchForChanges,
        LogLevel = LogLevel,
        RevealPolicy = RevealPolicy with { },
    };
}

/// <summary>Source-generated JSON context for settings persistence.</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
public sealed partial class AppSettingsJsonContext : JsonSerializerContext;
