using System.Text;
using FluentAssertions;
using GitVault.Core.Profiles;
using GitVault.Core.Repository;
using Xunit;
using Xunit.Abstractions;

namespace GitVault.Core.Tests;

/// <summary>
/// Editing the plain-text files that change how git treats a repository.
/// </summary>
/// <remarks>
/// Ordinary files rather than history, so the safety net is the snapshot rather than a ref backup.
/// What is asserted is that planning writes nothing, that the file's own line ending survives a
/// round trip, that a file GitVault cannot carry back unchanged is refused rather than mangled,
/// and that a snapshot puts the previous version back.
/// </remarks>
public sealed class RepositoryFileEditingTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Planning_a_write_changes_nothing_on_disk()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("plan");
        var path = Path.Combine(repository, ".gitignore");
        File.WriteAllText(path, "bin/\nobj/\n");

        var editor = await BuildAsync(environment);
        var plan = await editor.PlanWriteAsync(
            repository, RepositoryFileKind.Ignore, "bin/\nobj/\n*.user\n", CancellationToken.None);

        plan.CanApply.Should().BeTrue();
        File.ReadAllText(path).Should().Be("bin/\nobj/\n", "planning must not write");
    }

    [Fact]
    public async Task Writing_an_ignore_file_replaces_its_content()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("write");
        var path = Path.Combine(repository, ".gitignore");
        File.WriteAllText(path, "bin/\n");

        var editor = await BuildAsync(environment);
        var plan = await editor.PlanWriteAsync(
            repository, RepositoryFileKind.Ignore, "bin/\nobj/\n", CancellationToken.None);

        var result = await editor.ApplyAsync(plan, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        File.ReadAllText(path).Should().Be("bin/\nobj/\n");

        // The point of editing this file at all: git has to agree it now ignores the thing.
        Directory.CreateDirectory(Path.Combine(repository, "obj"));
        File.WriteAllText(Path.Combine(repository, "obj", "thing.txt"), "x");
        environment.Git(repository, "status", "--porcelain").Should().NotContain("obj/");
    }

    [Fact]
    public async Task A_file_that_does_not_exist_yet_is_created()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("create");
        var path = Path.Combine(repository, ".mailmap");

        var editor = await BuildAsync(environment);
        var plan = await editor.PlanWriteAsync(
            repository,
            RepositoryFileKind.Mailmap,
            "Right Name <right@example.invalid> <wrong@example.invalid>\n",
            CancellationToken.None);

        plan.FilesToSnapshot.Should().BeEmpty("there is nothing yet to preserve");

        await editor.ApplyAsync(plan, CancellationToken.None);

        File.Exists(path).Should().BeTrue();
        File.ReadAllText(path).Should().Contain("right@example.invalid");
    }

    [Fact]
    public async Task A_file_written_with_crlf_keeps_crlf()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("crlf");
        var path = Path.Combine(repository, ".gitattributes");
        File.WriteAllText(path, "*.txt text\r\n");

        var editor = await BuildAsync(environment);

        // The editor hands back text with the newlines a text box produces; the file's own ending
        // is what must survive, because changing it would rewrite every line of the file.
        var plan = await editor.PlanWriteAsync(
            repository, RepositoryFileKind.Attributes, "*.txt text\n*.png binary\n", CancellationToken.None);

        await editor.ApplyAsync(plan, CancellationToken.None);

        var written = File.ReadAllText(path);
        written.Should().Be("*.txt text\r\n*.png binary\r\n");
    }

    [Fact]
    public async Task A_binary_file_is_refused_rather_than_offered_for_editing()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("binary");
        File.WriteAllBytes(Path.Combine(repository, ".gitignore"), [0x00, 0x01, 0x02]);

        var editor = await BuildAsync(environment);

        (await editor.ReadAsync(repository, RepositoryFileKind.Ignore, CancellationToken.None))
            .Should().BeNull();

        var plan = await editor.PlanWriteAsync(
            repository, RepositoryFileKind.Ignore, "bin/\n", CancellationToken.None);

        plan.Blockers.Should().Contain(RepositoryFileBlockers.NotEditableText);
        plan.CanApply.Should().BeFalse();
    }

    [Fact]
    public async Task A_file_in_another_encoding_is_refused_rather_than_re_encoded()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("encoding");

        // Latin-1 bytes that are not valid UTF-8: decoding and writing back would change them.
        File.WriteAllBytes(Path.Combine(repository, ".gitignore"), [0x62, 0x69, 0x6E, 0xE9, 0x0A]);

        var editor = await BuildAsync(environment);

        (await editor.ReadAsync(repository, RepositoryFileKind.Ignore, CancellationToken.None))
            .Should().BeNull("rewriting it would change the whole file's encoding");
    }

    [Fact]
    public async Task A_byte_order_mark_survives_an_edit_rather_than_being_dropped()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        // A mark at the start of a .gitignore is already a latent problem — git treats it as part
        // of the first pattern — but it is the user's problem to fix, not something an editor
        // should silently remove while they were changing a different line.
        var repository = environment.CreateRepository("bom");
        var path = Path.Combine(repository, ".gitignore");
        File.WriteAllBytes(path, [.. Encoding.UTF8.GetPreamble(), .. "bin/\nobj/\n"u8.ToArray()]);

        var editor = await BuildAsync(environment);
        var file = await editor.ReadAsync(repository, RepositoryFileKind.Ignore, CancellationToken.None);

        file.Should().NotBeNull();
        file!.Text[0].Should().Be('\uFEFF', "the mark is carried as text rather than quietly eaten");

        var plan = await editor.PlanWriteAsync(
            repository, RepositoryFileKind.Ignore, file.Text.Replace("obj/", "out/", StringComparison.Ordinal),
            CancellationToken.None);

        await editor.ApplyAsync(plan, CancellationToken.None);

        var written = await File.ReadAllBytesAsync(path);
        written.Take(3).Should().Equal(Encoding.UTF8.GetPreamble(), "the mark is still the first three bytes");
        Encoding.UTF8.GetString(written).Should().Contain("out/");
    }

    [Fact]
    public async Task Restoring_the_snapshot_puts_the_previous_version_back()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("undo");
        var path = Path.Combine(repository, ".gitignore");
        File.WriteAllText(path, "bin/\n");

        var snapshots = new SnapshotService(environment.Paths);
        var editor = await BuildAsync(environment, snapshots);

        var plan = await editor.PlanWriteAsync(
            repository, RepositoryFileKind.Ignore, "everything\n", CancellationToken.None);

        var result = await editor.ApplyAsync(plan, CancellationToken.None);
        result.SnapshotPath.Should().NotBeNull();
        File.ReadAllText(path).Should().Be("everything\n");

        await snapshots.RestoreAsync(result.SnapshotPath!, CancellationToken.None);

        File.ReadAllText(path).Should().Be("bin/\n");
    }

    [Fact]
    public async Task The_private_exclude_file_is_reported_as_untracked()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("exclude");
        File.WriteAllText(Path.Combine(repository, ".gitignore"), "bin/\n");
        environment.Git(repository, "add", ".gitignore");
        environment.Git(repository, "commit", "--quiet", "-m", "Add an ignore file");

        var editor = await BuildAsync(environment);

        var ignore = await editor.ReadAsync(repository, RepositoryFileKind.Ignore, CancellationToken.None);
        var exclude = await editor.ReadAsync(repository, RepositoryFileKind.Exclude, CancellationToken.None);

        ignore!.IsTracked.Should().BeTrue("a change to it would reach everyone else");
        exclude!.IsTracked.Should().BeFalse("the exclude file is this clone's own business");
    }

    [Fact]
    public void A_difference_shows_the_changed_lines_rather_than_the_whole_file()
    {
        var before = string.Join('\n', Enumerable.Range(1, 40).Select(i => "line " + i)) + "\n";
        var after = before.Replace("line 20\n", "line twenty\n", StringComparison.Ordinal);

        var lines = TextDiff.Render(before, after);

        lines.Should().Contain(l => l.Kind == DiffLineKind.Removal && l.Text == "line 20");
        lines.Should().Contain(l => l.Kind == DiffLineKind.Addition && l.Text == "line twenty");
        lines.Count.Should().BeLessThan(12, "a preview nobody reads is not a safety mechanism");
        lines.Should().Contain(l => l.Kind == DiffLineKind.Elision, "the rest of the file is accounted for");
    }

    [Fact]
    public void A_difference_between_identical_text_is_empty()
    {
        TextDiff.Render("same\n", "same\n").Should().BeEmpty();
        TextDiff.Render(null, null).Should().BeEmpty();
    }

    [Fact]
    public void A_trailing_newline_is_a_terminator_rather_than_an_empty_line()
    {
        // Both forms describe the same two lines, so neither is reported as a change to the other
        // beyond the one line that actually differs.
        var lines = TextDiff.Render("a\nb\n", "a\nc\n");

        lines.Where(l => l.Kind is DiffLineKind.Removal).Should().ContainSingle();
        lines.Where(l => l.Kind is DiffLineKind.Addition).Should().ContainSingle();
    }

    private static async Task<RepositoryFileEditor> BuildAsync(
        TempGitEnvironment environment,
        ISnapshotService? snapshots = null)
    {
        var config = await environment.BuildConfigServiceAsync();
        var runner = new GitCommandRunner(new GitVault.Core.Platform.ProcessRunner(), config, environment.Paths);

        return new RepositoryFileEditor(snapshots ?? new SnapshotService(environment.Paths), runner);
    }
}
