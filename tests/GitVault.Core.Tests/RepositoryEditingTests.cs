using FluentAssertions;
using GitVault.Core.Repository;
using Xunit;
using Xunit.Abstractions;

namespace GitVault.Core.Tests;

/// <summary>
/// Reading and editing remotes, branches and tags, against real repositories.
/// </summary>
/// <remarks>
/// These are the first operations in the project that change refs rather than files, so the
/// safety net changes with them: a ref backup instead of a file snapshot. The properties under
/// test are that planning writes nothing, that a plan git would refuse is refused earlier and
/// more clearly, that a decision the user is entitled to make warns rather than blocks, and that
/// the backup makes such a decision reversible.
/// </remarks>
public sealed class RepositoryEditingTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Remotes_branches_and_tags_are_read_as_git_reports_them()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("inspect");
        environment.Git(repository, "remote", "add", "origin", "https://git.example.invalid/thing.git");
        environment.Git(repository, "branch", "feature");
        environment.Git(repository, "tag", "v1.0");
        environment.Git(repository, "tag", "-a", "v2.0", "-m", "Second release");

        var inspector = await BuildInspectorAsync(environment);

        var remotes = await inspector.ListRemotesAsync(repository, CancellationToken.None);
        remotes.Should().ContainSingle();
        remotes[0].Name.Should().Be("origin");
        remotes[0].FetchUrl.Should().Be("https://git.example.invalid/thing.git");
        remotes[0].PushUrl.Should().Be("https://git.example.invalid/thing.git",
            "a remote with no separate push URL pushes where it fetches");

        var branches = await inspector.ListBranchesAsync(repository, CancellationToken.None);
        branches.Select(b => b.Name).Should().Contain(["feature"]);
        branches.Should().ContainSingle(b => b.IsCurrent);

        var tags = await inspector.ListTagsAsync(repository, CancellationToken.None);
        tags.Should().HaveCount(2);
        tags.Single(t => t.Name == "v1.0").IsAnnotated.Should().BeFalse();

        var annotated = tags.Single(t => t.Name == "v2.0");
        annotated.IsAnnotated.Should().BeTrue();
        annotated.Message.Should().Be("Second release");
        annotated.TargetCommit.Should().Be(environment.Git(repository, "rev-parse", "HEAD"),
            "an annotated tag reports the commit it points at, not its own object");
    }

    [Fact]
    public async Task A_commit_subject_containing_a_tab_does_not_shift_a_column()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("awkward");

        File.WriteAllText(Path.Combine(repository, "file.txt"), "x");
        environment.Git(repository, "add", "file.txt");
        environment.Git(repository, "commit", "--quiet", "-m", "Subject\twith a tab and | a pipe");

        var inspector = await BuildInspectorAsync(environment);
        var branches = await inspector.ListBranchesAsync(repository, CancellationToken.None);

        branches.Single(b => b.IsCurrent).TipSubject
            .Should().Be("Subject\twith a tab and | a pipe");
    }

    [Fact]
    public async Task Repository_state_reports_a_dirty_tree_and_an_interrupted_operation()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("state");
        var inspector = await BuildInspectorAsync(environment);

        var clean = await inspector.GetStateAsync(repository, CancellationToken.None);
        clean.HasUncommittedChanges.Should().BeFalse();
        clean.IsDetached.Should().BeFalse();
        clean.Operation.Should().Be(RepositoryOperation.None);
        clean.IsQuiet.Should().BeTrue();

        File.WriteAllText(Path.Combine(repository, "dirty.txt"), "unstaged");

        var dirty = await inspector.GetStateAsync(repository, CancellationToken.None);
        dirty.HasUncommittedChanges.Should().BeTrue();

        // A merge left half-finished is the state most edits must refuse to walk into.
        var gitDirectory = environment.Git(repository, "rev-parse", "--absolute-git-dir");
        File.WriteAllText(Path.Combine(gitDirectory, "MERGE_HEAD"), clean.HeadCommit + "\n");

        var merging = await inspector.GetStateAsync(repository, CancellationToken.None);
        merging.Operation.Should().Be(RepositoryOperation.Merge);
        merging.IsQuiet.Should().BeFalse();
    }

    [Fact]
    public async Task Planning_a_branch_deletion_writes_nothing()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("plan");
        environment.Git(repository, "branch", "doomed");

        var editor = await BuildEditorAsync(environment);
        var plan = await editor.PlanDeleteBranchAsync(repository, "doomed", CancellationToken.None);

        plan.CanApply.Should().BeTrue();
        environment.Git(repository, "branch", "--list", "doomed").Should().NotBeEmpty("planning must not delete");
    }

    [Fact]
    public async Task Deleting_the_checked_out_branch_is_blocked_rather_than_attempted()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("current");
        var current = environment.Git(repository, "branch", "--show-current");

        var editor = await BuildEditorAsync(environment);
        var plan = await editor.PlanDeleteBranchAsync(repository, current, CancellationToken.None);

        plan.Blockers.Should().Contain(RepositoryBlockers.BranchIsCurrent);
        plan.CanApply.Should().BeFalse();

        var result = await editor.ApplyAsync(plan, CancellationToken.None);
        result.BackupId.Should().BeNull("a blocked plan takes no backup because it runs nothing");

        environment.Git(repository, "branch", "--show-current").Should().Be(current);
    }

    [Fact]
    public async Task An_unmerged_branch_warns_but_can_still_be_deleted_and_restored()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("unmerged");
        var main = environment.Git(repository, "branch", "--show-current");

        environment.Git(repository, "checkout", "--quiet", "-b", "work");
        File.WriteAllText(Path.Combine(repository, "work.txt"), "only on this branch");
        environment.Git(repository, "add", "work.txt");
        environment.Git(repository, "commit", "--quiet", "-m", "Work in progress");
        var tip = environment.Git(repository, "rev-parse", "HEAD");

        environment.Git(repository, "checkout", "--quiet", main);

        var backups = new RefBackupService(await BuildRunnerAsync(environment));
        var editor = await BuildEditorAsync(environment, backups);

        var plan = await editor.PlanDeleteBranchAsync(repository, "work", CancellationToken.None);

        plan.Warnings.Should().Contain(RepositoryWarnings.BranchNotMerged,
            "losing commits is the user's decision to make, not the tool's to forbid");
        plan.Blockers.Should().BeEmpty();
        plan.RefsToBackUp.Should().Contain("refs/heads/work");

        var result = await editor.ApplyAsync(plan, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.BackupId.Should().NotBeNullOrEmpty();
        environment.Git(repository, "branch", "--list", "work").Should().BeEmpty();

        // The whole point of the backup: the decision is reversible.
        await backups.RestoreAsync(repository, result.BackupId!, CancellationToken.None);

        environment.Git(repository, "rev-parse", "refs/heads/work").Should().Be(tip);
    }

    [Fact]
    public async Task A_backup_keeps_the_orphaned_commits_reachable()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("reachable");
        var main = environment.Git(repository, "branch", "--show-current");

        environment.Git(repository, "checkout", "--quiet", "-b", "temp");
        File.WriteAllText(Path.Combine(repository, "temp.txt"), "content");
        environment.Git(repository, "add", "temp.txt");
        environment.Git(repository, "commit", "--quiet", "-m", "Temporary");
        var tip = environment.Git(repository, "rev-parse", "HEAD");

        environment.Git(repository, "checkout", "--quiet", main);

        var backups = new RefBackupService(await BuildRunnerAsync(environment));
        var editor = await BuildEditorAsync(environment, backups);

        var plan = await editor.PlanDeleteBranchAsync(repository, "temp", CancellationToken.None);
        await editor.ApplyAsync(plan, CancellationToken.None);

        // Aggressive pruning is what would discard an orphaned commit. It must not, because the
        // backup ref makes the commit reachable.
        environment.Git(repository, "reflog", "expire", "--expire=now", "--all");
        environment.Git(repository, "gc", "--prune=now", "--quiet");

        environment.Git(repository, "cat-file", "-t", tip).Should().Be("commit",
            "a backup ref keeps the history a deletion orphaned");
    }

    [Fact]
    public async Task A_ref_name_git_would_refuse_is_refused_before_git_sees_it()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("names");
        var editor = await BuildEditorAsync(environment);

        foreach (var name in new[] { "", "-f", "with space", "ends/", "two..dots", "bad~name", "trap.lock" })
        {
            var plan = await editor.PlanCreateBranchAsync(repository, name, null, CancellationToken.None);

            plan.Blockers.Should().Contain(RepositoryBlockers.RefNameInvalid,
                $"\"{name}\" is not a name git accepts");
            plan.CanApply.Should().BeFalse();
        }
    }

    [Fact]
    public async Task Remote_edits_round_trip_through_git()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("remotes");
        var editor = await BuildEditorAsync(environment);
        var inspector = await BuildInspectorAsync(environment);

        await editor.ApplyAsync(
            await editor.PlanAddRemoteAsync(
                repository, "origin", "https://git.example.invalid/a.git", CancellationToken.None),
            CancellationToken.None);

        environment.Git(repository, "remote", "get-url", "origin")
            .Should().Be("https://git.example.invalid/a.git");

        // Adding a second remote of the same name is refused rather than attempted.
        var duplicate = await editor.PlanAddRemoteAsync(
            repository, "origin", "https://git.example.invalid/b.git", CancellationToken.None);
        duplicate.Blockers.Should().Contain(RepositoryBlockers.RemoteExists);

        await editor.ApplyAsync(
            await editor.PlanSetRemoteUrlAsync(
                repository,
                "origin",
                "https://git.example.invalid/c.git",
                "ssh://git@example.invalid/c.git",
                CancellationToken.None),
            CancellationToken.None);

        var remote = (await inspector.ListRemotesAsync(repository, CancellationToken.None)).Single();
        remote.FetchUrl.Should().Be("https://git.example.invalid/c.git");
        remote.PushUrl.Should().Be("ssh://git@example.invalid/c.git",
            "a separate push URL is reported separately");

        await editor.ApplyAsync(
            await editor.PlanRenameRemoteAsync(repository, "origin", "upstream", CancellationToken.None),
            CancellationToken.None);

        (await inspector.ListRemotesAsync(repository, CancellationToken.None))
            .Single().Name.Should().Be("upstream");
    }

    [Fact]
    public async Task Tags_can_be_created_annotated_and_deleted_with_a_backup()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("tags");
        var backups = new RefBackupService(await BuildRunnerAsync(environment));
        var editor = await BuildEditorAsync(environment, backups);
        var inspector = await BuildInspectorAsync(environment);

        await editor.ApplyAsync(
            await editor.PlanCreateTagAsync(repository, "v1.0", null, "First release", CancellationToken.None),
            CancellationToken.None);

        var tag = (await inspector.ListTagsAsync(repository, CancellationToken.None)).Single();
        tag.IsAnnotated.Should().BeTrue();
        tag.Message.Should().Be("First release");

        var result = await editor.ApplyAsync(
            await editor.PlanDeleteTagAsync(repository, "v1.0", CancellationToken.None),
            CancellationToken.None);

        (await inspector.ListTagsAsync(repository, CancellationToken.None)).Should().BeEmpty();

        await backups.RestoreAsync(repository, result.BackupId!, CancellationToken.None);

        (await inspector.ListTagsAsync(repository, CancellationToken.None))
            .Should().ContainSingle().Which.Name.Should().Be("v1.0");
    }

    [Fact]
    public async Task An_interrupted_operation_blocks_a_branch_change()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("interrupted");
        environment.Git(repository, "branch", "other");

        var gitDirectory = environment.Git(repository, "rev-parse", "--absolute-git-dir");
        File.WriteAllText(
            Path.Combine(gitDirectory, "MERGE_HEAD"),
            environment.Git(repository, "rev-parse", "HEAD") + "\n");

        var editor = await BuildEditorAsync(environment);
        var plan = await editor.PlanDeleteBranchAsync(repository, "other", CancellationToken.None);

        plan.Blockers.Should().Contain(RepositoryBlockers.OperationInProgress,
            "a half-finished merge is not a state to start deleting branches in");
    }

    private static async Task<GitCommandRunner> BuildRunnerAsync(TempGitEnvironment environment)
    {
        var config = await environment.BuildConfigServiceAsync();
        return new GitCommandRunner(new GitVault.Core.Platform.ProcessRunner(), config, environment.Paths);
    }

    private static async Task<RepositoryInspector> BuildInspectorAsync(TempGitEnvironment environment) =>
        new(await BuildRunnerAsync(environment));

    private static async Task<GitObjectEditor> BuildEditorAsync(
        TempGitEnvironment environment,
        RefBackupService? backups = null)
    {
        var runner = await BuildRunnerAsync(environment);
        return new GitObjectEditor(
            runner,
            new RepositoryInspector(runner),
            new RepositoryPlanApplier(runner, backups ?? new RefBackupService(runner)));
    }
}
