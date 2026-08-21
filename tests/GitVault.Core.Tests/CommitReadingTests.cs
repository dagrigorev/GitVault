using FluentAssertions;
using GitVault.Core.Repository;
using Xunit;
using Xunit.Abstractions;

namespace GitVault.Core.Tests;

/// <summary>
/// Reading commit history, against real repositories.
/// </summary>
/// <remarks>
/// This is the foundation the rewriting work sits on, so the bar is higher than "the grid shows
/// something". A rewrite reproduces a commit from what was read: if a date loses its offset, if a
/// message with an awkward byte shifts a field, or if the author and committer are conflated, the
/// rewrite writes something the user did not ask for. Each of those is a test here.
/// </remarks>
public sealed class CommitReadingTests(ITestOutputHelper output)
{
    [Fact]
    public async Task A_commit_is_read_with_every_field_a_rewrite_would_need()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("read");
        var reader = await BuildReaderAsync(environment);

        var commit = await reader.ReadOneAsync(repository, "HEAD", CancellationToken.None);

        commit.Should().NotBeNull();
        commit!.Sha.Should().Be(environment.Git(repository, "rev-parse", "HEAD"));
        commit.TreeSha.Should().Be(environment.Git(repository, "rev-parse", "HEAD^{tree}"));
        commit.Subject.Should().Be("Initial commit");
        commit.Body.Should().BeEmpty();
        commit.AuthorEmail.Should().Be("harness@example.invalid");
        commit.CommitterEmail.Should().Be("harness@example.invalid");
        commit.IsRoot.Should().BeTrue();
        commit.IsMerge.Should().BeFalse();
        commit.Signature.IsPresent.Should().BeFalse();
    }

    [Fact]
    public async Task The_author_date_keeps_the_offset_git_recorded()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("dates");

        // A deliberately unusual offset, so a conversion to local time would be obvious.
        const string authored = "2021-03-04T05:06:07+05:45";
        environment.GitWithEnvironment(
            repository,
            new Dictionary<string, string?>
            {
                ["GIT_AUTHOR_DATE"] = authored,
                ["GIT_COMMITTER_DATE"] = "2022-07-08T09:10:11-03:00",
            },
            "commit", "--quiet", "--allow-empty", "-m", "Timed");

        var reader = await BuildReaderAsync(environment);
        var commit = await reader.ReadOneAsync(repository, "HEAD", CancellationToken.None);

        commit!.AuthorDate.Should().Be(DateTimeOffset.Parse(authored, System.Globalization.CultureInfo.InvariantCulture));
        commit.AuthorDate.Offset.Should().Be(TimeSpan.FromMinutes(345),
            "a rewrite has to reproduce the original offset, not the machine's");

        commit.CommitterDate.Offset.Should().Be(TimeSpan.FromHours(-3));
        commit.CommitterDate.Should().NotBe(commit.AuthorDate,
            "author and committer dates are separate facts");
    }

    [Fact]
    public async Task An_author_who_is_not_the_committer_is_reported_as_both()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("identities");

        environment.GitWithEnvironment(
            repository,
            new Dictionary<string, string?>
            {
                ["GIT_AUTHOR_NAME"] = "Original Author",
                ["GIT_AUTHOR_EMAIL"] = "author@example.invalid",
            },
            "commit", "--quiet", "--allow-empty", "-m", "Applied from a patch");

        var reader = await BuildReaderAsync(environment);
        var commit = await reader.ReadOneAsync(repository, "HEAD", CancellationToken.None);

        commit!.AuthorName.Should().Be("Original Author");
        commit.CommitterName.Should().Be("Temp Harness");
        commit.AuthorDiffersFromCommitter.Should().BeTrue();
        commit.AuthorIdentity.Should().Be("Original Author <author@example.invalid>");
    }

    [Fact]
    public async Task A_message_containing_the_field_separator_does_not_shift_a_field()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("awkward-message");

        // The unit separator this reader uses between fields, inside the message itself. Git
        // accepts arbitrary bytes in a commit message, so this is legal and a parser that
        // splits without a bound would lose the rest of the record.
        const string subject = "Subject with  a unit separator";
        var body = "Body line one\n\nBody line  two\twith a tab";

        environment.Git(repository, "commit", "--quiet", "--allow-empty", "-m", subject, "-m", body);

        var reader = await BuildReaderAsync(environment);
        var commit = await reader.ReadOneAsync(repository, "HEAD", CancellationToken.None);

        commit!.Subject.Should().Be(subject);
        commit.Body.Should().Contain("Body line  two\twith a tab");
        commit.Sha.Should().HaveLength(40, "the fields before the message are still intact");
        commit.AuthorEmail.Should().Be("harness@example.invalid");
    }

    [Fact]
    public async Task A_multi_paragraph_message_keeps_its_shape()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("paragraphs");

        environment.Git(
            repository,
            "commit", "--quiet", "--allow-empty",
            "-m", "Short subject",
            "-m", "First paragraph.",
            "-m", "Second paragraph.");

        var reader = await BuildReaderAsync(environment);
        var commit = await reader.ReadOneAsync(repository, "HEAD", CancellationToken.None);

        commit!.Subject.Should().Be("Short subject");
        commit.Body.Should().Be("First paragraph.\n\nSecond paragraph.");
        commit.FullMessage.Should().StartWith("Short subject\n\nFirst paragraph.");
    }

    [Fact]
    public async Task History_is_returned_newest_first_with_parents()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("history");

        foreach (var name in new[] { "second", "third" })
        {
            File.WriteAllText(Path.Combine(repository, name + ".txt"), name);
            environment.Git(repository, "add", name + ".txt");
            environment.Git(repository, "commit", "--quiet", "-m", "Commit " + name);
        }

        var reader = await BuildReaderAsync(environment);
        var commits = await reader.ReadAsync(repository, new CommitQuery(), CancellationToken.None);

        commits.Should().HaveCount(3);
        commits[0].Subject.Should().Be("Commit third");
        commits[2].Subject.Should().Be("Initial commit");

        // The chain a rewrite would have to rebuild.
        commits[0].Parents.Single().Should().Be(commits[1].Sha);
        commits[1].Parents.Single().Should().Be(commits[2].Sha);
        commits[2].Parents.Should().BeEmpty();
    }

    [Fact]
    public async Task A_merge_commit_reports_every_parent()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("merge");
        var main = environment.Git(repository, "branch", "--show-current");

        environment.Git(repository, "checkout", "--quiet", "-b", "side");
        File.WriteAllText(Path.Combine(repository, "side.txt"), "side");
        environment.Git(repository, "add", "side.txt");
        environment.Git(repository, "commit", "--quiet", "-m", "Side work");

        environment.Git(repository, "checkout", "--quiet", main);
        environment.Git(repository, "merge", "--quiet", "--no-ff", "side", "-m", "Merge side");

        var reader = await BuildReaderAsync(environment);
        var commit = await reader.ReadOneAsync(repository, "HEAD", CancellationToken.None);

        commit!.IsMerge.Should().BeTrue();
        commit.Parents.Should().HaveCount(2, "a rewrite has to reproduce every parent, not just the first");
    }

    [Fact]
    public async Task The_limit_and_the_filters_narrow_the_result()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("filters");

        for (var i = 1; i <= 4; i++)
        {
            File.WriteAllText(Path.Combine(repository, $"f{i}.txt"), "x");
            environment.Git(repository, "add", $"f{i}.txt");
            environment.Git(repository, "commit", "--quiet", "-m", i % 2 == 0 ? "Even change" : "Odd change");
        }

        var reader = await BuildReaderAsync(environment);

        (await reader.ReadAsync(repository, new CommitQuery(Limit: 2), CancellationToken.None))
            .Should().HaveCount(2);

        var even = await reader.ReadAsync(
            repository, new CommitQuery() { MessageFilter = "Even" }, CancellationToken.None);
        even.Should().HaveCount(2).And.OnlyContain(c => c.Subject == "Even change");

        var byPath = await reader.ReadAsync(
            repository, new CommitQuery() { PathFilter = "f3.txt" }, CancellationToken.None);
        byPath.Should().ContainSingle().Which.Subject.Should().Be("Odd change");
    }

    [Fact]
    public async Task The_files_a_commit_touched_are_reported_with_counts_and_renames()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("changes");

        File.WriteAllText(Path.Combine(repository, "kept.txt"), "one\ntwo\nthree\n");
        File.WriteAllText(Path.Combine(repository, "gone.txt"), "delete me\n");
        environment.Git(repository, "add", ".");
        environment.Git(repository, "commit", "--quiet", "-m", "Set up");

        File.WriteAllText(Path.Combine(repository, "kept.txt"), "one\ntwo\nthree\nfour\n");
        File.Delete(Path.Combine(repository, "gone.txt"));
        File.WriteAllText(Path.Combine(repository, "added.txt"), "new file\n");
        environment.Git(repository, "add", "--all");
        environment.Git(repository, "commit", "--quiet", "-m", "Change things");

        var reader = await BuildReaderAsync(environment);
        var changes = await reader.ReadChangesAsync(repository, "HEAD", CancellationToken.None);

        changes.Should().HaveCount(3);

        var modified = changes.Single(c => c.Path == "kept.txt");
        modified.Status.Should().Be(FileChangeStatus.Modified);
        modified.Added.Should().Be(1);
        modified.Removed.Should().Be(0);

        changes.Single(c => c.Path == "added.txt").Status.Should().Be(FileChangeStatus.Added);
        changes.Single(c => c.Path == "gone.txt").Status.Should().Be(FileChangeStatus.Deleted);
    }

    [Fact]
    public async Task A_binary_file_is_reported_as_binary_rather_than_as_zero_lines()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("binary");

        await File.WriteAllBytesAsync(Path.Combine(repository, "blob.bin"), [0, 1, 2, 0, 255, 7]);
        environment.Git(repository, "add", "blob.bin");
        environment.Git(repository, "commit", "--quiet", "-m", "Add a binary file");

        var reader = await BuildReaderAsync(environment);
        var change = (await reader.ReadChangesAsync(repository, "HEAD", CancellationToken.None)).Single();

        change.IsBinary.Should().BeTrue("git reports a dash rather than a line count");
        change.Added.Should().BeNull();
    }

    [Fact]
    public async Task Reading_a_revision_that_does_not_exist_returns_null_rather_than_throwing()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("missing");
        var reader = await BuildReaderAsync(environment);

        (await reader.ReadOneAsync(repository, "no-such-ref", CancellationToken.None)).Should().BeNull();
    }

    private static async Task<CommitReader> BuildReaderAsync(TempGitEnvironment environment)
    {
        var config = await environment.BuildConfigServiceAsync();
        return new CommitReader(
            new GitCommandRunner(new GitVault.Core.Platform.ProcessRunner(), config, environment.Paths));
    }
}
