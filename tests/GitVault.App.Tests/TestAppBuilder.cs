using Avalonia;
using Avalonia.Headless;
using GitVault.App;
using GitVault.App.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace GitVault.App.Tests;

/// <summary>Builds the real application on the headless platform for UI tests.</summary>
public static class TestAppBuilder
{
    /// <summary>Configures the headless Avalonia app.</summary>
    /// <returns>An app builder the xUnit adapter drives.</returns>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });
}
