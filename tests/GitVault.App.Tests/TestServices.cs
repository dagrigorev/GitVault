using GitVault.App.Markup;
using GitVault.Core.Abstractions;
using GitVault.App.Services;
using GitVault.App.ViewModels;
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

/// <summary>
/// A dialog service that answers instead of showing a window.
/// </summary>
/// <remarks>
/// This is what lets the tests assert the safety properties directly: that applying is impossible
/// until a plan dialog was confirmed, and that a destructive action asked first. Every dialog the
/// application raises is recorded, so a test can also assert that one was <em>not</em> shown.
/// </remarks>
internal sealed class FakeDialogService : IDialogService
{
    /// <summary>Every dialog that was shown, in order.</summary>
    public List<DialogViewModel> Shown { get; } = [];

    /// <summary>The answer given to a dialog when no handler is set.</summary>
    public bool Answer { get; set; }

    /// <summary>
    /// Called for each dialog as it is shown. Lets a test fill an editor in the way the user
    /// would and then decide whether to accept it, which is the only faithful way to exercise a
    /// modal editor without a window.
    /// </summary>
    public Func<DialogViewModel, bool>? Handler { get; set; }

    /// <summary>Folder the picker pretends the user chose.</summary>
    public string? FolderToPick { get; set; }

    /// <summary>Dialogs of a given type that were shown.</summary>
    /// <typeparam name="T">Dialog view model type.</typeparam>
    /// <returns>The matching dialogs.</returns>
    public IReadOnlyList<T> ShownOfType<T>()
        where T : DialogViewModel => [.. Shown.OfType<T>()];

    public Task<bool> ShowAsync(DialogViewModel dialog)
    {
        Shown.Add(dialog);

        var answer = Handler?.Invoke(dialog) ?? Answer;

        // A dialog that cannot be confirmed is answered "no" whatever the test asked for: that is
        // what the real window does, because its confirming button is disabled.
        return Task.FromResult(answer && dialog.CanConfirm);
    }

    public Task<string?> PickFolderAsync(string titleKey, string? startPath) => Task.FromResult(FolderToPick);
}

/// <summary>Builds a service provider for the headless tests.</summary>
internal static class TestServices
{
    /// <summary>
    /// Registers the application's real services, then swaps in the isolated settings store and
    /// the answering dialog service, and publishes the localizer the <c>{loc:Tr}</c> markup
    /// extension binds to.
    /// </summary>
    /// <param name="customize">Optional hook to replace further registrations.</param>
    /// <returns>A provider the caller owns and must dispose.</returns>
    internal static ServiceProvider Build(Action<IServiceCollection>? customize = null)
    {
        var services = new ServiceCollection();
        Program.ConfigureServicesForTests(services);

        services.RemoveAll<ISettingsService>();
        services.AddSingleton<ISettingsService, InMemorySettingsService>();

        // Redirect every path away from the developer's own profile. Profiles and snapshots are
        // written through IPlatformPaths, so without this a test that saves a profile would
        // rewrite the profiles.json of whoever is running the suite.
        services.RemoveAll<IPlatformPaths>();
        services.AddSingleton<IPlatformPaths>(_ => new TempPlatformPaths());

        // Real dialogs need a window; these tests answer them instead.
        services.RemoveAll<IDialogService>();
        services.AddSingleton<FakeDialogService>();
        services.AddSingleton<IDialogService>(sp => sp.GetRequiredService<FakeDialogService>());

        customize?.Invoke(services);

        var provider = services.BuildServiceProvider();
        LocalizationHost.Current = provider.GetRequiredService<Localizer>();
        return provider;
    }
}
