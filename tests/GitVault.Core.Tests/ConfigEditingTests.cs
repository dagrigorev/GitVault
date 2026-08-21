using FluentAssertions;
using GitVault.Core.Models;
using GitVault.Core.Profiles;
using GitVault.Core.Repository;
using Xunit;
using Xunit.Abstractions;

namespace GitVault.Core.Tests;

/// <summary>
/// Editing configuration and per-repository settings, against real repositories.
/// </summary>
/// <remarks>
/// The properties under test are the same three that make activation safe, restated for a
/// different kind of write: planning touches nothing, the preview describes the scope being
/// written rather than the effective value, and applying snapshots first so the change can be
/// undone.
/// </remarks>
public sealed class ConfigEditingTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Planning_an_edit_writes_nothing()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("plan");
        var configFile = Path.Combine(repository, ".git", "config");
        var before = await File.ReadAllBytesAsync(configFile);

        var editor = await BuildEditorAsync(environment);

        var plan = await editor.PlanSetAsync(
            "user.email", "planned@example.invalid", GitConfigScope.Local, repository, CancellationToken.None);

        plan.CanApply.Should().BeTrue();
        plan.ToDiff().Should().Contain("planned@example.invalid");

        (await File.ReadAllBytesAsync(configFile)).Should().Equal(before, "planning must not write");
    }

    [Fact]
    public async Task Applying_an_edit_changes_the_value_git_reports()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("apply");
        var editor = await BuildEditorAsync(environment);

        var plan = await editor.PlanSetAsync(
            "user.email", "applied@example.invalid", GitConfigScope.Local, repository, CancellationToken.None);

        var result = await editor.ApplyAsync(plan, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.SnapshotPath.Should().NotBeNullOrEmpty("a snapshot precedes every write");

        environment.Git(repository, "config", "--local", "user.email").Should().Be("applied@example.invalid");
    }

    [Fact]
    public async Task The_preview_shows_the_value_at_the_scope_being_written()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("scoped");

        // A global value exists; the local scope has none. The preview must not claim the global
        // value is about to be replaced — that is the mistake that once made a deactivation write
        // a global identity into a repository.
        environment.Git(environment.Home, "config", "--global", "user.email", "global@example.invalid");

        var editor = await BuildEditorAsync(environment);

        var plan = await editor.PlanSetAsync(
            "user.email", "local@example.invalid", GitConfigScope.Local, repository, CancellationToken.None);

        plan.Changes.Should().ContainSingle()
            .Which.Before.Should().BeNull("the local scope has no value to replace");

        plan.ToDiff().Should().NotContain("global@example.invalid");
    }

    [Fact]
    public async Task A_blocked_plan_applies_nothing()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var editor = await BuildEditorAsync(environment);

        // Local scope without a repository has nowhere to write.
        var plan = await editor.PlanSetAsync(
            "user.email", "nowhere@example.invalid", GitConfigScope.Local, null, CancellationToken.None);

        plan.Blockers.Should().Contain(BlockerMessages.RepositoryRequired);
        plan.CanApply.Should().BeFalse();

        var result = await editor.ApplyAsync(plan, CancellationToken.None);

        result.SnapshotPath.Should().BeNull("a blocked plan must not even take a snapshot");
        result.Steps.Should().BeEmpty();
    }

    [Fact]
    public async Task Rolling_back_an_edit_restores_the_file_byte_for_byte()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("rollback");
        var configFile = Path.Combine(repository, ".git", "config");
        var before = await File.ReadAllBytesAsync(configFile);

        var snapshots = new SnapshotService(environment.Paths);
        var editor = await BuildEditorAsync(environment, snapshots);

        var plan = await editor.PlanSetAsync(
            "user.email", "temporary@example.invalid", GitConfigScope.Local, repository, CancellationToken.None);

        var result = await editor.ApplyAsync(plan, CancellationToken.None);

        (await File.ReadAllBytesAsync(configFile)).Should().NotEqual(before, "the edit landed");

        await snapshots.RestoreAsync(result.SnapshotPath!, CancellationToken.None);

        (await File.ReadAllBytesAsync(configFile)).Should().Equal(before,
            "rollback restores the configuration exactly as it was");
    }

    [Fact]
    public async Task Project_settings_round_trip_through_the_repository_configuration()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("project");
        var config = await environment.BuildConfigServiceAsync();
        var editor = new ConfigEditor(config, new SnapshotService(environment.Paths));
        var store = new ProjectSettingsStore(config, editor);

        (await store.LoadAsync(repository, CancellationToken.None)).IsEmpty.Should().BeTrue();

        var profileId = Guid.NewGuid();
        var settings = new ProjectSettings(repository)
        {
            ProfileId = profileId,
            ProfileName = "Work",
            SshKeyPath = "/home/qa/.ssh/id_ed25519",
            CredentialHelper = "manager",
            Note = "Corporate remote; do not push from the personal identity.",
            ExcludeFromScans = true,
        };

        var plan = await store.PlanSaveAsync(settings, CancellationToken.None);
        plan.CanApply.Should().BeTrue();

        await editor.ApplyAsync(plan, CancellationToken.None);

        // git itself must be able to read what GitVault wrote.
        environment.Git(repository, "config", "--local", "gitvault.profilename").Should().Be("Work");

        var reloaded = await store.LoadAsync(repository, CancellationToken.None);

        reloaded.ProfileId.Should().Be(profileId);
        reloaded.ProfileName.Should().Be("Work");
        reloaded.SshKeyPath.Should().Be("/home/qa/.ssh/id_ed25519");
        reloaded.CredentialHelper.Should().Be("manager");
        reloaded.Note.Should().Be("Corporate remote; do not push from the personal identity.");
        reloaded.ExcludeFromScans.Should().BeTrue();
    }

    [Fact]
    public async Task Clearing_a_field_removes_the_key_rather_than_blanking_it()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("clearfield");
        var config = await environment.BuildConfigServiceAsync();
        var editor = new ConfigEditor(config, new SnapshotService(environment.Paths));
        var store = new ProjectSettingsStore(config, editor);

        var saved = new ProjectSettings(repository) { ProfileName = "Work", Note = "temporary" };
        await editor.ApplyAsync(await store.PlanSaveAsync(saved, CancellationToken.None), CancellationToken.None);

        var withoutNote = saved with { Note = null };
        await editor.ApplyAsync(
            await store.PlanSaveAsync(withoutNote, CancellationToken.None), CancellationToken.None);

        var text = await File.ReadAllTextAsync(Path.Combine(repository, ".git", "config"));

        text.Should().Contain("profilename");
        text.Should().NotContain("note", "a cleared field leaves no empty key behind");
    }

    [Fact]
    public async Task Clearing_the_section_removes_every_key_GitVault_wrote()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("clearall");
        var config = await environment.BuildConfigServiceAsync();
        var editor = new ConfigEditor(config, new SnapshotService(environment.Paths));
        var store = new ProjectSettingsStore(config, editor);

        var settings = new ProjectSettings(repository) { ProfileName = "Work", CredentialHelper = "manager" };
        await editor.ApplyAsync(await store.PlanSaveAsync(settings, CancellationToken.None), CancellationToken.None);

        await editor.ApplyAsync(
            await store.PlanClearAsync(repository, CancellationToken.None), CancellationToken.None);

        (await store.LoadAsync(repository, CancellationToken.None)).IsEmpty.Should().BeTrue();

        var text = await File.ReadAllTextAsync(Path.Combine(repository, ".git", "config"));
        text.Should().NotContain("gitvault");
    }

    [Fact]
    public async Task A_gitvault_section_inherited_from_the_global_config_is_not_treated_as_the_repositorys()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("inherit");
        environment.Git(environment.Home, "config", "--global", "gitvault.profilename", "Global Profile");

        var config = await environment.BuildConfigServiceAsync();
        var store = new ProjectSettingsStore(config, new ConfigEditor(config, new SnapshotService(environment.Paths)));

        var loaded = await store.LoadAsync(repository, CancellationToken.None);

        loaded.IsEmpty.Should().BeTrue(
            "settings inherited from a wider scope belong to every repository, so they belong to none");
    }

    private static async Task<ConfigEditor> BuildEditorAsync(
        TempGitEnvironment environment,
        SnapshotService? snapshots = null)
    {
        var config = await environment.BuildConfigServiceAsync();
        return new ConfigEditor(config, snapshots ?? new SnapshotService(environment.Paths));
    }
}
