using FluentAssertions;
using GitVault.Core.Profiles;
using GitVault.Core.Repository;
using Xunit;
using Xunit.Abstractions;

namespace GitVault.Core.Tests;

/// <summary>
/// Editing hook scripts, against real repositories.
/// </summary>
/// <remarks>
/// A hook is a program git runs by itself, so these assertions are about containment rather than
/// convenience: the directory git actually uses is the one written to, a name that could climb out
/// of it is refused, disabling leaves nothing runnable behind, and a snapshot puts the previous
/// script back.
///
/// Nothing here runs a hook, and neither does the editor. The tests that involve git committing
/// are the only place a hook could execute, and they are arranged so that it would be visible if
/// one did.
/// </remarks>
public sealed class HookEditingTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Hooks_are_listed_from_the_directory_git_actually_uses()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("list");
        var editor = await BuildAsync(environment);

        var directory = await editor.ListAsync(repository, CancellationToken.None);

        directory.IsRedirected.Should().BeFalse();
        directory.Directory.Should().Be(Path.Combine(repository, ".git", "hooks"));
        directory.Hooks.Should().Contain(h => h.Name == "pre-commit");
        directory.Hooks.Should().OnlyContain(h => !h.Name.EndsWith(".sample", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_redirected_hooks_path_is_followed_rather_than_assumed()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        // The trap this avoids: writing to .git/hooks and reporting success while git runs
        // something else entirely.
        var repository = environment.CreateRepository("redirected");
        var elsewhere = Path.Combine(repository, "tools", "hooks");
        Directory.CreateDirectory(elsewhere);
        environment.Git(repository, "config", "core.hooksPath", "tools/hooks");

        var editor = await BuildAsync(environment);
        var directory = await editor.ListAsync(repository, CancellationToken.None);

        directory.IsRedirected.Should().BeTrue();
        directory.Directory.Should().Be(Path.GetFullPath(elsewhere));

        var plan = await editor.PlanWriteAsync(
            repository, "pre-commit", "#!/bin/sh\nexit 0\n", true, CancellationToken.None);

        await editor.ApplyAsync(plan, CancellationToken.None);

        File.Exists(Path.Combine(elsewhere, "pre-commit")).Should().BeTrue();
        File.Exists(Path.Combine(repository, ".git", "hooks", "pre-commit")).Should().BeFalse();
    }

    [Fact]
    public async Task Planning_a_hook_writes_nothing()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("plan");
        var editor = await BuildAsync(environment);

        var plan = await editor.PlanWriteAsync(
            repository, "pre-commit", "#!/bin/sh\nexit 0\n", true, CancellationToken.None);

        plan.CanApply.Should().BeTrue();
        File.Exists(Path.Combine(repository, ".git", "hooks", "pre-commit")).Should().BeFalse();
    }

    [Fact]
    public async Task A_written_hook_is_enabled_and_executable()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("write");
        var editor = await BuildAsync(environment);

        var plan = await editor.PlanWriteAsync(
            repository, "pre-commit", "#!/bin/sh\nexit 0\n", true, CancellationToken.None);

        var result = await editor.ApplyAsync(plan, CancellationToken.None);
        result.Succeeded.Should().BeTrue();

        var directory = await editor.ListAsync(repository, CancellationToken.None);
        var hook = directory.Hooks.Single(h => h.Name == "pre-commit");

        hook.Exists.Should().BeTrue();
        hook.IsEnabled.Should().BeTrue();
        hook.IsExecutable.Should().BeTrue("git skips a hook without the bit, silently");
        hook.IsInertlyDisabled.Should().BeFalse();
    }

    [Fact]
    public async Task Disabling_a_hook_leaves_nothing_git_would_run()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("disable");
        var hooks = Path.Combine(repository, ".git", "hooks");
        var editor = await BuildAsync(environment);

        await editor.ApplyAsync(
            await editor.PlanWriteAsync(repository, "pre-commit", "#!/bin/sh\nexit 0\n", true, CancellationToken.None),
            CancellationToken.None);

        File.Exists(Path.Combine(hooks, "pre-commit")).Should().BeTrue();

        await editor.ApplyAsync(
            await editor.PlanWriteAsync(repository, "pre-commit", "#!/bin/sh\nexit 0\n", false, CancellationToken.None),
            CancellationToken.None);

        File.Exists(Path.Combine(hooks, "pre-commit")).Should().BeFalse(
            "a disable that left a live copy behind would be the worst outcome of that dialog");

        File.Exists(Path.Combine(hooks, "pre-commit.sample")).Should().BeTrue();

        var directory = await editor.ListAsync(repository, CancellationToken.None);
        directory.Hooks.Single(h => h.Name == "pre-commit").IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task An_enabled_hook_is_the_one_git_runs()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        // The only end-to-end proof that any of this works: git has to refuse the commit.
        var repository = environment.CreateRepository("runs");
        var editor = await BuildAsync(environment);

        await editor.ApplyAsync(
            await editor.PlanWriteAsync(
                repository, "pre-commit", "#!/bin/sh\nexit 1\n", true, CancellationToken.None),
            CancellationToken.None);

        File.WriteAllText(Path.Combine(repository, "thing.txt"), "x\n");
        environment.Git(repository, "add", "thing.txt");

        var committed = environment.TryGit(repository, "commit", "--quiet", "-m", "Should not happen");

        committed.Should().BeFalse("the hook GitVault wrote is the one git ran, and it refused");
    }

    [Fact]
    public async Task Deleting_a_hook_removes_it_and_the_snapshot_puts_it_back()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("delete");
        var snapshots = new SnapshotService(environment.Paths);
        var editor = await BuildAsync(environment, snapshots);
        var path = Path.Combine(repository, ".git", "hooks", "pre-push");

        await editor.ApplyAsync(
            await editor.PlanWriteAsync(repository, "pre-push", "#!/bin/sh\necho hi\n", true, CancellationToken.None),
            CancellationToken.None);

        var plan = await editor.PlanDeleteAsync(repository, "pre-push", CancellationToken.None);
        var result = await editor.ApplyAsync(plan, CancellationToken.None);

        File.Exists(path).Should().BeFalse();
        result.SnapshotPath.Should().NotBeNull();

        await snapshots.RestoreAsync(result.SnapshotPath!, CancellationToken.None);

        File.Exists(path).Should().BeTrue();
        File.ReadAllText(path).Should().Contain("echo hi");
    }

    [Fact]
    public async Task Deleting_a_hook_that_is_not_there_is_refused()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        // A name git ships no sample for: pre-commit has one, and deleting that sample would be a
        // real deletion rather than the "nothing to delete" this is about.
        var repository = environment.CreateRepository("absent");
        var editor = await BuildAsync(environment);

        var plan = await editor.PlanDeleteAsync(repository, "post-commit", CancellationToken.None);

        plan.Blockers.Should().Contain(HookBlockers.HookNotFound);
        plan.CanApply.Should().BeFalse();
    }

    [Theory]
    [InlineData("../../evil")]
    [InlineData("/etc/profile")]
    [InlineData(".hidden")]
    [InlineData("-rf")]
    [InlineData("pre-commit.sample")]
    [InlineData("has space")]
    public async Task A_name_that_could_write_outside_the_hooks_directory_is_refused(string name)
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository(
            "badname-" + Math.Abs(name.GetHashCode(StringComparison.Ordinal)));

        var editor = await BuildAsync(environment);

        var plan = await editor.PlanWriteAsync(
            repository, name, "#!/bin/sh\nexit 0\n", true, CancellationToken.None);

        plan.Blockers.Should().Contain(HookBlockers.NameNotValid);
        plan.CanApply.Should().BeFalse("writing an executable to an arbitrary path is the thing to prevent");
    }

    [Fact]
    public async Task A_compiled_hook_is_refused_rather_than_offered_as_text()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("compiled");
        var hooks = Path.Combine(repository, ".git", "hooks");
        Directory.CreateDirectory(hooks);
        File.WriteAllBytes(Path.Combine(hooks, "pre-commit"), [0x7F, 0x45, 0x4C, 0x46, 0x00, 0x01]);

        var editor = await BuildAsync(environment);

        (await editor.ReadAsync(repository, "pre-commit", CancellationToken.None)).Should().BeNull();

        var plan = await editor.PlanWriteAsync(
            repository, "pre-commit", "#!/bin/sh\nexit 0\n", true, CancellationToken.None);

        plan.Blockers.Should().Contain(HookBlockers.NotEditableText,
            "replacing a binary hook with text would destroy it without saying so");
    }

    [Fact]
    public async Task The_sample_hooks_git_ships_are_listed_as_disabled()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("samples");
        var editor = await BuildAsync(environment);

        var directory = await editor.ListAsync(repository, CancellationToken.None);
        var samples = directory.Hooks.Where(h => h.Exists && !h.IsEnabled).ToList();

        // git init ships a handful of .sample files; whichever this git version ships, they are
        // present and none of them is reported as something git would run.
        samples.Should().NotBeEmpty();
        samples.Should().OnlyContain(h => h.Path.EndsWith(".sample", StringComparison.Ordinal));
    }

    private static async Task<HookEditor> BuildAsync(
        TempGitEnvironment environment,
        ISnapshotService? snapshots = null)
    {
        var config = await environment.BuildConfigServiceAsync();
        var runner = new GitCommandRunner(new GitVault.Core.Platform.ProcessRunner(), config, environment.Paths);

        return new HookEditor(runner, snapshots ?? new SnapshotService(environment.Paths));
    }
}
