using Avalonia.Headless.XUnit;
using FluentAssertions;
using GitVault.App.Services;
using GitVault.App.ViewModels;
using GitVault.Core.Models;
using GitVault.Core.Profiles;
using GitVault.Core.Repository;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GitVault.App.Tests;

/// <summary>
/// The repository subtree, and the pages that only mean anything inside one.
/// </summary>
/// <remarks>
/// The tree shares a single view-model instance per page type across every repository, so the
/// thing worth testing is that the context is set before the page is shown. A page rendering the
/// previous repository's configuration would be wrong in the most expensive way available: the
/// user would be editing one repository while looking at another.
/// </remarks>
public sealed class RepositoryContextTests
{
    [AvaloniaFact]
    public void Repositories_appear_as_nodes_with_their_own_pages_beneath_them()
    {
        using var provider = TestServices.Build();
        var shell = provider.GetRequiredService<MainWindowViewModel>();
        var repositories = provider.GetRequiredService<RepositoriesViewModel>();

        repositories.Rows.Add(Row(provider, "alpha", "/src/alpha"));
        repositories.Rows.Add(Row(provider, "beta", "/src/beta"));

        shell.RebuildRepositoryNodes();

        var parent = shell.RootNodes[0].Children.Single(n => n.Page is RepositoriesViewModel);

        parent.Children.Select(n => n.Caption).Should().Equal("alpha", "beta");
        parent.Children[0].Children.Select(n => n.Page)
            .Should().AllSatisfy(p => p.Should().NotBeNull())
            .And.HaveCount(7);

        parent.Children[0].Children.Should().Contain(n => n.Page is CommitsViewModel);
        parent.Children[0].Children.Should().Contain(n => n.Page is HistoryToolsViewModel);
        parent.Children[0].Children.Should().Contain(n => n.Page is RemotesViewModel);
        parent.Children[0].Children.Should().Contain(n => n.Page is BranchesViewModel);
        parent.Children[0].Children.Should().Contain(n => n.Page is TagsViewModel);
        parent.Children[0].Children.Should().Contain(n => n.Page is RepositoryConfigViewModel);
        parent.Children[0].Children.Should().Contain(n => n.Page is ProjectSettingsViewModel);
    }

    [AvaloniaFact]
    public void A_repository_name_is_shown_verbatim_rather_than_translated()
    {
        using var provider = TestServices.Build();
        var shell = provider.GetRequiredService<MainWindowViewModel>();
        var repositories = provider.GetRequiredService<RepositoriesViewModel>();

        // A repository called after a resource key must still read as itself.
        repositories.Rows.Add(Row(provider, "Nav_Logs", "/src/oddly-named"));
        shell.RebuildRepositoryNodes();

        var parent = shell.RootNodes[0].Children.Single(n => n.Page is RepositoriesViewModel);
        parent.Children.Single().Caption.Should().Be("Nav_Logs");
    }

    [AvaloniaFact]
    public void Selecting_a_repository_page_sets_the_context_before_the_page_is_shown()
    {
        using var provider = TestServices.Build();
        var shell = provider.GetRequiredService<MainWindowViewModel>();
        var repositories = provider.GetRequiredService<RepositoriesViewModel>();
        var context = provider.GetRequiredService<RepositoryContext>();

        repositories.Rows.Add(Row(provider, "alpha", "/src/alpha"));
        repositories.Rows.Add(Row(provider, "beta", "/src/beta"));
        shell.RebuildRepositoryNodes();

        var parent = shell.RootNodes[0].Children.Single(n => n.Page is RepositoriesViewModel);
        var betaConfig = parent.Children
            .Single(n => n.Caption == "beta")
            .Children.Single(n => n.Page is RepositoryConfigViewModel);

        shell.SelectedNode = betaConfig;

        context.CurrentPath.Should().Be("/src/beta");
        context.CurrentName.Should().Be("beta");
        shell.SelectedPage.Should().BeOfType<RepositoryConfigViewModel>();
    }

    [AvaloniaFact]
    public void Switching_repositories_moves_the_context_with_the_selection()
    {
        using var provider = TestServices.Build();
        var shell = provider.GetRequiredService<MainWindowViewModel>();
        var repositories = provider.GetRequiredService<RepositoriesViewModel>();
        var context = provider.GetRequiredService<RepositoryContext>();

        repositories.Rows.Add(Row(provider, "alpha", "/src/alpha"));
        repositories.Rows.Add(Row(provider, "beta", "/src/beta"));
        shell.RebuildRepositoryNodes();

        var parent = shell.RootNodes[0].Children.Single(n => n.Page is RepositoriesViewModel);

        foreach (var name in new[] { "alpha", "beta", "alpha" })
        {
            shell.SelectedNode = parent.Children
                .Single(n => n.Caption == name)
                .Children.Single(n => n.Page is ProjectSettingsViewModel);

            context.CurrentPath.Should().Be("/src/" + name);
        }
    }

    [AvaloniaFact]
    public void With_no_repository_selected_the_project_page_says_so_rather_than_showing_blanks()
    {
        using var provider = TestServices.Build();
        var page = provider.GetRequiredService<ProjectSettingsViewModel>();

        page.HasRepository.Should().BeFalse();
        page.NoRepositoryCaption.Should().NotBeEmpty();
        page.Properties.Should().BeEmpty("there is nothing to describe");
    }

    [AvaloniaFact]
    public async Task Saving_project_settings_is_previewed_and_cancelling_writes_nothing()
    {
        using var provider = TestServices.Build(services =>
        {
            services.AddSingleton<RecordingProjectSettingsStore>();
            services.AddSingleton<IProjectSettingsStore>(sp => sp.GetRequiredService<RecordingProjectSettingsStore>());
        });

        var dialogs = provider.GetRequiredService<FakeDialogService>();
        var store = provider.GetRequiredService<RecordingProjectSettingsStore>();
        var context = provider.GetRequiredService<RepositoryContext>();
        var page = provider.GetRequiredService<ProjectSettingsViewModel>();

        context.Select("/src/alpha", "alpha");
        await page.ReloadAsync(CancellationToken.None);

        dialogs.Answer = false;
        await page.SaveCommand.ExecuteAsync(CancellationToken.None);

        dialogs.ShownOfType<OperationReviewViewModel>().Should().ContainSingle(
            "saving GitVault's own settings is previewed like any other write");

        store.PlannedSaves.Should().ContainSingle("a plan was built");
        store.Applied.Should().BeEmpty("cancelling the preview writes nothing");
    }

    [AvaloniaFact]
    public void The_project_page_states_where_the_settings_are_kept()
    {
        using var provider = TestServices.Build();
        var page = provider.GetRequiredService<ProjectSettingsViewModel>();

        // Writing into a repository's own configuration is a posture the user should be told
        // about rather than left to discover.
        page.StorageNoteCaption.Should().Contain("[gitvault]");
        page.StorageNoteCaption.Should().Contain(".git/config");
    }

    private static RepositoryRow Row(IServiceProvider provider, string name, string path) =>
        new(provider.GetRequiredService<GitVault.Localization.Localizer>(), new DiscoveredRepository(path, name));
}

/// <summary>A project settings store that records what it was asked to plan, and writes nothing.</summary>
internal sealed class RecordingProjectSettingsStore : IProjectSettingsStore
{
    /// <summary>Settings a save was planned for.</summary>
    public List<ProjectSettings> PlannedSaves { get; } = [];

    /// <summary>Plans that were actually applied. Never populated by the store itself.</summary>
    public List<GitOperationPlan> Applied { get; } = [];

    public Task<ProjectSettings> LoadAsync(string repositoryPath, CancellationToken cancellationToken) =>
        Task.FromResult(new ProjectSettings(repositoryPath));

    public Task<GitOperationPlan> PlanSaveAsync(ProjectSettings settings, CancellationToken cancellationToken)
    {
        PlannedSaves.Add(settings);
        return Task.FromResult(Plan(settings.RepositoryPath, ProjectSettingsStore.SaveOperationId));
    }

    public Task<GitOperationPlan> PlanClearAsync(string repositoryPath, CancellationToken cancellationToken) =>
        Task.FromResult(Plan(repositoryPath, ProjectSettingsStore.ClearOperationId));

    private static GitOperationPlan Plan(string repositoryPath, string operationId) =>
        new(operationId, GitConfigScope.Local, repositoryPath)
        {
            Changes =
            [
                new GitVault.Core.Profiles.PlannedChange(
                    ConfigEditor.ConfigStepId,
                    GitVault.Core.Profiles.ChangeKind.GitConfigSet,
                    "gitvault.profilename",
                    null,
                    "Work"),
            ],
            FilesToSnapshot = [Path.Combine(repositoryPath, ".git", "config")],
        };
}
