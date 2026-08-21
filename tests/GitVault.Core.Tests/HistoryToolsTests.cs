using FluentAssertions;
using GitVault.Core.Repository;
using Xunit;
using Xunit.Abstractions;

namespace GitVault.Core.Tests;

/// <summary>
/// Operations that reach across a branch's whole history, against real repositories.
/// </summary>
/// <remarks>
/// These are the jobs people reach for <c>filter-repo</c> to do, and the assertions are about the
/// promises rather than the mechanics: a purged file is gone from every commit that held it, every
/// other file comes through as the very same blob, the commits that only touched it are kept
/// rather than dropped, and one ref puts all of it back.
///
/// The purge case is also the one with a security claim attached, so the warning that says the
/// content survives elsewhere is asserted like any other behaviour.
/// </remarks>
public sealed class HistoryToolsTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Removing_a_path_takes_it_out_of_every_commit_that_held_it()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("purge");
        Write(environment, repository, "secret.key", "PRIVATE KEY\n", "Add a key by accident");
        Write(environment, repository, "app.txt", "code\n", "Add some code");
        Write(environment, repository, "app.txt", "more code\n", "Change the code");

        var tools = await BuildAsync(environment);
        var plan = await tools.PlanRemovePathAsync(repository, "secret.key", CancellationToken.None);

        plan.CanApply.Should().BeTrue();
        plan.Warnings.Should().Contain(RewriteWarnings.RemovedContentSurvives,
            "a purge must never imply the secret is now safe");

        var rewriter = await BuildRewriterAsync(environment);
        var result = await rewriter.ApplyAsync(plan, CancellationToken.None);
        result.Succeeded.Should().BeTrue();

        var branch = environment.Git(repository, "log", "--name-only", "--format=", "HEAD");
        branch.Should().NotContain("secret.key", "no commit on the branch holds it any more");

        environment.Git(repository, "show", "HEAD:app.txt").Should().Contain("more code",
            "everything else must come through untouched");
    }

    [Fact]
    public async Task A_purged_file_is_still_reachable_through_the_backup_ref()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        // This is the honest half of a purge, and the reason the warning exists. The backup ref is
        // what makes the operation undoable, and it keeps the old blobs reachable for exactly as
        // long as it is there. A secret that reached a commit has to be revoked, not deleted.
        var repository = environment.CreateRepository("purge-survives");
        Write(environment, repository, "secret.key", "PRIVATE KEY\n", "Add a key by accident");
        Write(environment, repository, "app.txt", "code\n", "Add some code");

        var runner = await BuildRunnerAsync(environment);
        var backups = new RefBackupService(runner);
        var tools = await BuildAsync(environment, backups);
        var rewriter = await BuildRewriterAsync(environment, backups);

        var plan = await tools.PlanRemovePathAsync(repository, "secret.key", CancellationToken.None);
        plan.Warnings.Should().Contain(RewriteWarnings.RemovedContentSurvives);

        await rewriter.ApplyAsync(plan, CancellationToken.None);

        environment.Git(repository, "log", "--name-only", "--format=", "HEAD")
            .Should().NotContain("secret.key");

        environment.Git(repository, "log", "--all", "--name-only", "--format=")
            .Should().Contain("secret.key",
                "the backup ref still holds it, which is what makes the purge reversible");
    }

    [Fact]
    public async Task Removing_a_directory_takes_every_file_under_it()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("purge-dir");
        Write(environment, repository, "secrets/one.key", "first\n", "Add the first key");
        Write(environment, repository, "secrets/two.key", "second\n", "Add the second key");
        Write(environment, repository, "app.txt", "code\n", "Add some code");

        var tools = await BuildAsync(environment);
        var plan = await tools.PlanRemovePathAsync(repository, "secrets", CancellationToken.None);

        plan.CanApply.Should().BeTrue();

        var rewriter = await BuildRewriterAsync(environment);
        await rewriter.ApplyAsync(plan, CancellationToken.None);

        environment.Git(repository, "log", "--name-only", "--format=", "HEAD")
            .Should().NotContain("secrets/");

        environment.Git(repository, "ls-tree", "-r", "--name-only", "HEAD").Should().Contain("app.txt");
    }

    [Fact]
    public async Task A_binary_file_can_be_purged_even_though_it_cannot_be_edited()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        // The case the feature exists for: something that was never text and must still go.
        var repository = environment.CreateRepository("purge-binary");
        File.WriteAllBytes(Path.Combine(repository, "dump.bin"), [0x00, 0x01, 0xFF, 0x00, 0x7F]);
        environment.Git(repository, "add", "dump.bin");
        environment.Git(repository, "commit", "--quiet", "-m", "Add a binary by accident");
        Write(environment, repository, "app.txt", "code\n", "Add some code");

        var tools = await BuildAsync(environment);
        var plan = await tools.PlanRemovePathAsync(repository, "dump.bin", CancellationToken.None);

        plan.CanApply.Should().BeTrue();

        var rewriter = await BuildRewriterAsync(environment);
        await rewriter.ApplyAsync(plan, CancellationToken.None);

        environment.Git(repository, "log", "--name-only", "--format=", "HEAD").Should().NotContain("dump.bin");
    }

    [Fact]
    public async Task A_commit_that_only_touched_the_removed_path_is_kept_and_reported()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("emptied");
        Write(environment, repository, "secret.key", "one\n", "Add a key");
        Write(environment, repository, "secret.key", "two\n", "Change the key");
        Write(environment, repository, "app.txt", "code\n", "Add some code");

        var before = int.Parse(
            environment.Git(repository, "rev-list", "--count", "HEAD"),
            System.Globalization.CultureInfo.InvariantCulture);

        var tools = await BuildAsync(environment);
        var plan = await tools.PlanRemovePathAsync(repository, "secret.key", CancellationToken.None);

        plan.Warnings.Should().Contain(RewriteWarnings.CommitsBecomeEmpty,
            "the user is told that some commits will hold what their parent holds");

        var rewriter = await BuildRewriterAsync(environment);
        await rewriter.ApplyAsync(plan, CancellationToken.None);

        var after = int.Parse(
            environment.Git(repository, "rev-list", "--count", "HEAD"),
            System.Globalization.CultureInfo.InvariantCulture);

        after.Should().Be(before,
            "GitVault does not delete commits nobody asked it to delete, even empty ones");
    }

    [Fact]
    public async Task Removing_a_path_that_is_nowhere_in_history_is_refused()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("absent");
        Write(environment, repository, "app.txt", "code\n", "Add some code");

        var tools = await BuildAsync(environment);
        var plan = await tools.PlanRemovePathAsync(repository, "nothing/here.txt", CancellationToken.None);

        plan.Blockers.Should().Contain(RewriteBlockers.PathNotInHistory);
        plan.CanApply.Should().BeFalse();
    }

    [Fact]
    public async Task Restoring_the_backup_undoes_a_purge()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("purge-undo");
        Write(environment, repository, "secret.key", "PRIVATE KEY\n", "Add a key by accident");
        Write(environment, repository, "app.txt", "code\n", "Add some code");

        var runner = await BuildRunnerAsync(environment);
        var backups = new RefBackupService(runner);
        var tools = await BuildAsync(environment, backups);
        var rewriter = await BuildRewriterAsync(environment, backups);

        var tip = environment.Git(repository, "rev-parse", "HEAD");

        var plan = await tools.PlanRemovePathAsync(repository, "secret.key", CancellationToken.None);
        var result = await rewriter.ApplyAsync(plan, CancellationToken.None);

        result.BackupId.Should().NotBeNull();
        environment.Git(repository, "rev-parse", "HEAD").Should().NotBe(tip);

        await backups.RestoreAsync(repository, result.BackupId!, CancellationToken.None);

        environment.Git(repository, "rev-parse", "HEAD").Should().Be(tip);
        environment.Git(repository, "show", "HEAD~1:secret.key").Should().Contain("PRIVATE KEY");
    }

    [Fact]
    public async Task Renaming_a_path_moves_it_in_every_commit_and_keeps_the_blob()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("rename");
        Write(environment, repository, "notes.txt", "alpha\n", "Add notes");
        Write(environment, repository, "app.txt", "code\n", "Add some code");

        var blob = environment.Git(repository, "rev-parse", "HEAD:notes.txt");

        var tools = await BuildAsync(environment);
        var plan = await tools.PlanRenamePathAsync(repository, "notes.txt", "docs/notes.txt", CancellationToken.None);

        plan.CanApply.Should().BeTrue();

        var rewriter = await BuildRewriterAsync(environment);
        await rewriter.ApplyAsync(plan, CancellationToken.None);

        environment.Git(repository, "rev-parse", "HEAD:docs/notes.txt").Should().Be(blob,
            "a rename moves the entry rather than rewriting the content");

        environment.Git(repository, "ls-tree", "-r", "--name-only", "HEAD").Should().NotContain("\nnotes.txt");
        environment.Git(repository, "ls-tree", "-r", "--name-only", "HEAD~1").Should().Contain("docs/notes.txt",
            "the file reads as though it had always lived there");
    }

    [Fact]
    public async Task Renaming_onto_a_path_a_commit_already_holds_is_refused()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("collide");
        Write(environment, repository, "notes.txt", "alpha\n", "Add notes");
        Write(environment, repository, "other.txt", "beta\n", "Add another file");

        var tools = await BuildAsync(environment);
        var plan = await tools.PlanRenamePathAsync(repository, "notes.txt", "other.txt", CancellationToken.None);

        plan.Blockers.Should().Contain(RewriteBlockers.RenameTargetExists);
        plan.CanApply.Should().BeFalse("landing on an existing file would replace it silently");
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("../outside.txt")]
    [InlineData(".git/config")]
    [InlineData("--force")]
    public async Task A_path_that_does_not_mean_what_it_looks_like_is_refused(string target)
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("badpath-" + Math.Abs(target.GetHashCode(StringComparison.Ordinal)));
        Write(environment, repository, "notes.txt", "alpha\n", "Add notes");

        var tools = await BuildAsync(environment);
        var plan = await tools.PlanRenamePathAsync(repository, "notes.txt", target, CancellationToken.None);

        plan.Blockers.Should().Contain(RewriteBlockers.PathNotValid);
        plan.CanApply.Should().BeFalse();
    }

    [Fact]
    public async Task Replacing_an_identity_corrects_every_commit_that_carries_it()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("identity");
        environment.Git(repository, "config", "user.email", "wrong@example.invalid");
        environment.Git(repository, "config", "user.name", "Wrong Name");
        Write(environment, repository, "a.txt", "one\n", "First");
        Write(environment, repository, "b.txt", "two\n", "Second");

        var tools = await BuildAsync(environment);
        var plan = await tools.PlanReplaceIdentityAsync(
            repository, "wrong@example.invalid", "Right Name", "right@example.invalid", CancellationToken.None);

        plan.CanApply.Should().BeTrue();
        plan.EditedCount.Should().Be(2, "both commits carry the wrong address");

        var rewriter = await BuildRewriterAsync(environment);
        await rewriter.ApplyAsync(plan, CancellationToken.None);

        environment.Git(repository, "log", "--format=%ae %ce", "-2")
            .Should().NotContain("wrong@example.invalid");

        environment.Git(repository, "log", "-1", "--format=%an").Should().Be("Right Name");
        environment.Git(repository, "log", "-1", "--format=%ae").Should().Be("right@example.invalid");
    }

    [Fact]
    public async Task Replacing_an_identity_leaves_other_people_alone()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("identity-others");
        environment.Git(repository, "config", "user.email", "wrong@example.invalid");
        environment.Git(repository, "config", "user.name", "Wrong Name");
        Write(environment, repository, "a.txt", "one\n", "Mine");

        environment.Git(repository, "config", "user.email", "colleague@example.invalid");
        environment.Git(repository, "config", "user.name", "A Colleague");
        Write(environment, repository, "b.txt", "two\n", "Theirs");

        var tools = await BuildAsync(environment);
        var plan = await tools.PlanReplaceIdentityAsync(
            repository, "wrong@example.invalid", "Right Name", "right@example.invalid", CancellationToken.None);

        plan.EditedCount.Should().Be(1, "only the commits carrying that address are edited");

        var rewriter = await BuildRewriterAsync(environment);
        await rewriter.ApplyAsync(plan, CancellationToken.None);

        environment.Git(repository, "log", "-1", "--format=%ae").Should().Be("colleague@example.invalid",
            "someone else's authorship is not the user's to reassign");
        environment.Git(repository, "log", "-1", "--format=%ae", "HEAD~1").Should().Be("right@example.invalid");
    }

    [Fact]
    public async Task Replacing_an_identity_nobody_used_is_refused()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("identity-absent");
        Write(environment, repository, "a.txt", "one\n", "First");

        var tools = await BuildAsync(environment);
        var plan = await tools.PlanReplaceIdentityAsync(
            repository, "nobody@example.invalid", "Right Name", "right@example.invalid", CancellationToken.None);

        plan.Blockers.Should().Contain(RewriteBlockers.IdentityNotFound);
        plan.CanApply.Should().BeFalse();
    }

    [Fact]
    public async Task Planning_a_path_operation_writes_nothing()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("readonly");
        Write(environment, repository, "secret.key", "PRIVATE KEY\n", "Add a key by accident");
        Write(environment, repository, "app.txt", "code\n", "Add some code");

        var tip = environment.Git(repository, "rev-parse", "HEAD");
        var indexPath = Path.Combine(repository, ".git", "index");
        var indexStamp = File.GetLastWriteTimeUtc(indexPath);

        var tools = await BuildAsync(environment);
        var plan = await tools.PlanRemovePathAsync(repository, "secret.key", CancellationToken.None);

        plan.CanApply.Should().BeTrue();
        environment.Git(repository, "rev-parse", "HEAD").Should().Be(tip);
        File.GetLastWriteTimeUtc(indexPath).Should().Be(indexStamp, "planning must not touch the index");
        environment.Git(repository, "status", "--porcelain").Should().BeEmpty();
    }

    /// <summary>Writes a file and commits it.</summary>
    private static void Write(
        TempGitEnvironment environment,
        string repository,
        string path,
        string content,
        string subject)
    {
        var full = Path.Combine(repository, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);

        environment.Git(repository, "add", path);
        environment.Git(repository, "commit", "--quiet", "-m", subject);
    }

    private static async Task<GitCommandRunner> BuildRunnerAsync(TempGitEnvironment environment)
    {
        var config = await environment.BuildConfigServiceAsync();
        return new GitCommandRunner(new GitVault.Core.Platform.ProcessRunner(), config, environment.Paths);
    }

    private static async Task<HistoryRewriter> BuildRewriterAsync(
        TempGitEnvironment environment,
        RefBackupService? backups = null)
    {
        var runner = await BuildRunnerAsync(environment);

        return new HistoryRewriter(
            runner,
            new CommitReader(runner),
            new RepositoryInspector(runner),
            backups ?? new RefBackupService(runner),
            new ContentMerger(runner),
            new TreeBuilder(runner));
    }

    private static async Task<HistoryTools> BuildAsync(
        TempGitEnvironment environment,
        RefBackupService? backups = null)
    {
        var runner = await BuildRunnerAsync(environment);

        return new HistoryTools(
            runner,
            new CommitReader(runner),
            await BuildRewriterAsync(environment, backups),
            new RepositoryInspector(runner));
    }
}
