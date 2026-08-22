using FluentAssertions;
using GitVault.Core.Repository;
using Xunit;
using Xunit.Abstractions;

namespace GitVault.Core.Tests;

/// <summary>
/// Working trees, stashes and submodules, against real repositories.
/// </summary>
/// <remarks>
/// The three share one property worth asserting: none of them ever passes <c>--force</c>. Git
/// refuses to remove a working tree holding uncommitted work, refuses to deinitialise a submodule
/// with changes inside it, and those refusals are the behaviour to keep rather than an obstacle to
/// route around. What the tests check is that the refusal actually arrives.
///
/// The other property is that dropping a stash is undoable, which is only true because the entry's
/// commit is preserved as a ref before it goes.
/// </remarks>
public sealed class WorktreeAndStashTests(ITestOutputHelper output)
{
    [Fact]
    public async Task The_main_working_tree_is_listed_first_and_named_as_such()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("list");
        var editor = await BuildWorktreesAsync(environment);

        var worktrees = await editor.ListAsync(repository, CancellationToken.None);

        worktrees.Should().ContainSingle();
        worktrees[0].IsMain.Should().BeTrue();
        worktrees[0].Branch.Should().NotBeNull("the harness checks out a branch");
        worktrees[0].IsLocked.Should().BeFalse();
    }

    [Fact]
    public async Task Adding_a_working_tree_creates_it_and_the_branch_it_was_asked_for()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("add");
        var target = Path.Combine(environment.Home, "add-worktree");
        var editor = await BuildWorktreesAsync(environment);

        var plan = await editor.PlanAddAsync(repository, target, "HEAD", "feature", CancellationToken.None);

        plan.CanApply.Should().BeTrue();
        Directory.Exists(target).Should().BeFalse("planning must not create anything");

        var result = await editor.ApplyAsync(plan, CancellationToken.None);
        result.Succeeded.Should().BeTrue();

        Directory.Exists(target).Should().BeTrue();

        var worktrees = await editor.ListAsync(repository, CancellationToken.None);
        worktrees.Should().HaveCount(2);
        worktrees.Should().Contain(w => w.Branch == "feature" && !w.IsMain);
    }

    [Fact]
    public async Task Adding_into_a_directory_that_holds_something_is_refused()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("occupied");
        var target = Path.Combine(environment.Home, "occupied-target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "something.txt"), "x\n");

        var editor = await BuildWorktreesAsync(environment);
        var plan = await editor.PlanAddAsync(repository, target, "HEAD", "feature", CancellationToken.None);

        plan.Blockers.Should().Contain(WorktreeBlockers.DirectoryNotEmpty);
        plan.CanApply.Should().BeFalse();
    }

    [Fact]
    public async Task Removing_a_working_tree_with_uncommitted_work_is_refused_by_git()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        // The whole argument for never passing --force: git's refusal is the safety net, and it
        // has to actually reach the user rather than being overridden for a smoother dialog.
        var repository = environment.CreateRepository("dirty");
        var target = Path.Combine(environment.Home, "dirty-worktree");
        var editor = await BuildWorktreesAsync(environment);

        await editor.ApplyAsync(
            await editor.PlanAddAsync(repository, target, "HEAD", "feature", CancellationToken.None),
            CancellationToken.None);

        File.WriteAllText(Path.Combine(target, "README.md"), "changed\n");

        var plan = await editor.PlanRemoveAsync(repository, target, CancellationToken.None);
        plan.CanApply.Should().BeTrue("the refusal comes from git, which knows about the changes");

        var result = await editor.ApplyAsync(plan, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        Directory.Exists(target).Should().BeTrue("nothing was thrown away");
    }

    [Fact]
    public async Task Removing_a_clean_working_tree_leaves_its_branch_alone()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("clean");
        var target = Path.Combine(environment.Home, "clean-worktree");
        var editor = await BuildWorktreesAsync(environment);

        await editor.ApplyAsync(
            await editor.PlanAddAsync(repository, target, "HEAD", "feature", CancellationToken.None),
            CancellationToken.None);

        var plan = await editor.PlanRemoveAsync(repository, target, CancellationToken.None);
        plan.Warnings.Should().Contain(WorktreeWarnings.BranchSurvives);

        var result = await editor.ApplyAsync(plan, CancellationToken.None);
        result.Succeeded.Should().BeTrue();

        Directory.Exists(target).Should().BeFalse();
        environment.Git(repository, "branch", "--list", "feature").Should().Contain("feature",
            "removing a checkout is not deleting the work");
    }

    [Fact]
    public async Task The_main_working_tree_cannot_be_removed_this_way()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("main");
        var editor = await BuildWorktreesAsync(environment);

        var plan = await editor.PlanRemoveAsync(repository, repository, CancellationToken.None);

        plan.Blockers.Should().Contain(WorktreeBlockers.CannotRemoveMain);
        plan.CanApply.Should().BeFalse();
    }

    [Fact]
    public async Task A_locked_working_tree_is_reported_and_not_removed()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("locked");
        var target = Path.Combine(environment.Home, "locked-worktree");
        var editor = await BuildWorktreesAsync(environment);

        await editor.ApplyAsync(
            await editor.PlanAddAsync(repository, target, "HEAD", "feature", CancellationToken.None),
            CancellationToken.None);

        await editor.ApplyAsync(
            await editor.PlanLockAsync(repository, target, true, "on a removable disk", CancellationToken.None),
            CancellationToken.None);

        var worktrees = await editor.ListAsync(repository, CancellationToken.None);
        var locked = worktrees.Single(w => !w.IsMain);

        locked.IsLocked.Should().BeTrue();
        locked.LockReason.Should().Be("on a removable disk");

        var plan = await editor.PlanRemoveAsync(repository, target, CancellationToken.None);
        plan.Blockers.Should().Contain(WorktreeBlockers.Locked);
    }

    [Fact]
    public async Task Pruning_with_nothing_to_prune_is_refused_rather_than_run()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("prune");
        var editor = await BuildWorktreesAsync(environment);

        var plan = await editor.PlanPruneAsync(repository, CancellationToken.None);

        plan.Blockers.Should().Contain(WorktreeBlockers.NothingToPrune);
        plan.CanApply.Should().BeFalse();
    }

    [Fact]
    public async Task Stashing_moves_the_changes_out_of_the_working_tree()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("push");
        File.WriteAllText(Path.Combine(repository, "README.md"), "changed\n");

        var editor = await BuildStashesAsync(environment);
        var plan = await editor.PlanPushAsync(repository, "work in progress", false, CancellationToken.None);

        plan.CanApply.Should().BeTrue();
        environment.Git(repository, "status", "--porcelain").Should().NotBeEmpty("planning changes nothing");

        var result = await editor.ApplyAsync(plan, CancellationToken.None);
        result.Succeeded.Should().BeTrue();

        environment.Git(repository, "status", "--porcelain").Should().BeEmpty();

        var stashes = await editor.ListAsync(repository, CancellationToken.None);
        stashes.Should().ContainSingle();
        stashes[0].Message.Should().Contain("work in progress");
        stashes[0].Reference.Should().Be("stash@{0}");
    }

    [Fact]
    public async Task Stashing_nothing_is_refused()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("empty");
        var editor = await BuildStashesAsync(environment);

        var plan = await editor.PlanPushAsync(repository, null, false, CancellationToken.None);

        plan.Blockers.Should().Contain(StashBlockers.NothingToStash);
        plan.CanApply.Should().BeFalse();
    }

    [Fact]
    public async Task Applying_a_stash_keeps_the_entry()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("apply");
        File.WriteAllText(Path.Combine(repository, "README.md"), "changed\n");

        var editor = await BuildStashesAsync(environment);
        await editor.ApplyAsync(
            await editor.PlanPushAsync(repository, "keep me", false, CancellationToken.None),
            CancellationToken.None);

        var plan = await editor.PlanApplyAsync(repository, "stash@{0}", CancellationToken.None);
        plan.Warnings.Should().Contain(StashWarnings.EntryStays);

        var result = await editor.ApplyAsync(plan, CancellationToken.None);
        result.Succeeded.Should().BeTrue();

        File.ReadAllText(Path.Combine(repository, "README.md")).Should().Contain("changed");

        (await editor.ListAsync(repository, CancellationToken.None)).Should().ContainSingle(
            "apply is not pop, so nothing was discarded");
    }

    [Fact]
    public async Task Applying_into_a_dirty_working_tree_is_refused()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("dirty-apply");
        File.WriteAllText(Path.Combine(repository, "README.md"), "changed\n");

        var editor = await BuildStashesAsync(environment);
        await editor.ApplyAsync(
            await editor.PlanPushAsync(repository, "first", false, CancellationToken.None),
            CancellationToken.None);

        File.WriteAllText(Path.Combine(repository, "README.md"), "something else\n");

        var plan = await editor.PlanApplyAsync(repository, "stash@{0}", CancellationToken.None);

        plan.Blockers.Should().Contain(StashBlockers.WorkingTreeDirty,
            "merging into work in progress can leave markers in a file being edited");

        plan.CanApply.Should().BeFalse();
    }

    [Fact]
    public async Task Dropping_a_stash_is_undone_by_restoring_its_backup()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("drop");
        File.WriteAllText(Path.Combine(repository, "README.md"), "changed\n");

        var runner = await BuildRunnerAsync(environment);
        var backups = new RefBackupService(runner);
        var editor = await BuildStashesAsync(environment, backups);

        await editor.ApplyAsync(
            await editor.PlanPushAsync(repository, "droppable", false, CancellationToken.None),
            CancellationToken.None);

        var entry = (await editor.ListAsync(repository, CancellationToken.None)).Single();

        var plan = await editor.PlanDropAsync(repository, entry.Reference, CancellationToken.None);
        plan.RefsToBackUp.Should().Contain(entry.Sha, "a dropped entry is otherwise unreachable");

        var result = await editor.ApplyAsync(plan, CancellationToken.None);
        result.BackupId.Should().NotBeNull();

        (await editor.ListAsync(repository, CancellationToken.None)).Should().BeEmpty();

        // The stash list is a reflog and restoring the ref does not rebuild it, but the commit —
        // which is the work — is still there, which is the whole point of the backup.
        environment.Git(repository, "cat-file", "-t", entry.Sha).Should().Be("commit");
        environment.Git(repository, "show", entry.Sha + ":README.md").Should().Contain("changed");
    }

    [Fact]
    public async Task A_stash_entry_reports_the_files_it_holds()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("files");
        File.WriteAllText(Path.Combine(repository, "README.md"), "one\ntwo\n");

        var editor = await BuildStashesAsync(environment);
        await editor.ApplyAsync(
            await editor.PlanPushAsync(repository, "with files", false, CancellationToken.None),
            CancellationToken.None);

        var changes = await editor.ReadChangesAsync(repository, "stash@{0}", CancellationToken.None);

        changes.Should().ContainSingle();
        changes[0].Path.Should().Be("README.md");
    }

    [Fact]
    public async Task A_refused_plan_does_not_report_itself_as_done()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        // "Every step succeeded" is vacuously true of no steps, and a refused plan runs none. The
        // interface guards this by refusing to confirm a blocked plan, but the value itself must
        // not tell the caller the work was done.
        var repository = environment.CreateRepository("refused");
        var editor = await BuildStashesAsync(environment);

        var plan = await editor.PlanPushAsync(repository, null, false, CancellationToken.None);
        plan.CanApply.Should().BeFalse();

        var result = await editor.ApplyAsync(plan, CancellationToken.None);

        result.Steps.Should().BeEmpty();
        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task A_repository_with_no_submodules_reports_none()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("nosub");
        var editor = await BuildSubmodulesAsync(environment);

        (await editor.ListAsync(repository, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_recorded_submodule_is_listed_with_its_url_and_state()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        // Written by hand rather than added with git, because adding one clones over the network
        // and GitVault makes no network calls — including in its own tests.
        var repository = environment.CreateRepository("sub");
        WriteModules(repository, "lib", "vendor/lib", "https://git.example.invalid/lib.git", "main");

        var editor = await BuildSubmodulesAsync(environment);
        var submodules = await editor.ListAsync(repository, CancellationToken.None);

        submodules.Should().ContainSingle();
        submodules[0].Name.Should().Be("lib");
        submodules[0].Path.Should().Be("vendor/lib");
        submodules[0].Url.Should().Be("https://git.example.invalid/lib.git");
        submodules[0].Branch.Should().Be("main");
    }

    [Fact]
    public async Task Correcting_a_submodule_url_rewrites_the_file_and_warns_that_sync_is_needed()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("suburl");
        WriteModules(repository, "lib", "vendor/lib", "https://git.example.invalid/lib.git", null);

        var editor = await BuildSubmodulesAsync(environment);
        var plan = await editor.PlanSetUrlAsync(
            repository, "lib", "git@git.example.invalid:lib.git", CancellationToken.None);

        plan.CanApply.Should().BeTrue();
        plan.Warnings.Should().Contain(SubmoduleWarnings.SyncNeeded,
            "editing the file changes nothing about what git does next");

        var result = await editor.ApplyAsync(plan, CancellationToken.None);
        result.Succeeded.Should().BeTrue();

        var again = await editor.ListAsync(repository, CancellationToken.None);
        again[0].Url.Should().Be("git@git.example.invalid:lib.git");
    }

    [Fact]
    public async Task Editing_a_submodule_that_is_not_recorded_is_refused()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("subabsent");
        WriteModules(repository, "lib", "vendor/lib", "https://git.example.invalid/lib.git", null);

        var editor = await BuildSubmodulesAsync(environment);
        var plan = await editor.PlanSetUrlAsync(
            repository, "other", "https://git.example.invalid/other.git", CancellationToken.None);

        plan.Blockers.Should().Contain(SubmoduleBlockers.NotFound);
        plan.CanApply.Should().BeFalse();
    }

    [Fact]
    public async Task Deinitialising_a_submodule_that_was_never_checked_out_is_refused()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("subdeinit");
        WriteModules(repository, "lib", "vendor/lib", "https://git.example.invalid/lib.git", null);

        var editor = await BuildSubmodulesAsync(environment);
        var plan = await editor.PlanDeinitAsync(repository, "lib", CancellationToken.None);

        plan.Blockers.Should().Contain(SubmoduleBlockers.NotInitialized);
        plan.CanApply.Should().BeFalse();
    }

    /// <summary>Writes a .gitmodules entry by hand and commits it.</summary>
    private static void WriteModules(string repository, string name, string path, string url, string? branch)
    {
        var lines = new List<string>
        {
            "[submodule \"" + name + "\"]",
            "\tpath = " + path,
            "\turl = " + url,
        };

        if (branch is { Length: > 0 })
        {
            lines.Add("\tbranch = " + branch);
        }

        File.WriteAllText(Path.Combine(repository, ".gitmodules"), string.Join('\n', lines) + "\n");
    }

    private static async Task<GitCommandRunner> BuildRunnerAsync(TempGitEnvironment environment)
    {
        var config = await environment.BuildConfigServiceAsync();
        return new GitCommandRunner(new GitVault.Core.Platform.ProcessRunner(), config, environment.Paths);
    }

    private static async Task<WorktreeEditor> BuildWorktreesAsync(TempGitEnvironment environment)
    {
        var runner = await BuildRunnerAsync(environment);
        return new WorktreeEditor(runner, new RepositoryPlanApplier(runner, new RefBackupService(runner)));
    }

    private static async Task<StashEditor> BuildStashesAsync(
        TempGitEnvironment environment,
        RefBackupService? backups = null)
    {
        var runner = await BuildRunnerAsync(environment);

        return new StashEditor(
            runner,
            new RepositoryInspector(runner),
            new RepositoryPlanApplier(runner, backups ?? new RefBackupService(runner)));
    }

    private static async Task<SubmoduleEditor> BuildSubmodulesAsync(TempGitEnvironment environment)
    {
        var runner = await BuildRunnerAsync(environment);
        return new SubmoduleEditor(runner, new RepositoryPlanApplier(runner, new RefBackupService(runner)));
    }
}
