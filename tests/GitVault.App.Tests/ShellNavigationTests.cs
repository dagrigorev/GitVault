using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using GitVault.App.Markup;
using GitVault.App.ViewModels;
using GitVault.App.Views;
using GitVault.Localization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GitVault.App.Tests;

/// <summary>Headless checks of the shell: navigation, view resolution and live language switching.</summary>
public sealed class ShellNavigationTests
{
    private static ServiceProvider BuildProvider() => TestServices.Build();

    [AvaloniaFact]
    public void Every_page_hangs_off_the_machine_in_the_navigation_tree()
    {
        using var provider = BuildProvider();
        var shell = provider.GetRequiredService<MainWindowViewModel>();

        var window = new MainWindow { DataContext = shell };
        window.Show();

        var tree = window.FindControl<TreeView>("NavigationTree");
        tree.Should().NotBeNull();

        shell.RootNodes.Should().ContainSingle("the tree has one root: this computer");
        shell.RootNodes[0].IsRoot.Should().BeTrue();
        shell.RootNodes[0].Children.Should().HaveCount(11);
        shell.SelectedPage.Should().BeOfType<DashboardViewModel>();
    }

    [AvaloniaFact]
    public void Selecting_a_tree_node_changes_the_page()
    {
        using var provider = BuildProvider();
        var shell = provider.GetRequiredService<MainWindowViewModel>();

        var window = new MainWindow { DataContext = shell };
        window.Show();

        var node = shell.RootNodes[0].Children.Single(n => n.Page is SshKeysViewModel);
        shell.SelectedNode = node;

        shell.SelectedPage.Should().BeOfType<SshKeysViewModel>();
    }

    [AvaloniaFact]
    public void Selecting_the_root_is_not_a_navigation()
    {
        // The machine is a heading. Clicking it must not blank the workspace.
        using var provider = BuildProvider();
        var shell = provider.GetRequiredService<MainWindowViewModel>();

        var window = new MainWindow { DataContext = shell };
        window.Show();

        shell.Navigate<LogsViewModel>();
        shell.SelectedNode = shell.RootNodes[0];

        shell.SelectedPage.Should().BeOfType<LogsViewModel>();
    }

    [AvaloniaFact]
    public void Navigating_from_a_menu_moves_the_tree_selection_too()
    {
        using var provider = BuildProvider();
        var shell = provider.GetRequiredService<MainWindowViewModel>();

        var window = new MainWindow { DataContext = shell };
        window.Show();

        shell.Navigate<SnapshotsViewModel>();

        shell.SelectedNode!.Page.Should().BeOfType<SnapshotsViewModel>();
    }

    [AvaloniaFact]
    public void Selecting_a_page_resolves_its_view()
    {
        using var provider = BuildProvider();
        var shell = provider.GetRequiredService<MainWindowViewModel>();

        var window = new MainWindow { DataContext = shell };
        window.Show();

        var locator = new ViewLocator();
        locator.Build(shell.Pages.OfType<SettingsViewModel>().Single()).Should().BeOfType<SettingsView>();
        locator.Build(shell.Pages.OfType<LogsViewModel>().Single()).Should().BeOfType<LogsView>();
        locator.Build(shell.Pages.OfType<IdentitiesViewModel>().Single()).Should().BeOfType<IdentitiesView>();
        locator.Build(shell.Pages.OfType<SshKeysViewModel>().Single()).Should().BeOfType<SshKeysView>();

        locator.Build(shell.Pages.OfType<AgentsViewModel>().Single()).Should().BeOfType<AgentsView>();
        locator.Build(shell.Pages.OfType<CredentialsViewModel>().Single()).Should().BeOfType<CredentialsView>();
        locator.Build(shell.Pages.OfType<ClientsViewModel>().Single()).Should().BeOfType<ClientsView>();
        locator.Build(shell.Pages.OfType<ProfilesViewModel>().Single()).Should().BeOfType<ProfilesView>();
        locator.Build(shell.Pages.OfType<RepositoriesViewModel>().Single()).Should().BeOfType<RepositoriesView>();
        locator.Build(shell.Pages.OfType<SnapshotsViewModel>().Single()).Should().BeOfType<SnapshotsView>();

        // The shared list view is still the fallback for a page with no view of its own.
        new ViewLocator().Match(shell.Pages[0]).Should().BeTrue();
    }

    [AvaloniaTheory]
    [InlineData("en-US", "Overview", "Options")]
    [InlineData("ru-RU", "Обзор", "Параметры")]
    [InlineData("zh-Hans", "概览", "选项")]
    public void Navigation_captions_follow_the_selected_language(
        string culture,
        string expectedFirst,
        string expectedSettings)
    {
        using var provider = BuildProvider();
        provider.GetRequiredService<ILocalizationService>().SetCulture(CultureInfo.GetCultureInfo(culture));

        var shell = provider.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = shell };
        window.Show();

        shell.Pages[0].NavCaption.Should().Be(expectedFirst);
        shell.Pages.OfType<SettingsViewModel>().Single().NavCaption.Should().Be(expectedSettings);
    }

    [AvaloniaFact]
    public void Switching_language_at_runtime_updates_captions_without_recreating_the_window()
    {
        using var provider = BuildProvider();
        var localization = provider.GetRequiredService<ILocalizationService>();
        var shell = provider.GetRequiredService<MainWindowViewModel>();

        var window = new MainWindow { DataContext = shell };
        window.Show();

        localization.SetCulture(CultureInfo.GetCultureInfo("en-US"));
        shell.Pages[0].Title.Should().Be("Overview");

        localization.SetCulture(CultureInfo.GetCultureInfo("ru-RU"));
        shell.Pages[0].Title.Should().Be("Обзор");

        localization.SetCulture(CultureInfo.GetCultureInfo("zh-Hans"));
        shell.Pages[0].Title.Should().Be("概览");
    }

    [AvaloniaFact]
    public void Language_picker_drives_the_whole_ui()
    {
        using var provider = BuildProvider();
        var settings = provider.GetRequiredService<SettingsViewModel>();
        var shell = provider.GetRequiredService<MainWindowViewModel>();

        var window = new MainWindow { DataContext = shell };
        window.Show();

        settings.SelectedLanguage = settings.Languages.Single(l => l.Culture.Name == "zh-Hans");

        shell.Pages[0].NavCaption.Should().Be("概览");
        settings.Themes[0].Label.Should().Be("跟随系统");
    }

    [AvaloniaFact]
    public void A_caption_written_in_xaml_retranslates_when_the_culture_changes()
    {
        // The view-model assertions elsewhere in this file all passed while every caption
        // written as {loc:Tr} stayed in English, because they never looked at a rendered
        // control. This one does.
        using var provider = BuildProvider();
        var localization = provider.GetRequiredService<ILocalizationService>();
        var shell = provider.GetRequiredService<MainWindowViewModel>();

        var window = new MainWindow { DataContext = shell };
        window.Show();

        var searchBox = window.FindControl<TextBox>("SearchBox");
        searchBox.Should().NotBeNull();

        localization.SetCulture(CultureInfo.GetCultureInfo("en-US"));
        searchBox!.Watermark.Should().Be("Filter current view");

        localization.SetCulture(CultureInfo.GetCultureInfo("ru-RU"));
        searchBox.Watermark.Should().Be("Фильтр текущего вида", "a {loc:Tr} binding must follow the culture");

        localization.SetCulture(CultureInfo.GetCultureInfo("zh-Hans"));
        searchBox.Watermark.Should().Be("筛选当前视图");
    }

    [AvaloniaFact]
    public void Constructing_the_settings_page_does_not_change_the_active_culture()
    {
        using var provider = BuildProvider();
        var localization = provider.GetRequiredService<ILocalizationService>();
        localization.SetCulture(CultureInfo.GetCultureInfo("ru-RU"));

        var settings = provider.GetRequiredService<SettingsViewModel>();

        localization.CurrentCulture.Name.Should().Be("ru-RU");
        settings.SelectedLanguage!.Culture.Name.Should().Be("ru-RU");
    }

    [AvaloniaFact]
    public void Dashboard_counts_are_pluralized_in_the_active_language()
    {
        using var provider = BuildProvider();
        var localization = provider.GetRequiredService<ILocalizationService>();
        var dashboard = provider.GetRequiredService<DashboardViewModel>();

        localization.SetCulture(CultureInfo.GetCultureInfo("ru-RU"));
        dashboard.Cards[1].Count = 2;
        dashboard.Cards[1].CountCaption.Should().Be("2 ключа");

        dashboard.Cards[1].Count = 5;
        dashboard.Cards[1].CountCaption.Should().Be("5 ключей");
    }
}
