using System.Globalization;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using GitVault.App.Markup;
using GitVault.App.Services;
using GitVault.App.ViewModels;
using GitVault.App.Views;
using GitVault.Core.Abstractions;
using GitVault.Core.Diagnostics;
using GitVault.Core.Discovery;
using GitVault.Core.Models;
using GitVault.Localization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace GitVault.App.Tests;

/// <summary>Rescan wiring: one scan updates every page that observes the coordinator.</summary>
public sealed class ScanFlowTests
{
    private sealed class StubProbe(ProbePayload payload) : IProbe
    {
        public string ProbeId => "stub";

        public string DisplayName => "Stub";

        public bool IsSupportedOnThisPlatform => true;

        public Task<ProbeResult<ProbePayload>> ProbeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(ProbeResult<ProbePayload>.Ok(ProbeId, payload));
    }

    private static ServiceProvider BuildProvider(ProbePayload payload) => TestServices.Build(services =>
    {
        // Replace the real probe set with a deterministic one, so the assertions do not depend
        // on what happens to be installed on the machine running the tests.
        services.RemoveAll<IProbe>();
        services.AddSingleton<IProbe>(new StubProbe(payload));
    });

    private static ProbePayload SamplePayload() => new()
    {
        Identities =
        [
            GitIdentity.Create("Ada Lovelace", "ada@example.com", IdentitySource.GitGlobalConfig,
                "/home/ada/.gitconfig", hosts: ["github.com"]),
            GitIdentity.Create("Ada Work", "ada@work.example", IdentitySource.RepoLocal,
                "/repo/.git/config"),
        ],
        Warnings = [new KeyWarning(GitIdentityProbe.NoIdentityConfiguredCode, WarningSeverity.Medium, "/x")],
    };

    [AvaloniaFact]
    public async Task Rescan_fills_the_dashboard_and_the_identities_grid()
    {
        using var provider = BuildProvider(SamplePayload());
        // The window is deliberately not shown: the headless platform has no font for the
        // DataGrid's glyphs, and this test is about the scan wiring, not about rendering.
        var dashboard = provider.GetRequiredService<DashboardViewModel>();
        var identities = provider.GetRequiredService<IdentitiesViewModel>();

        await provider.GetRequiredService<ScanCoordinator>().RescanAsync(CancellationToken.None);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        dashboard.Cards[0].Count.Should().Be(2);
        dashboard.HasNoScan.Should().BeFalse();
        dashboard.Warnings.Should().ContainSingle();
        identities.Rows.Should().HaveCount(2);
        identities.IsEmpty.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task Warning_text_is_localized_from_the_warning_code()
    {
        using var provider = BuildProvider(SamplePayload());
        var localization = provider.GetRequiredService<ILocalizationService>();
        var dashboard = provider.GetRequiredService<DashboardViewModel>();

        await provider.GetRequiredService<ScanCoordinator>().RescanAsync(CancellationToken.None);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        localization.SetCulture(CultureInfo.GetCultureInfo("en-US"));
        dashboard.Warnings[0].Title.Should().Be("No author identity configured");

        localization.SetCulture(CultureInfo.GetCultureInfo("ru-RU"));
        dashboard.Warnings[0].Title.Should().Be("Идентичность автора не настроена");

        localization.SetCulture(CultureInfo.GetCultureInfo("zh-Hans"));
        dashboard.Warnings[0].Title.Should().Be("未配置作者身份");
    }

    [AvaloniaFact]
    public async Task Identity_rows_localize_their_source_and_confidence_labels()
    {
        using var provider = BuildProvider(SamplePayload());
        var localization = provider.GetRequiredService<ILocalizationService>();
        var identities = provider.GetRequiredService<IdentitiesViewModel>();

        await provider.GetRequiredService<ScanCoordinator>().RescanAsync(CancellationToken.None);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        localization.SetCulture(CultureInfo.GetCultureInfo("ru-RU"));
        var row = identities.Rows.Single(r => r.Email == "ada@example.com");

        row.Source.Should().Be("Пользовательская конфигурация Git");
        row.Confidence.Should().Be("Точно");
        row.Hosts.Should().Be("github.com");
    }

    [AvaloniaFact]
    public async Task An_empty_scan_leaves_the_grid_in_its_empty_state()
    {
        using var provider = BuildProvider(ProbePayload.Empty);
        var identities = provider.GetRequiredService<IdentitiesViewModel>();

        await provider.GetRequiredService<ScanCoordinator>().RescanAsync(CancellationToken.None);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        identities.Rows.Should().BeEmpty();
        identities.IsEmpty.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task The_shell_reports_when_a_scan_is_running()
    {
        using var provider = BuildProvider(SamplePayload());
        var shell = provider.GetRequiredService<MainWindowViewModel>();

        shell.IsScanning.Should().BeFalse();
        await shell.RescanCommand.ExecuteAsync(null);
        shell.IsScanning.Should().BeFalse("the flag must be cleared once the scan finishes");
    }
}
