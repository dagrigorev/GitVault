using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using GitVault.App.Markup;
using GitVault.App.Services;
using GitVault.App.ViewModels;
using GitVault.App.Views;
using GitVault.Core.Settings;
using GitVault.Localization;
using Microsoft.Extensions.DependencyInjection;

namespace GitVault.App;

/// <summary>Avalonia application object. Owns the service provider and the theme variant.</summary>
internal sealed class App : Application
{
    private IServiceProvider? _services;

    /// <summary>Creates an application that resolves its views from <paramref name="services"/>.</summary>
    /// <param name="services">Composition root. May be null in design-time tooling.</param>
    public App(IServiceProvider? services) => _services = services;

    /// <summary>Parameterless constructor required by the XAML previewer and headless tests.</summary>
    public App()
    {
    }

    /// <summary>Replaces the service provider. Used by the headless test host.</summary>
    /// <param name="services">Composition root.</param>
    internal void UseServices(IServiceProvider services) => _services = services;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        InstallDispatcherExceptionHandler();

        if (_services is not null)
        {
            LocalizationHost.Current = _services.GetRequiredService<Localizer>();
            ApplyTheme(_services.GetRequiredService<ISettingsService>().Current.Theme);
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && _services is not null)
        {
            var shell = _services.GetRequiredService<MainWindowViewModel>();
            var settings = _services.GetRequiredService<SettingsViewModel>();
            settings.ThemeChangeRequested += (_, preference) => ApplyTheme(preference);

            var window = new MainWindow { DataContext = shell };
            _services.GetRequiredService<ClipboardService>().Attach(window);

            desktop.MainWindow = window;
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;

            // Scan once the window exists, so the first frame is not blocked on probes.
            _ = shell.RescanCommand.ExecuteAsync(null);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Keeps a failed page from taking the window down.
    /// </summary>
    /// <remarks>
    /// This must happen here rather than in <c>Program.Main</c>. Touching
    /// <see cref="Avalonia.Threading.Dispatcher.UIThread"/> before the platform is initialised
    /// creates the dispatcher without a controlled loop implementation, and Avalonia then reuses
    /// it — <c>Dispatcher.MainLoop</c> throws and the window is created but never painted.
    /// </remarks>
    private static void InstallDispatcherExceptionHandler() =>
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Serilog.Log.Error(e.Exception, "Unhandled exception on {Source}", "Dispatcher");
            e.Handled = true;
        };

    /// <summary>Applies a theme preference to the running application.</summary>
    /// <param name="preference">Theme the user selected.</param>
    internal void ApplyTheme(ThemePreference preference) => RequestedThemeVariant = preference switch
    {
        ThemePreference.Light => ThemeVariant.Light,
        ThemePreference.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };
}
