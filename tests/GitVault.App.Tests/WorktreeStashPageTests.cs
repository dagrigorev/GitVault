using Avalonia.Headless.XUnit;
using FluentAssertions;
using GitVault.App.Services;
using GitVault.App.ViewModels;
using GitVault.Core.Models;
using GitVault.Core.Repository;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GitVault.App.Tests;

/// <summary>
/// The working trees, stashes and submodules pages.
/// </summary>
/// <remarks>
/// The engines are covered in <c>GitVault.Core.Tests</c> against real repositories. What is
/// checked here is the interface in front of them: every action previews first, closing the
/// preview does nothing, and the two decisions that are easy to get wrong stay separated — putting
/// a stash back is not the same button as discarding it, and the submodules page never offers
/// anything that would need the network.
/// </remarks>
public sealed class WorktreeStashPageTests
{
    [AvaloniaFact]
    public async Task Removing_a_working_tree_is_previewed_and_closing_it_does_nothing()
    {
        using var provider = Build(out var worktrees, out _, out _);
        var page = await OpenWorktreesAsync(provider);
        var dialogs = provider.GetRequiredService<FakeDialogService>();

        page.SelectedRow = page.Rows.Single(r => !r.Worktree.IsMain);
        dialogs.Answer = false;

        await page.RemoveCommand.ExecuteAsync(CancellationToken.None);

        dialogs.ShownOfType<RepositoryReviewViewModel>().Should().ContainSingle();
        worktrees.Applied.Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task Confirming_the_preview_is_what_removes_a_working_tree()
    {
        using var provider = Build(out var worktrees, out _, out _);
        var page = await OpenWorktreesAsync(provider);
        var dialogs = provider.GetRequiredService<FakeDialogService>();

        page.SelectedRow = page.Rows.Single(r => !r.Worktree.IsMain);
        dialogs.Answer = true;

        await page.RemoveCommand.ExecuteAsync(CancellationToken.None);

        worktrees.Removed.Should().ContainSingle().Which.Should().Be("/src/alpha-feature");
        worktrees.Applied.Should().ContainSingle();
    }

    [AvaloniaFact]
    public async Task The_main_working_tree_offers_no_removal()
    {
        using var provider = Build(out _, out _, out _);
        var page = await OpenWorktreesAsync(provider);

        page.SelectedRow = page.Rows.Single(r => r.Worktree.IsMain);

        page.CanRemove.Should().BeFalse();
        page.CanLock.Should().BeFalse("the repository's own working tree is not lockable either");
    }

    [AvaloniaFact]
    public async Task A_locked_working_tree_offers_unlocking_rather_than_locking()
    {
        using var provider = Build(out var worktrees, out _, out _);
        worktrees.Worktrees =
        [
            new GitWorktree("/src/alpha", "aaaa1111", "main", true, false, null, false, false),
            new GitWorktree("/src/alpha-feature", "bbbb2222", "feature", false, true, "on a disk", false, false),
        ];

        var page = await OpenWorktreesAsync(provider);
        page.SelectedRow = page.Rows.Single(r => !r.Worktree.IsMain);

        page.CanLock.Should().BeFalse();
        page.CanUnlock.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task Putting_a_stash_back_and_discarding_it_are_separate_actions()
    {
        using var provider = Build(out _, out var stashes, out _);
        var page = await OpenStashesAsync(provider);
        var dialogs = provider.GetRequiredService<FakeDialogService>();

        dialogs.Answer = true;
        await page.ApplyEntryCommand.ExecuteAsync(CancellationToken.None);

        stashes.Applied.Should().ContainSingle().Which.Should().Be("stash@{0}");
        stashes.Dropped.Should().BeEmpty("putting an entry back must not discard it");
    }

    [AvaloniaFact]
    public async Task Discarding_a_stash_is_previewed_and_names_the_backup()
    {
        using var provider = Build(out _, out var stashes, out _);
        var page = await OpenStashesAsync(provider);
        var dialogs = provider.GetRequiredService<FakeDialogService>();

        dialogs.Answer = true;
        await page.DropCommand.ExecuteAsync(CancellationToken.None);

        var review = dialogs.ShownOfType<RepositoryReviewViewModel>().Should().ContainSingle().Subject;
        review.HasBackup.Should().BeTrue("a dropped entry is otherwise unreachable");
        review.HasWarnings.Should().BeTrue();

        stashes.Dropped.Should().ContainSingle().Which.Should().Be("stash@{0}");
    }

    [AvaloniaFact]
    public async Task A_blocked_stash_plan_cannot_be_confirmed()
    {
        using var provider = Build(out _, out var stashes, out _);
        stashes.BlockNextPlan = StashBlockers.WorkingTreeDirty;

        var page = await OpenStashesAsync(provider);
        var dialogs = provider.GetRequiredService<FakeDialogService>();

        dialogs.Answer = true;
        await page.ApplyEntryCommand.ExecuteAsync(CancellationToken.None);

        var review = dialogs.ShownOfType<RepositoryReviewViewModel>().Should().ContainSingle().Subject;
        review.HasBlockers.Should().BeTrue();
        review.CanConfirm.Should().BeFalse();

        stashes.AppliedPlans.Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task Correcting_a_submodule_address_is_previewed()
    {
        using var provider = Build(out _, out _, out var submodules);
        var page = await OpenSubmodulesAsync(provider);
        var dialogs = provider.GetRequiredService<FakeDialogService>();

        dialogs.Handler = dialog =>
        {
            if (dialog is SubmoduleEditorViewModel editor)
            {
                editor.Url = "git@git.example.invalid:lib.git";
                return true;
            }

            return true;
        };

        await page.EditCommand.ExecuteAsync(CancellationToken.None);

        submodules.Urls.Should().ContainSingle()
            .Which.Should().Be("lib -> git@git.example.invalid:lib.git");

        dialogs.ShownOfType<RepositoryReviewViewModel>().Should().ContainSingle();
        submodules.Applied.Should().ContainSingle();
    }

    [AvaloniaFact]
    public async Task A_submodule_with_no_working_copy_offers_no_removal_of_one()
    {
        using var provider = Build(out _, out _, out var submodules);
        submodules.Submodules =
        [
            new GitSubmodule("lib", "vendor/lib", "https://git.example.invalid/lib.git", null, "",
                SubmoduleState.NotInitialized),
        ];

        var page = await OpenSubmodulesAsync(provider);

        page.CanDeinit.Should().BeFalse();
        page.HasSelectedSubmodule.Should().BeTrue("it is still listed and its address still editable");
    }

    [AvaloniaFact]
    public async Task The_submodules_page_says_it_will_not_use_the_network()
    {
        using var provider = Build(out _, out _, out _);
        var page = await OpenSubmodulesAsync(provider);
        var localizer = provider.GetRequiredService<GitVault.Localization.Localizer>();

        page.NoNetworkCaption.Should().Be(localizer[GitVault.Localization.Keys.Submodules_NoNetworkNote]);
        page.NoNetworkCaption.Should().NotBeEmpty();
    }

    private static async Task<WorktreesViewModel> OpenWorktreesAsync(ServiceProvider provider)
    {
        provider.GetRequiredService<RepositoryContext>().Select("/src/alpha", "alpha");

        var page = provider.GetRequiredService<WorktreesViewModel>();
        await page.ReloadAsync(CancellationToken.None);

        page.Rows.Should().NotBeEmpty();
        return page;
    }

    private static async Task<StashesViewModel> OpenStashesAsync(ServiceProvider provider)
    {
        provider.GetRequiredService<RepositoryContext>().Select("/src/alpha", "alpha");

        var page = provider.GetRequiredService<StashesViewModel>();
        await page.ReloadAsync(CancellationToken.None);

        page.SelectedRow.Should().NotBeNull();
        return page;
    }

    private static async Task<SubmodulesViewModel> OpenSubmodulesAsync(ServiceProvider provider)
    {
        provider.GetRequiredService<RepositoryContext>().Select("/src/alpha", "alpha");

        var page = provider.GetRequiredService<SubmodulesViewModel>();
        await page.ReloadAsync(CancellationToken.None);

        return page;
    }

    private static ServiceProvider Build(
        out StubWorktreeEditor worktrees,
        out StubStashEditor stashes,
        out StubSubmoduleEditor submodules)
    {
        var stubWorktrees = new StubWorktreeEditor();
        var stubStashes = new StubStashEditor();
        var stubSubmodules = new StubSubmoduleEditor();

        worktrees = stubWorktrees;
        stashes = stubStashes;
        submodules = stubSubmodules;

        return TestServices.Build(services =>
        {
            services.AddSingleton<IWorktreeEditor>(stubWorktrees);
            services.AddSingleton<IStashEditor>(stubStashes);
            services.AddSingleton<ISubmoduleEditor>(stubSubmodules);
        });
    }

    /// <summary>Mirrors the real appliers: an applied plan reports a step for each change.</summary>
    internal static RepositoryResult Result(RepositoryPlan plan) =>
        new(plan.OperationId, plan.RefsToBackUp.Count > 0 ? "backup" : null)
        {
            Steps = [.. plan.Changes.Select(c =>
                new ActivationStepResult(c.Kind.ToString(), StepOutcome.Applied, c.Target))],
        };
}

/// <summary>A working-tree editor that records what it was asked to do.</summary>
internal sealed class StubWorktreeEditor : IWorktreeEditor
{
    /// <summary>Working trees reported to the page.</summary>
    public IReadOnlyList<GitWorktree> Worktrees { get; set; } =
    [
        new GitWorktree("/src/alpha", "aaaa1111", "main", true, false, null, false, false),
        new GitWorktree("/src/alpha-feature", "bbbb2222", "feature", false, false, null, false, false),
    ];

    /// <summary>Removals that were planned.</summary>
    public List<string> Removed { get; } = [];

    /// <summary>Plans that reached <see cref="ApplyAsync"/> and were not refused.</summary>
    public List<RepositoryPlan> Applied { get; } = [];

    public Task<IReadOnlyList<GitWorktree>> ListAsync(
        string repositoryPath, CancellationToken cancellationToken) =>
        Task.FromResult(Worktrees);

    public Task<RepositoryPlan> PlanAddAsync(
        string repositoryPath, string path, string startPoint, string? createBranch, CancellationToken cancellationToken) =>
        Task.FromResult(Plan(repositoryPath, RepositoryChangeKind.WorktreeAdd, path));

    public Task<RepositoryPlan> PlanRemoveAsync(
        string repositoryPath, string path, CancellationToken cancellationToken)
    {
        Removed.Add(path);
        return Task.FromResult(Plan(repositoryPath, RepositoryChangeKind.WorktreeRemove, path));
    }

    public Task<RepositoryPlan> PlanLockAsync(
        string repositoryPath, string path, bool locked, string? reason, CancellationToken cancellationToken) =>
        Task.FromResult(Plan(
            repositoryPath,
            locked ? RepositoryChangeKind.WorktreeLock : RepositoryChangeKind.WorktreeUnlock,
            path));

    public Task<RepositoryPlan> PlanPruneAsync(string repositoryPath, CancellationToken cancellationToken) =>
        Task.FromResult(Plan(repositoryPath, RepositoryChangeKind.WorktreePrune, string.Empty));

    public Task<RepositoryResult> ApplyAsync(RepositoryPlan plan, CancellationToken cancellationToken)
    {
        if (plan.CanApply)
        {
            Applied.Add(plan);
        }

        return Task.FromResult(WorktreeStashPageTests.Result(plan));
    }

    private static RepositoryPlan Plan(string repositoryPath, RepositoryChangeKind kind, string target) =>
        new(WorktreeEditor.OperationId, repositoryPath)
        {
            Changes = [new RepositoryChange(kind, target, null, target, ["noop"])],
        };
}

/// <summary>A stash editor that records what it was asked to do.</summary>
internal sealed class StubStashEditor : IStashEditor
{
    /// <summary>Entries reported to the page.</summary>
    public IReadOnlyList<GitStash> Stashes { get; set; } =
    [
        new GitStash(0, "stash@{0}", "cccc3333", "WIP on main: work", "main", DateTimeOffset.UnixEpoch),
    ];

    /// <summary>Blocker the next plan carries, when set.</summary>
    public string? BlockNextPlan { get; set; }

    /// <summary>References an apply was planned for.</summary>
    public List<string> Applied { get; } = [];

    /// <summary>References a drop was planned for.</summary>
    public List<string> Dropped { get; } = [];

    /// <summary>Plans that reached <see cref="ApplyAsync"/> and were not refused.</summary>
    public List<RepositoryPlan> AppliedPlans { get; } = [];

    public Task<IReadOnlyList<GitStash>> ListAsync(string repositoryPath, CancellationToken cancellationToken) =>
        Task.FromResult(Stashes);

    public Task<IReadOnlyList<CommitFileChange>> ReadChangesAsync(
        string repositoryPath, string reference, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CommitFileChange>>(
            [new CommitFileChange(FileChangeStatus.Modified, "README.md", null, 1, 0)]);

    public Task<RepositoryPlan> PlanPushAsync(
        string repositoryPath, string? message, bool includeUntracked, CancellationToken cancellationToken) =>
        Task.FromResult(Plan(repositoryPath, RepositoryChangeKind.StashPush, message ?? string.Empty, false));

    public Task<RepositoryPlan> PlanApplyAsync(
        string repositoryPath, string reference, CancellationToken cancellationToken)
    {
        Applied.Add(reference);
        return Task.FromResult(Plan(repositoryPath, RepositoryChangeKind.StashApply, reference, false));
    }

    public Task<RepositoryPlan> PlanDropAsync(
        string repositoryPath, string reference, CancellationToken cancellationToken)
    {
        Dropped.Add(reference);
        return Task.FromResult(Plan(repositoryPath, RepositoryChangeKind.StashDrop, reference, true));
    }

    public Task<RepositoryPlan> PlanBranchAsync(
        string repositoryPath, string reference, string branch, CancellationToken cancellationToken) =>
        Task.FromResult(Plan(repositoryPath, RepositoryChangeKind.StashBranch, branch, false));

    public Task<RepositoryResult> ApplyAsync(RepositoryPlan plan, CancellationToken cancellationToken)
    {
        if (plan.CanApply)
        {
            AppliedPlans.Add(plan);
        }

        return Task.FromResult(WorktreeStashPageTests.Result(plan));
    }

    private RepositoryPlan Plan(
        string repositoryPath,
        RepositoryChangeKind kind,
        string target,
        bool backUp)
    {
        var blockers = BlockNextPlan is { Length: > 0 } blocker ? new[] { blocker } : [];
        BlockNextPlan = null;

        return new RepositoryPlan(StashEditor.OperationId, repositoryPath)
        {
            Changes = [new RepositoryChange(kind, target, null, target, ["noop"])],
            RefsToBackUp = backUp ? ["cccc3333"] : [],
            Blockers = blockers,
            Warnings = [StashWarnings.DropIsPermanent],
        };
    }
}

/// <summary>A submodule editor that records what it was asked to do, and never uses a network.</summary>
internal sealed class StubSubmoduleEditor : ISubmoduleEditor
{
    /// <summary>Submodules reported to the page.</summary>
    public IReadOnlyList<GitSubmodule> Submodules { get; set; } =
    [
        new GitSubmodule("lib", "vendor/lib", "https://git.example.invalid/lib.git", "main", "dddd4444",
            SubmoduleState.UpToDate),
    ];

    /// <summary>URL changes that were planned, as "name -&gt; url".</summary>
    public List<string> Urls { get; } = [];

    /// <summary>Plans that reached <see cref="ApplyAsync"/> and were not refused.</summary>
    public List<RepositoryPlan> Applied { get; } = [];

    public Task<IReadOnlyList<GitSubmodule>> ListAsync(
        string repositoryPath, CancellationToken cancellationToken) =>
        Task.FromResult(Submodules);

    public Task<RepositoryPlan> PlanSetUrlAsync(
        string repositoryPath, string name, string url, CancellationToken cancellationToken)
    {
        Urls.Add(name + " -> " + url);
        return Task.FromResult(Plan(repositoryPath, RepositoryChangeKind.SubmoduleSetUrl, name));
    }

    public Task<RepositoryPlan> PlanSetBranchAsync(
        string repositoryPath, string name, string? branch, CancellationToken cancellationToken) =>
        Task.FromResult(Plan(repositoryPath, RepositoryChangeKind.SubmoduleSetBranch, name));

    public Task<RepositoryPlan> PlanSyncAsync(
        string repositoryPath, string? name, CancellationToken cancellationToken) =>
        Task.FromResult(Plan(repositoryPath, RepositoryChangeKind.SubmoduleSync, name ?? string.Empty));

    public Task<RepositoryPlan> PlanDeinitAsync(
        string repositoryPath, string name, CancellationToken cancellationToken) =>
        Task.FromResult(Plan(repositoryPath, RepositoryChangeKind.SubmoduleDeinit, name));

    public Task<RepositoryResult> ApplyAsync(RepositoryPlan plan, CancellationToken cancellationToken)
    {
        if (plan.CanApply)
        {
            Applied.Add(plan);
        }

        return Task.FromResult(WorktreeStashPageTests.Result(plan));
    }

    private static RepositoryPlan Plan(string repositoryPath, RepositoryChangeKind kind, string target) =>
        new(SubmoduleEditor.OperationId, repositoryPath)
        {
            Changes = [new RepositoryChange(kind, target, null, target, ["noop"])],
            Warnings = [SubmoduleWarnings.SyncNeeded],
        };
}
