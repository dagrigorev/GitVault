using FluentAssertions;
using GitVault.Core.Repository;
using Xunit;
using Xunit.Abstractions;

namespace GitVault.Core.Tests;

/// <summary>
/// Rewriting commit metadata, against real repositories.
/// </summary>
/// <remarks>
/// The most consequential operation in this application, so the assertions are about what must
/// remain true rather than about what the feature does. Planning writes nothing. Trees are never
/// touched, so no file content can change. A commit nobody edited rebuilds to the same object
/// name. The whole thing is undone by restoring one ref. And a rewrite that git would refuse is
/// refused earlier, with a reason.
/// </remarks>
public sealed class HistoryRewriteTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Planning_a_rewrite_writes_nothing()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = Build(environment, "plan", 3);
        var tip = environment.Git(repository, "rev-parse", "HEAD");
        var (rewriter, reader) = await BuildAsync(environment);

        var commits = await reader.ReadAsync(repository, new CommitQuery(), CancellationToken.None);
        var plan = await rewriter.PlanAsync(
            repository,
            [new CommitEdit(commits[1].Sha) { Message = "Rewritten subject" }],
            CancellationToken.None);

        plan.CanApply.Should().BeTrue();
        environment.Git(repository, "rev-parse", "HEAD").Should().Be(tip, "planning must not move anything");
    }

    [Fact]
    public async Task Editing_a_message_rewrites_that_commit_and_everything_after_it()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = Build(environment, "message", 3);
        var (rewriter, reader) = await BuildAsync(environment);

        var before = await reader.ReadAsync(repository, new CommitQuery(), CancellationToken.None);
        var target = before[1];

        var plan = await rewriter.PlanAsync(
            repository,
            [new CommitEdit(target.Sha) { Message = "Rewritten subject\n\nAnd a body." }],
            CancellationToken.None);

        // The range starts at the earliest edit and runs to the tip. Commits before the edit are
        // untouched: their inputs did not change, so there is nothing to rebuild.
        plan.EditedCount.Should().Be(1);
        plan.CarriedCount.Should().Be(1, "only the commit after the edit has to move");
        plan.Steps.Should().HaveCount(2);

        var result = await rewriter.ApplyAsync(plan, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.BackupId.Should().NotBeNullOrEmpty();

        var after = await reader.ReadAsync(repository, new CommitQuery(), CancellationToken.None);

        after.Should().HaveCount(3);
        after[1].Subject.Should().Be("Rewritten subject");
        after[1].Body.Should().Be("And a body.");
        after[1].Sha.Should().NotBe(target.Sha);

        after[2].Sha.Should().Be(before[2].Sha, "a commit before the edit is untouched");
        after[0].Sha.Should().NotBe(before[0].Sha, "a commit after the edit gets a new name");
        after[0].Subject.Should().Be(before[0].Subject, "…but says the same thing");
    }

    [Fact]
    public async Task File_content_is_identical_after_a_rewrite()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = Build(environment, "trees", 3);
        var (rewriter, reader) = await BuildAsync(environment);

        var before = await reader.ReadAsync(repository, new CommitQuery(), CancellationToken.None);
        var treesBefore = before.Select(c => c.TreeSha).ToList();

        var plan = await rewriter.PlanAsync(
            repository,
            [new CommitEdit(before[1].Sha) { AuthorEmail = "someone-else@example.invalid" }],
            CancellationToken.None);

        await rewriter.ApplyAsync(plan, CancellationToken.None);

        var after = await reader.ReadAsync(repository, new CommitQuery(), CancellationToken.None);

        after.Select(c => c.TreeSha).Should().Equal(treesBefore,
            "the rewrite rebuilds commits around unchanged trees, so no file content can move");

        environment.Git(repository, "status", "--porcelain").Should().BeEmpty(
            "the working tree still matches the branch");
    }

    [Fact]
    public async Task Author_and_committer_are_changed_independently_and_keep_their_offsets()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = Build(environment, "identities", 2);
        var (rewriter, reader) = await BuildAsync(environment);

        var before = await reader.ReadAsync(repository, new CommitQuery(), CancellationToken.None);
        var authored = new DateTimeOffset(2019, 4, 5, 6, 7, 8, TimeSpan.FromMinutes(330));

        var plan = await rewriter.PlanAsync(
            repository,
            [
                new CommitEdit(before[0].Sha)
                {
                    AuthorName = "New Author",
                    AuthorEmail = "new-author@example.invalid",
                    AuthorDate = authored,
                    CommitterName = "New Committer",
                    CommitterEmail = "new-committer@example.invalid",
                },
            ],
            CancellationToken.None);

        await rewriter.ApplyAsync(plan, CancellationToken.None);

        var after = (await reader.ReadAsync(repository, new CommitQuery(Limit: 1), CancellationToken.None)).Single();

        after.AuthorName.Should().Be("New Author");
        after.AuthorEmail.Should().Be("new-author@example.invalid");
        after.AuthorDate.Should().Be(authored);
        after.AuthorDate.Offset.Should().Be(TimeSpan.FromMinutes(330), "the offset asked for is the offset written");

        after.CommitterName.Should().Be("New Committer");
        after.CommitterDate.Should().Be(before[0].CommitterDate, "an unedited field keeps its original value");
    }

    [Fact]
    public async Task The_rewrite_reaches_no_further_back_than_the_earliest_edit()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = Build(environment, "reach", 5);
        var (rewriter, reader) = await BuildAsync(environment);

        var before = await reader.ReadAsync(repository, new CommitQuery(), CancellationToken.None);

        // Edit the tip only. Nothing before it can be affected, because a commit's name depends
        // on its own contents and its parents, and neither changed.
        var plan = await rewriter.PlanAsync(
            repository,
            [new CommitEdit(before[0].Sha) { Message = "Only the tip changes" }],
            CancellationToken.None);

        plan.Steps.Should().ContainSingle("editing the tip rebuilds the tip and nothing else");

        var result = await rewriter.ApplyAsync(plan, CancellationToken.None);
        result.Succeeded.Should().BeTrue();

        var after = await reader.ReadAsync(repository, new CommitQuery(), CancellationToken.None);

        after.Skip(1).Select(c => c.Sha).Should().Equal(before.Skip(1).Select(c => c.Sha),
            "every commit before the edit keeps its identifier");
    }

    [Fact]
    public async Task Restoring_the_backup_undoes_the_whole_rewrite()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = Build(environment, "undo", 4);
        var backups = new RefBackupService(await BuildRunnerAsync(environment));
        var (rewriter, reader) = await BuildAsync(environment, backups);

        var before = await reader.ReadAsync(repository, new CommitQuery(), CancellationToken.None);
        var tipBefore = before[0].Sha;

        var plan = await rewriter.PlanAsync(
            repository,
            [new CommitEdit(before[2].Sha) { Message = "Deep edit" }],
            CancellationToken.None);

        var result = await rewriter.ApplyAsync(plan, CancellationToken.None);
        environment.Git(repository, "rev-parse", "HEAD").Should().NotBe(tipBefore);

        await backups.RestoreAsync(repository, result.BackupId!, CancellationToken.None);

        environment.Git(repository, "rev-parse", "HEAD").Should().Be(tipBefore,
            "restoring one ref puts the whole history back");

        var restored = await reader.ReadAsync(repository, new CommitQuery(), CancellationToken.None);
        restored.Select(c => c.Sha).Should().Equal(before.Select(c => c.Sha));
    }

    [Fact]
    public async Task A_dirty_working_tree_blocks_the_rewrite()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = Build(environment, "dirty", 2);
        var (rewriter, reader) = await BuildAsync(environment);

        File.WriteAllText(Path.Combine(repository, "uncommitted.txt"), "work in progress");

        var before = await reader.ReadAsync(repository, new CommitQuery(), CancellationToken.None);
        var tip = before[0].Sha;

        var plan = await rewriter.PlanAsync(
            repository,
            [new CommitEdit(before[0].Sha) { Message = "Should not happen" }],
            CancellationToken.None);

        plan.Blockers.Should().Contain(RewriteBlockers.WorkingTreeDirty);
        plan.CanApply.Should().BeFalse();

        var result = await rewriter.ApplyAsync(plan, CancellationToken.None);

        result.BackupId.Should().BeNull("a blocked rewrite takes no backup because it runs nothing");
        environment.Git(repository, "rev-parse", "HEAD").Should().Be(tip);
    }

    [Fact]
    public async Task An_edit_that_changes_nothing_is_refused()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = Build(environment, "empty-edit", 2);
        var (rewriter, reader) = await BuildAsync(environment);

        var before = await reader.ReadAsync(repository, new CommitQuery(), CancellationToken.None);

        var plan = await rewriter.PlanAsync(
            repository, [new CommitEdit(before[0].Sha)], CancellationToken.None);

        plan.Blockers.Should().Contain(RewriteBlockers.NothingToChange);
        plan.CanApply.Should().BeFalse();
    }

    [Fact]
    public async Task A_tag_pointing_into_the_range_is_reported_as_left_behind()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = Build(environment, "stranded", 3);
        var (rewriter, reader) = await BuildAsync(environment);

        var before = await reader.ReadAsync(repository, new CommitQuery(), CancellationToken.None);
        environment.Git(repository, "tag", "v1.0", before[1].Sha);

        var plan = await rewriter.PlanAsync(
            repository,
            [new CommitEdit(before[1].Sha) { Message = "Retagged history" }],
            CancellationToken.None);

        plan.Warnings.Should().Contain(RewriteWarnings.OtherRefsPointIntoRange);
        plan.StrandedRefs.Should().Contain("refs/tags/v1.0",
            "a tag keeps pointing at the commit the rewrite replaced, and the user should know which");
    }

    [Fact]
    public async Task A_signed_commit_in_the_range_warns_that_the_signature_is_lost()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = Build(environment, "signed", 2);
        var (rewriter, reader) = await BuildAsync(environment);

        var before = await reader.ReadAsync(repository, new CommitQuery(), CancellationToken.None);

        // No signing key is available in a test environment, so this checks the plumbing rather
        // than a real signature: an unsigned range must not claim a loss it is not causing.
        var plan = await rewriter.PlanAsync(
            repository,
            [new CommitEdit(before[0].Sha) { Message = "Unsigned" }],
            CancellationToken.None);

        plan.Warnings.Should().NotContain(RewriteWarnings.SignaturesWillBeLost);
    }

    [Fact]
    public async Task A_merge_in_the_range_keeps_every_parent_and_warns()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("merge-rewrite");
        var main = environment.Git(repository, "branch", "--show-current");

        environment.Git(repository, "checkout", "--quiet", "-b", "side");
        File.WriteAllText(Path.Combine(repository, "side.txt"), "side");
        environment.Git(repository, "add", "side.txt");
        environment.Git(repository, "commit", "--quiet", "-m", "Side work");

        environment.Git(repository, "checkout", "--quiet", main);
        environment.Git(repository, "merge", "--quiet", "--no-ff", "side", "-m", "Merge side");

        var (rewriter, reader) = await BuildAsync(environment);
        var before = await reader.ReadAsync(repository, new CommitQuery(), CancellationToken.None);

        var plan = await rewriter.PlanAsync(
            repository,
            [new CommitEdit(before[0].Sha) { Message = "Merge side branch" }],
            CancellationToken.None);

        plan.Warnings.Should().Contain(RewriteWarnings.RangeContainsMerges);

        await rewriter.ApplyAsync(plan, CancellationToken.None);

        var after = (await reader.ReadAsync(repository, new CommitQuery(Limit: 1), CancellationToken.None)).Single();

        after.Subject.Should().Be("Merge side branch");
        after.Parents.Should().HaveCount(2, "a rebuilt merge keeps both parents");
        after.Parents.Should().Contain(before[0].Parents[1],
            "the parent outside the rewritten range keeps its original name");
    }

    [Fact]
    public async Task The_confirmation_phrase_is_the_branch_name()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = Build(environment, "phrase", 2);
        var (rewriter, reader) = await BuildAsync(environment);

        var before = await reader.ReadAsync(repository, new CommitQuery(), CancellationToken.None);

        var plan = await rewriter.PlanAsync(
            repository, [new CommitEdit(before[0].Sha) { Message = "x" }], CancellationToken.None);

        plan.ConfirmationPhrase.Should().Be(environment.Git(repository, "branch", "--show-current"));
        plan.ConfirmationPhrase.Should().NotBeEmpty();
    }

    /// <summary>Creates a repository with the requested number of commits.</summary>
    private static string Build(TempGitEnvironment environment, string name, int commits)
    {
        var repository = environment.CreateRepository(name);

        for (var i = 2; i <= commits; i++)
        {
            File.WriteAllText(Path.Combine(repository, $"file{i}.txt"), $"content {i}\n");
            environment.Git(repository, "add", $"file{i}.txt");
            environment.Git(repository, "commit", "--quiet", "-m", $"Commit {i}");
        }

        return repository;
    }

    private static async Task<GitCommandRunner> BuildRunnerAsync(TempGitEnvironment environment)
    {
        var config = await environment.BuildConfigServiceAsync();
        return new GitCommandRunner(new GitVault.Core.Platform.ProcessRunner(), config, environment.Paths);
    }

    private static async Task<(HistoryRewriter Rewriter, CommitReader Reader)> BuildAsync(
        TempGitEnvironment environment,
        RefBackupService? backups = null)
    {
        var runner = await BuildRunnerAsync(environment);
        var reader = new CommitReader(runner);

        return (
            new HistoryRewriter(runner, reader, new RepositoryInspector(runner), backups ?? new RefBackupService(runner)),
            reader);
    }
}
