using GitVault.App.Markup;
using GitVault.Core.Settings;
using GitVault.Localization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GitVault.App.Tests;

/// <summary>
/// Settings that live only in memory. The real service writes to the user's profile, and a test
/// run must never change the language or the reveal policy of whoever is running it.
/// </summary>
internal sealed class InMemorySettingsService : ISettingsService
{
    private AppSettings _current = new();

    public AppSettings Current => _current;

    public string SettingsFilePath => string.Empty;

    public event EventHandler<AppSettings>? SettingsChanged;

    public Task<AppSettings> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(_current);

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        _current = settings.Clone();
        SettingsChanged?.Invoke(this, _current);
        return Task.CompletedTask;
    }
}

/// <summary>Builds a service provider for the headless tests.</summary>
internal static class TestServices
{
    /// <summary>
    /// Registers the application's real services, then swaps in the isolated settings store and
    /// publishes the localizer the <c>{loc:Tr}</c> markup extension binds to.
    /// </summary>
    /// <param name="customize">Optional hook to replace further registrations.</param>
    /// <returns>A provider the caller owns and must dispose.</returns>
    internal static ServiceProvider Build(Action<IServiceCollection>? customize = null)
    {
        var services = new ServiceCollection();
        Program.ConfigureServicesForTests(services);

        services.RemoveAll<ISettingsService>();
        services.AddSingleton<ISettingsService, InMemorySettingsService>();

        customize?.Invoke(services);

        var provider = services.BuildServiceProvider();
        LocalizationHost.Current = provider.GetRequiredService<Localizer>();
        return provider;
    }
}
