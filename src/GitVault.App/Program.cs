using Avalonia;
using Avalonia.Threading;
using GitVault.App.Composition;
using GitVault.App.Logging;
using GitVault.Core.Abstractions;
using GitVault.Core.Platform;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Events;

namespace GitVault.App;

/// <summary>Process entry point and composition root bootstrap.</summary>
internal static class Program
{
    private static IServiceProvider? _services;

    /// <summary>Entry point.</summary>
    /// <param name="args">Command line arguments, forwarded to Avalonia.</param>
    /// <returns>Zero on a clean exit, one when startup failed.</returns>
    [STAThread]
    internal static int Main(string[] args)
    {
        try
        {
            _services = BuildServices(DataRootFrom(args));
            _services.InitializeGitVaultAsync(CancellationToken.None).GetAwaiter().GetResult();

            ConfigureLogging(_services);
            InstallGlobalExceptionHandlers();

            Log.Information("GitVault {Version} starting on {Os}",
                typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                _services.GetRequiredService<IPlatformInfo>().OsDescription);

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception ex)
        {
            // Startup failed before the UI existed: there is nowhere to show a dialog.
            Log.Fatal(ex, "GitVault failed to start");
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
            (_services as IDisposable)?.Dispose();
        }
    }

    /// <summary>Builds the Avalonia application. Also used by the headless test host.</summary>
    /// <returns>A configured app builder.</returns>
    internal static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure(() => new App(_services))
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>Builds the service provider.</summary>
    /// <returns>The composition root.</returns>
    internal static ServiceProvider BuildServices(string? dataRoot = null) =>
        new ServiceCollection().AddGitVault(dataRoot).BuildServiceProvider();

    /// <summary>
    /// Reads <c>--data-root &lt;path&gt;</c>, which moves everything GitVault reads and writes.
    /// </summary>
    /// <remarks>
    /// Two jobs need this and neither has another way to get it. Screenshots for documentation
    /// have to contain nobody's real identity, keys or repository paths. And the manual test plan
    /// asks a person to exercise operations that rewrite history and delete refs, which on Windows
    /// they cannot sandbox by redirecting the environment — the profile and application-data
    /// folders come from the operating system, not from variables.
    ///
    /// It is a switch rather than a setting on purpose: something that changes where the
    /// application's own settings live cannot be read from those settings.
    /// </remarks>
    /// <param name="args">Command line arguments.</param>
    /// <returns>The requested root, or null to use the real one.</returns>
    private static string? DataRootFrom(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--data-root", StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    /// <summary>
    /// Registers the same services as <see cref="BuildServices"/> without building the provider,
    /// so a test can swap an implementation before the container is created.
    /// </summary>
    /// <param name="services">Collection to populate.</param>
    internal static void ConfigureServicesForTests(IServiceCollection services) => services.AddGitVault();

    /// <summary>
    /// Wires Serilog to a rolling file and to the in-app viewer, with the secret-redacting
    /// enricher in front of both sinks.
    /// </summary>
    /// <param name="services">Composition root.</param>
    internal static void ConfigureLogging(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var paths = services.GetRequiredService<PlatformPathsBase>();
        var redactor = services.GetRequiredService<ISecretRedactor>();
        var sink = services.GetRequiredService<InMemoryLogSink>();
        var level = ParseLevel(services.GetRequiredService<Core.Settings.ISettingsService>().Current.LogLevel);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .Enrich.With(new SecretRedactingEnricher(redactor))
            .WriteTo.Sink(sink)
            .WriteTo.File(
                Path.Combine(paths.LogDirectory, "gitvault-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: false,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    private static LogEventLevel ParseLevel(string name) =>
        Enum.TryParse<LogEventLevel>(name, ignoreCase: true, out var parsed)
            ? parsed
            : LogEventLevel.Information;

    /// <summary>
    /// Installs the handlers that do not depend on Avalonia being initialised.
    /// </summary>
    /// <remarks>
    /// The dispatcher handler is deliberately **not** installed here. Touching
    /// <see cref="Dispatcher.UIThread"/> before <c>UsePlatformDetect</c> runs creates the
    /// dispatcher with the default platform implementation, which is not a controlled loop;
    /// Avalonia then reuses that instance and <c>Dispatcher.MainLoop</c> throws
    /// <see cref="PlatformNotSupportedException"/>. The window is created but never painted.
    ///
    /// It is installed from <c>App.OnFrameworkInitializationCompleted</c> instead, once the real
    /// dispatcher exists.
    /// </remarks>
    private static void InstallGlobalExceptionHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception on {Source}", "AppDomain");

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "Unhandled exception on {Source}", "TaskScheduler");
            e.SetObserved();
        };
    }
}
