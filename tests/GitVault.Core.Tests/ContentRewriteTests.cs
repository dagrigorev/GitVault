using FluentAssertions;
using GitVault.Core.Repository;
using Xunit;
using Xunit.Abstractions;

namespace GitVault.Core.Tests;

/// <summary>
/// Editing the content of a file as of an old commit, against real repositories.
/// </summary>
/// <remarks>
/// The properties asserted here are the ones the interface promises. A content edit carries
/// forward into every later commit that did not touch the file, and does so exactly. A later
/// commit that did touch it is merged three ways, and when git cannot combine the two sides the
/// conflict is reported during planning rather than left in the repository. Nothing is written
/// until the plan is applied — not a blob, not a tree, not an index entry — and the working tree
/// is never involved at any point.
/// </remarks>
public sealed class ContentRewriteTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Editing_content_carries_the_change_into_every_later_commit()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        // notes.txt is written once and never touched again, so nothing can conflict.
        var repository = environment.CreateRepository("carry");
        Write(environment, repository, "notes.txt", "alpha\nbeta\n", "Add notes");
        Write(environment, repository, "other.txt", "unrelated\n", "Add something else");
        Write(environment, repository, "third.txt", "more\n", "Add a third file");

        var (rewriter, reader) = await BuildAsync(environment);
        var before = await reader.ReadAsync(repository, new CommitQuery(), CancellationToken.None);
        var target = before.Single(c => c.Subject == "Add notes");

        var plan = await rewriter.PlanAsync(
            repository,
            [new CommitEdit(target.Sha) { Files = [new FileEdit("notes.txt", "alpha\nBETA\n")] }],
            CancellationToken.None);

        plan.Conflicts.Should().BeEmpty("no later commit touches notes.txt");
        plan.CanApply.Should().BeTrue();
        plan.ContentCount.Should().Be(3, "the edited commit and both after it hold the new content");

        var result = await rewriter.ApplyAsync(plan, CancellationToken.None);
        result.Succeeded.Should().BeTrue();

        environment.Git(repository, "show", "HEAD:notes.txt").Should().Contain("BETA");
        environment.Git(repository, "show", "HEAD~2:notes.txt").Should().Contain("BETA");
        environment.Git(repository, "show", "HEAD:other.txt").Should().Contain("unrelated",
            "a file nobody edited must come through untouched");
    }

    [Fact]
    public async Task A_later_commit_that_changes_a_different_part_is_merged_without_asking()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("merge");
        Write(environment, repository, "notes.txt", Lines("one", "two", "three", "four", "five"), "Add notes");
        Write(environment, repository, "notes.txt", Lines("one", "two", "three", "four", "FIVE"), "Change the end");

        var (rewriter, reader) = await BuildAsync(environment);
        var before = await reader.ReadAsync(repository, new CommitQuery(), CancellationToken.None);
        var target = before.Single(c => c.Subject == "Add notes");

        // The edit changes the first line; the later commit changed the last one.
        var plan = await rewriter.PlanAsync(
            repository,
            [new CommitEdit(target.Sha) { Files = [new FileEdit("notes.txt", Lines("ONE", "two", "three", "four", "five"))] }],
            CancellationToken.None);

        plan.Conflicts.Should().BeEmpty("the two changes are far enough apart for git to combine them");
        plan.CanApply.Should().BeTrue();

        await rewriter.ApplyAsync(plan, CancellationToken.None);

        var tip = environment.Git(repository, "show", "HEAD:notes.txt");
        tip.Should().Contain("ONE", "the edit reached the tip");
        tip.Should().Contain("FIVE", "and the later commit's own change survived it");
    }

    [Fact]
    public async Task A_later_commit_that_changes_the_same_lines_is_reported_as_a_conflict()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("conflict");
        Write(environment, repository, "notes.txt", Lines("one", "two", "three"), "Add notes");
        Write(environment, repository, "notes.txt", Lines("one", "TWO", "three"), "Change the middle");

        var tip = environment.Git(repository, "rev-parse", "HEAD");
        var (rewriter, reader) = await BuildAsync(environment);
        var before = await reader.ReadAsync(repository, new CommitQuery(), CancellationToken.None);
        var target = before.Single(c => c.Subject == "Add notes");

        var plan = await rewriter.PlanAsync(
            repository,
            [new CommitEdit(target.Sha) { Files = [new FileEdit("notes.txt", Lines("one", "deux", "three"))] }],
            CancellationToken.None);

        plan.Conflicts.Should().ContainSingle();
        plan.Conflicts[0].Path.Should().Be("notes.txt");
        plan.Conflicts[0].Subject.Should().Be("Change the middle", "the user is told which commit disagrees");
        MergeLabels.HasMarkers(plan.Conflicts[0].MergedText).Should().BeTrue();

        plan.CanApply.Should().BeFalse("a conflict nobody has settled cannot be applied");
        plan.Blockers.Should().Contain(RewriteBlockers.UnresolvedConflicts);

        environment.Git(repository, "rev-parse", "HEAD").Should().Be(tip, "and nothing was written");
        environment.Git(repository, "status", "--porcelain").Should().BeEmpty(
            "planning must never leave the repository in a conflicted state");
    }

    [Fact]
    public async Task A_resolved_conflict_is_what_the_commit_ends_up_with()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("resolve");
        Write(environment, repository, "notes.txt", Lines("one", "two", "three"), "Add notes");
        Write(environment, repository, "notes.txt", Lines("one", "TWO", "three"), "Change the middle");

        var (rewriter, reader) = await BuildAsync(environment);
        var before = await reader.ReadAsync(repository, new CommitQuery(), CancellationToken.None);
        var target = before.Single(c => c.Subject == "Add notes");
        var edits = new[]
        {
            new CommitEdit(target.Sha) { Files = [new FileEdit("notes.txt", Lines("one", "deux", "three"))] },
        };

        var blocked = await rewriter.PlanAsync(repository, edits, CancellationToken.None);
        var conflict = blocked.Conflicts.Single();

        var settled = await rewriter.PlanAsync(
            repository,
            edits,
            [new ConflictResolution(conflict.Sha, conflict.Path, Lines("one", "DEUX", "three"))],
            CancellationToken.None);

        settled.Conflicts.Should().BeEmpty();
        settled.CanApply.Should().BeTrue();

        await rewriter.ApplyAsync(settled, CancellationToken.None);

        environment.Git(repository, "show", "HEAD:notes.txt").Should().Contain("DEUX");
        environment.Git(repository, "show", "HEAD~1:notes.txt").Should().Contain("deux",
            "the edited commit keeps what the user typed for it");
    }

    [Fact]
    public async Task A_content_edit_leaves_every_other_file_byte_identical()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("identical");
        Write(environment, repository, "notes.txt", "alpha\n", "Add notes");
        Write(environment, repository, "keep.txt", "untouched\n", "Add a file to leave alone");

        var (rewriter, reader) = await BuildAsync(environment);
        var before = await reader.ReadAsync(repository, new CommitQuery(), CancellationToken.None);
        var keepBlob = environment.Git(repository, "rev-parse", "HEAD:keep.txt");
        var target = before.Single(c => c.Subject == "Add notes");

        var plan = await rewriter.PlanAsync(
            repository,
            [new CommitEdit(target.Sha) { Files = [new FileEdit("notes.txt", "ALPHA\n")] }],
            CancellationToken.None);

        plan.CanApply.Should().BeTrue();
        await rewriter.ApplyAsync(plan, CancellationToken.None);

        environment.Git(repository, "rev-parse", "HEAD:keep.txt").Should().Be(keepBlob,
            "an untouched file must keep the very same blob, not merely the same text");
    }

    [Fact]
    public async Task Restoring_the_backup_undoes_a_content_rewrite()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("undo");
        Write(environment, repository, "notes.txt", "alpha\n", "Add notes");
        Write(environment, repository, "other.txt", "beta\n", "Add a second file");

        var runner = await BuildRunnerAsync(environment);
        var backups = new RefBackupService(runner);
        var (rewriter, reader) = await BuildAsync(environment, backups);

        var tip = environment.Git(repository, "rev-parse", "HEAD");
        var before = await reader.ReadAsync(repository, new CommitQuery(), CancellationToken.None);
        var target = before.Single(c => c.Subject == "Add notes");

        var plan = await rewriter.PlanAsync(
            repository,
            [new CommitEdit(target.Sha) { Files = [new FileEdit("notes.txt", "ALPHA\n")] }],
            CancellationToken.None);

        plan.CanApply.Should().BeTrue();

        var result = await rewriter.ApplyAsync(plan, CancellationToken.None);
        result.BackupId.Should().NotBeNull();
        environment.Git(repository, "rev-parse", "HEAD").Should().NotBe(tip);

        await backups.RestoreAsync(repository, result.BackupId!, CancellationToken.None);

        environment.Git(repository, "rev-parse", "HEAD").Should().Be(tip, "the whole rewrite is one ref away");
        environment.Git(repository, "show", "HEAD:notes.txt").Should().Contain("alpha");
    }

    [Fact]
    public async Task Editing_a_file_a_later_commit_deletes_is_refused()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("deleted");
        Write(environment, repository, "notes.txt", "alpha\n", "Add notes");
        File.Delete(Path.Combine(repository, "notes.txt"));
        environment.Git(repository, "add", "-A");
        environment.Git(repository, "commit", "--quiet", "-m", "Remove notes");

        var (rewriter, reader) = await BuildAsync(environment);
        var before = await reader.ReadAsync(repository, new CommitQuery(), CancellationToken.None);
        var target = before.Single(c => c.Subject == "Add notes");

        var plan = await rewriter.PlanAsync(
            repository,
            [new CommitEdit(target.Sha) { Files = [new FileEdit("notes.txt", "ALPHA\n")] }],
            CancellationToken.None);

        plan.Blockers.Should().Contain(RewriteBlockers.PathRemovedLater);
        plan.CanApply.Should().BeFalse("there is no later file to carry the edit into");
    }

    [Fact]
    public async Task A_binary_file_is_refused_rather_than_mangled()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("binary");
        File.WriteAllBytes(Path.Combine(repository, "blob.bin"), [0x00, 0x01, 0x02, 0xFF, 0x00]);
        environment.Git(repository, "add", "blob.bin");
        environment.Git(repository, "commit", "--quiet", "-m", "Add something binary");
        Write(environment, repository, "after.txt", "later\n", "Add a later commit");

        var (rewriter, reader) = await BuildAsync(environment);
        var before = await reader.ReadAsync(repository, new CommitQuery(), CancellationToken.None);
        var target = before.Single(c => c.Subject == "Add something binary");

        var plan = await rewriter.PlanAsync(
            repository,
            [new CommitEdit(target.Sha) { Files = [new FileEdit("blob.bin", "text now")] }],
            CancellationToken.None);

        plan.Blockers.Should().Contain(RewriteBlockers.PathIsNotEditableText);
        plan.CanApply.Should().BeFalse();
    }

    [Fact]
    public async Task An_executable_file_keeps_its_mode()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("mode");
        Write(environment, repository, "run.sh", "#!/bin/sh\necho one\n", "Add a script");
        environment.Git(repository, "update-index", "--chmod=+x", "run.sh");
        environment.Git(repository, "commit", "--quiet", "-m", "Make it executable");
        Write(environment, repository, "after.txt", "later\n", "Add a later commit");

        environment.Git(repository, "ls-tree", "HEAD", "--", "run.sh").Should().StartWith("100755");

        var (rewriter, reader) = await BuildAsync(environment);
        var before = await reader.ReadAsync(repository, new CommitQuery(), CancellationToken.None);
        var target = before.Single(c => c.Subject == "Make it executable");

        var plan = await rewriter.PlanAsync(
            repository,
            [new CommitEdit(target.Sha) { Files = [new FileEdit("run.sh", "#!/bin/sh\necho two\n")] }],
            CancellationToken.None);

        plan.CanApply.Should().BeTrue();
        await rewriter.ApplyAsync(plan, CancellationToken.None);

        environment.Git(repository, "ls-tree", "HEAD", "--", "run.sh").Should().StartWith("100755",
            "editing a file must not quietly take its executable bit away");
        environment.Git(repository, "show", "HEAD:run.sh").Should().Contain("echo two");
    }

    [Fact]
    public async Task A_file_in_a_subdirectory_is_edited_in_place()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("nested");
        Directory.CreateDirectory(Path.Combine(repository, "src", "deep"));
        Write(environment, repository, "src/deep/notes.txt", "alpha\n", "Add a nested file");
        Write(environment, repository, "after.txt", "later\n", "Add a later commit");

        var (rewriter, reader) = await BuildAsync(environment);
        var before = await reader.ReadAsync(repository, new CommitQuery(), CancellationToken.None);
        var target = before.Single(c => c.Subject == "Add a nested file");

        var plan = await rewriter.PlanAsync(
            repository,
            [new CommitEdit(target.Sha) { Files = [new FileEdit("src/deep/notes.txt", "ALPHA\n")] }],
            CancellationToken.None);

        plan.CanApply.Should().BeTrue();
        await rewriter.ApplyAsync(plan, CancellationToken.None);

        environment.Git(repository, "show", "HEAD:src/deep/notes.txt").Should().Contain("ALPHA");
        environment.Git(repository, "show", "HEAD:after.txt").Should().Contain("later");
    }

    [Fact]
    public async Task Content_and_metadata_can_be_changed_in_the_same_rewrite()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("both");
        Write(environment, repository, "notes.txt", "alpha\n", "Add notes");
        Write(environment, repository, "after.txt", "later\n", "Add a later commit");

        var (rewriter, reader) = await BuildAsync(environment);
        var before = await reader.ReadAsync(repository, new CommitQuery(), CancellationToken.None);
        var target = before.Single(c => c.Subject == "Add notes");

        var plan = await rewriter.PlanAsync(
            repository,
            [
                new CommitEdit(target.Sha)
                {
                    Message = "Add notes, properly",
                    AuthorName = "Someone Else",
                    Files = [new FileEdit("notes.txt", "ALPHA\n")],
                },
            ],
            CancellationToken.None);

        plan.CanApply.Should().BeTrue();
        await rewriter.ApplyAsync(plan, CancellationToken.None);

        environment.Git(repository, "show", "HEAD~1:notes.txt").Should().Contain("ALPHA");
        environment.Git(repository, "log", "-1", "--format=%s", "HEAD~1").Should().Be("Add notes, properly");
        environment.Git(repository, "log", "-1", "--format=%an", "HEAD~1").Should().Be("Someone Else");
    }

    [Fact]
    public async Task Planning_a_content_edit_writes_no_object_and_touches_no_index()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("nowrite");
        Write(environment, repository, "notes.txt", Lines("one", "two", "three"), "Add notes");
        Write(environment, repository, "notes.txt", Lines("one", "two", "THREE"), "Change the end");

        var (rewriter, reader) = await BuildAsync(environment);
        var before = await reader.ReadAsync(repository, new CommitQuery(), CancellationToken.None);
        var target = before.Single(c => c.Subject == "Add notes");

        var indexPath = Path.Combine(repository, ".git", "index");
        var indexStamp = File.GetLastWriteTimeUtc(indexPath);
        var objectsBefore = CountObjects(repository);

        var plan = await rewriter.PlanAsync(
            repository,
            [new CommitEdit(target.Sha) { Files = [new FileEdit("notes.txt", Lines("ONE", "two", "three"))] }],
            CancellationToken.None);

        plan.CanApply.Should().BeTrue("this is the case that would be applied, so it is the one worth checking");

        CountObjects(repository).Should().Be(objectsBefore, "a preview must not write a blob or a tree");
        File.GetLastWriteTimeUtc(indexPath).Should().Be(indexStamp, "and must not touch the index");
        environment.Git(repository, "status", "--porcelain").Should().BeEmpty();
    }

    /// <summary>Counts the loose objects in the repository.</summary>
    private static int CountObjects(string repository) =>
        Directory.Exists(Path.Combine(repository, ".git", "objects"))
            ? Directory
                .EnumerateFiles(Path.Combine(repository, ".git", "objects"), "*", SearchOption.AllDirectories)
                .Count(f => !f.Contains("info", StringComparison.Ordinal) && !f.Contains("pack", StringComparison.Ordinal))
            : 0;

    /// <summary>Joins lines with the newline git stores, whatever this machine prefers.</summary>
    private static string Lines(params string[] lines) => string.Join('\n', lines) + "\n";

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

    private static async Task<(HistoryRewriter Rewriter, CommitReader Reader)> BuildAsync(
        TempGitEnvironment environment,
        RefBackupService? backups = null)
    {
        var runner = await BuildRunnerAsync(environment);
        var reader = new CommitReader(runner);

        return (
            new HistoryRewriter(
                runner,
                reader,
                new RepositoryInspector(runner),
                backups ?? new RefBackupService(runner),
                new ContentMerger(runner),
                new TreeBuilder(runner)),
            reader);
    }
}
