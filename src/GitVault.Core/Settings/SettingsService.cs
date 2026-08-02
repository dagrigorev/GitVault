using System.Text.Json;
using GitVault.Core.Abstractions;

namespace GitVault.Core.Settings;

/// <summary>Loads and saves <see cref="AppSettings"/>.</summary>
public interface ISettingsService
{
    /// <summary>The settings currently in effect.</summary>
    AppSettings Current { get; }

    /// <summary>Absolute path of the settings file.</summary>
    string SettingsFilePath { get; }

    /// <summary>Raised after <see cref="Current"/> has been replaced.</summary>
    event EventHandler<AppSettings>? SettingsChanged;

    /// <summary>Reads settings from disk, falling back to defaults when absent or unreadable.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The loaded settings.</returns>
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Persists settings and raises <see cref="SettingsChanged"/>.</summary>
    /// <param name="settings">Settings to persist.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes once the file has been written.</returns>
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken);
}

/// <summary>JSON-file-backed settings store under the platform's app data directory.</summary>
public sealed class SettingsService : ISettingsService
{
    private readonly object _gate = new();
    private AppSettings _current = new();

    /// <summary>Creates the service.</summary>
    /// <param name="paths">Platform paths used to locate the settings file.</param>
    public SettingsService(IPlatformPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        SettingsFilePath = Path.Combine(paths.AppDataDirectory, "settings.json");
    }

    /// <inheritdoc/>
    public AppSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <inheritdoc/>
    public string SettingsFilePath { get; }

    /// <inheritdoc/>
    public event EventHandler<AppSettings>? SettingsChanged;

    /// <inheritdoc/>
    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken)
    {
        AppSettings loaded;
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                await using var stream = File.OpenRead(SettingsFilePath);
                loaded = await JsonSerializer
                    .DeserializeAsync(stream, AppSettingsJsonContext.Default.AppSettings, cancellationToken)
                    .ConfigureAwait(false) ?? new AppSettings();
            }
            else
            {
                loaded = new AppSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A corrupt or unreadable settings file must never stop the app from starting.
            loaded = new AppSettings();
        }

        lock (_gate)
        {
            _current = loaded;
        }

        SettingsChanged?.Invoke(this, loaded);
        return loaded;
    }

    /// <inheritdoc/>
    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var copy = settings.Clone();
        var directory = Path.GetDirectoryName(SettingsFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = SettingsFilePath + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer
                .SerializeAsync(stream, copy, AppSettingsJsonContext.Default.AppSettings, cancellationToken)
                .ConfigureAwait(false);
        }

        File.Move(temp, SettingsFilePath, overwrite: true);

        lock (_gate)
        {
            _current = copy;
        }

        SettingsChanged?.Invoke(this, copy);
    }
}
