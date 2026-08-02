using FluentAssertions;
using GitVault.Core.Abstractions;
using GitVault.Core.Settings;
using NSubstitute;
using Xunit;

namespace GitVault.Core.Tests;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "gitvault-tests", Guid.NewGuid().ToString("N"));

    private readonly IPlatformPaths _paths = Substitute.For<IPlatformPaths>();

    public SettingsServiceTests()
    {
        Directory.CreateDirectory(_directory);
        _paths.AppDataDirectory.Returns(_directory);
    }

    [Fact]
    public async Task Missing_file_yields_defaults()
    {
        var service = new SettingsService(_paths);

        var settings = await service.LoadAsync(CancellationToken.None);

        settings.Language.Should().Be("en-US");
        settings.DryRunByDefault.Should().BeTrue();
        settings.RevealPolicy.AutoHideSeconds.Should().Be(30);
    }

    [Fact]
    public async Task Saved_settings_round_trip()
    {
        var service = new SettingsService(_paths);
        var settings = new AppSettings
        {
            Language = "ru-RU",
            Theme = ThemePreference.Dark,
            DryRunByDefault = false,
            CustomKeyDirectories = ["/home/user/keys"],
        };

        await service.SaveAsync(settings, CancellationToken.None);
        var reloaded = await new SettingsService(_paths).LoadAsync(CancellationToken.None);

        reloaded.Language.Should().Be("ru-RU");
        reloaded.Theme.Should().Be(ThemePreference.Dark);
        reloaded.DryRunByDefault.Should().BeFalse();
        reloaded.CustomKeyDirectories.Should().ContainSingle().Which.Should().Be("/home/user/keys");
    }

    [Fact]
    public async Task Corrupt_file_falls_back_to_defaults_without_throwing()
    {
        await File.WriteAllTextAsync(Path.Combine(_directory, "settings.json"), "{ this is not json", CancellationToken.None);
        var service = new SettingsService(_paths);

        var settings = await service.LoadAsync(CancellationToken.None);

        settings.Language.Should().Be("en-US");
    }

    [Fact]
    public async Task Save_raises_the_changed_event_with_an_independent_copy()
    {
        var service = new SettingsService(_paths);
        AppSettings? observed = null;
        service.SettingsChanged += (_, s) => observed = s;

        var settings = new AppSettings { Language = "zh-Hans" };
        await service.SaveAsync(settings, CancellationToken.None);

        observed.Should().NotBeNull();
        observed!.Language.Should().Be("zh-Hans");
        observed.Should().NotBeSameAs(settings, "the service must not hand out the caller's instance");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test run over.
        }
    }
}
