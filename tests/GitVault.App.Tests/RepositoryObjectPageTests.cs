using Avalonia.Headless.XUnit;
using FluentAssertions;
using GitVault.App.Services;
using GitVault.App.ViewModels;
using GitVault.Core.Repository;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GitVault.App.Tests;

/// <summary>
/// The remotes, branches and tags pages, at the level the user meets them.
/// </summary>
/// <remarks>
/// The engine's guarantees are covered in <c>GitVault.Core.Tests</c> against real repositories.
/// What is checked here is the interface in front of them: that every one of the three pages goes
/// through the review dialog, that closing it applies nothing, and that a blocked plan cannot be
/// confirmed at all. Three pages sharing one base class is exactly the arrangement where one of
/// them can quietly acquire a shortcut.
/// </remarks>
public sealed class RepositoryObjectPageTests
{
    [AvaloniaFact]
    public async Task Adding_a_remote_is_previewed_and_cancelling_applies_nothing()
    {
        using var provider = Build(out var editor);
        var dialogs = provider.GetRequiredService<FakeDialogService>();
        var page = provider.GetRequiredService<RemotesViewModel>();

        provider.GetRequiredService<RepositoryContext>().Select("/src/alpha", "alpha");

        dialogs.Handler = dialog =>
        {
            if (dialog is RemoteEditorViewModel remote)
            {
                remote.Name = "origin";
                remote.FetchUrl = "https://git.example.invalid/a.git";
                return true;
            }

            // The review dialog: close it.
            return false;
        };

        await page.AddCommand.ExecuteAsync(CancellationToken.None);

        dialogs.ShownOfType<RepositoryReviewViewModel>().Should().ContainSingle(
            "a remote change is previewed like every other write");

        editor.Applied.Should().BeEmpty("closing the preview must apply nothing");
    }

    [AvaloniaFact]
    public async Task Deleting_a_branch_is_previewed_and_cancelling_applies_nothing()
    {
        using var provider = Build(out var editor);
        var dialogs = provider.GetRequiredService<FakeDialogService>();
        var page = provider.GetRequiredService<BranchesViewModel>();

        provider.GetRequiredService<RepositoryContext>().Select("/src/alpha", "alpha");
        await page.ReloadAsync(CancellationToken.None);

        page.SelectedRow.Should().NotBeNull();

        dialogs.Answer = false;
        await page.DeleteCommand.ExecuteAsync(CancellationToken.None);

        dialogs.ShownOfType<RepositoryReviewViewModel>().Should().ContainSingle();
        editor.Applied.Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task Confirming_the_preview_is_what_applies_a_branch_deletion()
    {
        using var provider = Build(out var editor);
        var dialogs = provider.GetRequiredService<FakeDialogService>();
        var page = provider.GetRequiredService<BranchesViewModel>();

        provider.GetRequiredService<RepositoryContext>().Select("/src/alpha", "alpha");
        await page.ReloadAsync(CancellationToken.None);

        dialogs.Answer = true;
        await page.DeleteCommand.ExecuteAsync(CancellationToken.None);

        editor.Applied.Should().ContainSingle();
        editor.Applied[0].OperationId.Should().Be(GitObjectEditor.BranchOperationId);
    }

    [AvaloniaFact]
    public async Task A_blocked_plan_cannot_be_confirmed_even_when_the_user_says_yes()
    {
        using var provider = Build(out var editor);
        var dialogs = provider.GetRequiredService<FakeDialogService>();
        var page = provider.GetRequiredService<TagsViewModel>();

        provider.GetRequiredService<RepositoryContext>().Select("/src/alpha", "alpha");
        await page.ReloadAsync(CancellationToken.None);

        editor.BlockNextPlan = true;
        dialogs.Answer = true;

        await page.DeleteCommand.ExecuteAsync(CancellationToken.None);

        var review = dialogs.ShownOfType<RepositoryReviewViewModel>().Should().ContainSingle().Subject;
        review.CanConfirm.Should().BeFalse("a blocked plan offers no way to confirm it");
        review.HasBlockers.Should().BeTrue();

        editor.Applied.Should().BeEmpty();
    }

    [AvaloniaFact]
    public void A_warning_is_shown_as_a_warning_rather_than_a_refusal()
    {
        using var provider = Build(out _);
        var localizer = provider.GetRequiredService<GitVault.Localization.Localizer>();

        var plan = new RepositoryPlan(GitObjectEditor.BranchOperationId, "/src/alpha")
        {
            Changes =
            [
                new RepositoryChange(RepositoryChangeKind.BranchDelete, "work", "abc123", null, ["branch", "-D", "work"]),
            ],
            RefsToBackUp = ["refs/heads/work"],
            Warnings = [RepositoryWarnings.BranchNotMerged],
        };

        var review = new RepositoryReviewViewModel(localizer, plan);

        review.HasWarnings.Should().BeTrue();
        review.HasBlockers.Should().BeFalse();
        review.CanConfirm.Should().BeTrue("losing commits is the user's decision, not a refusal");

        review.Warnings.Single().Should().Be(localizer[RepositoryWarnings.BranchNotMerged],
            "an identifier must be rendered in the reader's language");

        review.HasBackup.Should().BeTrue();
        review.BackedUpRefs.Should().Contain("refs/heads/work");
    }

    private static ServiceProvider Build(out RecordingObjectEditor editor)
    {
        var recording = new RecordingObjectEditor();
        editor = recording;

        return TestServices.Build(services =>
        {
            services.AddSingleton<IGitObjectEditor>(recording);
            services.AddSingleton<IRepositoryInspector>(new StubInspector());
        });
    }
}

/// <summary>An inspector that reports one of everything, without a repository on disk.</summary>
internal sealed class StubInspector : IRepositoryInspector
{
    public Task<RepositoryState> GetStateAsync(string repositoryPath, CancellationToken cancellationToken) =>
        Task.FromResult(new RepositoryState(
            repositoryPath, "main", "abc123", false, false, RepositoryOperation.None));

    public Task<IReadOnlyList<GitRemote>> ListRemotesAsync(string repositoryPath, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<GitRemote>>(
            [new GitRemote("origin", "https://git.example.invalid/a.git", "https://git.example.invalid/a.git")]);

    public Task<IReadOnlyList<GitBranch>> ListBranchesAsync(string repositoryPath, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<GitBranch>>(
        [
            new GitBranch("main", "refs/heads/main", true, false, null, 0, 0, "abc123", "Initial commit"),
            new GitBranch("work", "refs/heads/work", false, false, null, 0, 0, "def456", "Work in progress"),
        ]);

    public Task<IReadOnlyList<GitTag>> ListTagsAsync(string repositoryPath, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<GitTag>>(
            [new GitTag("v1.0", "abc123", true, false, "First release", "QA")]);
}

/// <summary>An editor that records what it was asked to apply, and changes nothing.</summary>
internal sealed class RecordingObjectEditor : IGitObjectEditor
{
    /// <summary>Plans that reached <see cref="ApplyAsync"/> and were not refused.</summary>
    public List<RepositoryPlan> Applied { get; } = [];

    /// <summary>When true, the next plan built carries a blocker.</summary>
    public bool BlockNextPlan { get; set; }

    public Task<RepositoryPlan> PlanAddRemoteAsync(
        string repositoryPath, string name, string url, CancellationToken cancellationToken) =>
        Plan(GitObjectEditor.RemoteOperationId, repositoryPath, RepositoryChangeKind.RemoteAdd, name, null, url);

    public Task<RepositoryPlan> PlanRenameRemoteAsync(
        string repositoryPath, string oldName, string newName, CancellationToken cancellationToken) =>
        Plan(GitObjectEditor.RemoteOperationId, repositoryPath, RepositoryChangeKind.RemoteRename, oldName, oldName, newName);

    public Task<RepositoryPlan> PlanRemoveRemoteAsync(
        string repositoryPath, string name, CancellationToken cancellationToken) =>
        Plan(GitObjectEditor.RemoteOperationId, repositoryPath, RepositoryChangeKind.RemoteRemove, name, name, null);

    public Task<RepositoryPlan> PlanSetRemoteUrlAsync(
        string repositoryPath, string name, string fetchUrl, string? pushUrl, CancellationToken cancellationToken) =>
        Plan(GitObjectEditor.RemoteOperationId, repositoryPath, RepositoryChangeKind.RemoteSetUrl, name, null, fetchUrl);

    public Task<RepositoryPlan> PlanCreateBranchAsync(
        string repositoryPath, string name, string? startPoint, CancellationToken cancellationToken) =>
        Plan(GitObjectEditor.BranchOperationId, repositoryPath, RepositoryChangeKind.BranchCreate, name, null, "abc123");

    public Task<RepositoryPlan> PlanRenameBranchAsync(
        string repositoryPath, string oldName, string newName, CancellationToken cancellationToken) =>
        Plan(GitObjectEditor.BranchOperationId, repositoryPath, RepositoryChangeKind.BranchRename, oldName, oldName, newName);

    public Task<RepositoryPlan> PlanDeleteBranchAsync(
        string repositoryPath, string name, CancellationToken cancellationToken) =>
        Plan(GitObjectEditor.BranchOperationId, repositoryPath, RepositoryChangeKind.BranchDelete, name, "abc123", null);

    public Task<RepositoryPlan> PlanSetUpstreamAsync(
        string repositoryPath, string name, string? upstream, CancellationToken cancellationToken) =>
        Plan(GitObjectEditor.BranchOperationId, repositoryPath, RepositoryChangeKind.BranchUpstream, name, null, upstream);

    public Task<RepositoryPlan> PlanCreateTagAsync(
        string repositoryPath, string name, string? target, string? message, CancellationToken cancellationToken) =>
        Plan(GitObjectEditor.TagOperationId, repositoryPath, RepositoryChangeKind.TagCreate, name, null, "abc123");

    public Task<RepositoryPlan> PlanDeleteTagAsync(
        string repositoryPath, string name, CancellationToken cancellationToken) =>
        Plan(GitObjectEditor.TagOperationId, repositoryPath, RepositoryChangeKind.TagDelete, name, "abc123", null);

    public Task<RepositoryResult> ApplyAsync(RepositoryPlan plan, CancellationToken cancellationToken)
    {
        // Mirrors the real editor: a plan that cannot be applied is refused here too.
        if (plan.CanApply)
        {
            Applied.Add(plan);
        }

        return Task.FromResult(new RepositoryResult(plan.OperationId, plan.RefsToBackUp.Count > 0 ? "backup" : null)
        {
            Steps = [.. plan.Changes.Select(c => new GitVault.Core.Models.ActivationStepResult(
                c.Kind.ToString(), GitVault.Core.Models.StepOutcome.Applied, c.Target))],
        });
    }

    private Task<RepositoryPlan> Plan(
        string operationId,
        string repositoryPath,
        RepositoryChangeKind kind,
        string target,
        string? before,
        string? after)
    {
        var blockers = BlockNextPlan ? new[] { RepositoryBlockers.BranchMissing } : [];
        BlockNextPlan = false;

        return Task.FromResult(new RepositoryPlan(operationId, repositoryPath)
        {
            Changes = [new RepositoryChange(kind, target, before, after, ["noop"])],
            RefsToBackUp = after is null ? [target] : [],
            Blockers = blockers,
        });
    }
}
