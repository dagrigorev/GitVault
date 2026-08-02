using System.Text;
using FluentAssertions;
using GitVault.Core.Abstractions;
using GitVault.Core.Models;
using GitVault.Core.Platform;
using GitVault.Core.Profiles;
using Xunit;

namespace GitVault.Core.Tests;

file sealed class ProfilePaths(string home) : PlatformPathsBase(home)
{
    public override string AppDataDirectory => Path.Combine(HomeDirectory, ".gitvault");

    public override IReadOnlyList<string> SystemGitConfigCandidates => [];

    public override IReadOnlyList<string> AdditionalKeyDirectories => [];
}

public sealed class ManagedBlockEditorTests
{
    private const string Existing = """
        # my own ssh config
        Host personal
            HostName github.com
            IdentityFile ~/.ssh/id_ed25519

        """;

    [Fact]
    public void Adds_a_block_to_an_empty_file()
    {
        var result = ManagedBlockEditor.Upsert(null, "work", "Host github.com-work\n    User git");

        result.Should().Be(
            "# >>> GitVault managed: work >>>\nHost github.com-work\n    User git\n"
            + "# <<< GitVault managed: work <<<\n");
    }

    [Fact]
    public void Appends_without_touching_what_was_already_there()
    {
        var result = ManagedBlockEditor.Upsert(Existing, "work", "Host work");

        result.Should().StartWith(Existing);
        result.Should().Contain("# >>> GitVault managed: work >>>");
    }

    [Fact]
    public void Adding_then_removing_a_block_is_the_identity()
    {
        var added = ManagedBlockEditor.Upsert(Existing, "work", "Host github.com-work\n    User git");

        ManagedBlockEditor.Remove(added, "work").Should().Be(Existing,
            "removing our own block must leave the user's file exactly as it was");
    }

    [Fact]
    public void Round_trip_holds_for_an_empty_starting_file()
    {
        var added = ManagedBlockEditor.Upsert(string.Empty, "work", "Host work");

        ManagedBlockEditor.Remove(added, "work").Should().BeEmpty();
    }

    [Fact]
    public void A_file_without_a_final_newline_gains_one_and_nothing_else()
    {
        // The single documented side effect of adding a block. Adding a blank separator line
        // instead would be indistinguishable from one the user wrote, and removal could then
        // not tell whether to take it back.
        const string Original = "Host personal\n    User git";

        var added = ManagedBlockEditor.Upsert(Original, "work", "Host work");

        ManagedBlockEditor.Remove(added, "work").Should().Be(Original + "\n");
    }

    [Fact]
    public void Replacing_a_block_leaves_neighbouring_content_alone()
    {
        var first = ManagedBlockEditor.Upsert(Existing, "work", "Host old");
        var withTrailer = first + "\nHost added-later\n    User git\n";

        var replaced = ManagedBlockEditor.Upsert(withTrailer, "work", "Host new");

        replaced.Should().StartWith(Existing);
        replaced.Should().Contain("Host added-later");
        replaced.Should().Contain("Host new");
        replaced.Should().NotContain("Host old");
    }

    [Fact]
    public void Two_profiles_can_own_separate_blocks()
    {
        var withWork = ManagedBlockEditor.Upsert(Existing, "work", "Host work");
        var withBoth = ManagedBlockEditor.Upsert(withWork, "oss", "Host oss");

        ManagedBlockEditor.ContainsBlock(withBoth, "work").Should().BeTrue();
        ManagedBlockEditor.ContainsBlock(withBoth, "oss").Should().BeTrue();

        var onlyOss = ManagedBlockEditor.Remove(withBoth, "work");
        ManagedBlockEditor.ContainsBlock(onlyOss, "work").Should().BeFalse();
        ManagedBlockEditor.ContainsBlock(onlyOss, "oss").Should().BeTrue();
    }

    [Fact]
    public void Crlf_line_endings_survive()
    {
        const string Original = "Host personal\r\n    User git\r\n";

        var added = ManagedBlockEditor.Upsert(Original, "work", "Host work");

        added.Should().Contain("\r\n");
        added.Should().NotContain("\n\n\r");
        ManagedBlockEditor.Remove(added, "work").Should().Be(Original);
    }

    [Fact]
    public void A_block_with_no_closing_marker_is_left_alone()
    {
        // Someone edited the file by hand and broke the pair. Guessing where the block ends
        // could delete their content, so we refuse to touch it.
        const string Broken = "# >>> GitVault managed: work >>>\nHost work\n";

        ManagedBlockEditor.ContainsBlock(Broken, "work").Should().BeFalse();
        ManagedBlockEditor.Remove(Broken, "work").Should().Be(Broken);
    }

    [Fact]
    public void Removing_a_block_that_is_not_there_changes_nothing() =>
        ManagedBlockEditor.Remove(Existing, "work").Should().Be(Existing);

    [Fact]
    public void The_body_of_a_block_can_be_read_back()
    {
        var added = ManagedBlockEditor.Upsert(Existing, "work", "Host work\n    User git");

        ManagedBlockEditor.ReadBlockBody(added, "work").Should().Be("Host work\n    User git");
        ManagedBlockEditor.ReadBlockBody(added, "missing").Should().BeNull();
    }

    [Fact]
    public void A_host_alias_renders_as_ssh_config()
    {
        var alias = new SshHostAlias("github.com-work", "github.com", "git", "~/.ssh/id_ed25519_work");

        ManagedBlockEditor.RenderHostAlias(alias).Should().Be(
            "Host github.com-work\n    HostName github.com\n    User git\n"
            + "    IdentityFile ~/.ssh/id_ed25519_work\n    IdentitiesOnly yes");
    }
}

public sealed class SnapshotServiceTests : IDisposable
{
    private readonly string _home =
        Path.Combine(Path.GetTempPath(), "gitvault-snap", Guid.NewGuid().ToString("N"));

    private readonly SnapshotService _snapshots;

    public SnapshotServiceTests()
    {
        Directory.CreateDirectory(_home);
        _snapshots = new SnapshotService(new ProfilePaths(_home));
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_home, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task Captures_and_restores_a_file()
    {
        var path = WriteFile("config", "original");

        var snapshot = await _snapshots.CaptureAsync([path], CancellationToken.None);
        await File.WriteAllTextAsync(path, "changed");

        await _snapshots.RestoreAsync(snapshot.Path, CancellationToken.None);

        (await File.ReadAllTextAsync(path)).Should().Be("original");
    }

    [Fact]
    public async Task Restoring_deletes_a_file_that_did_not_exist_before()
    {
        var path = Path.Combine(_home, "created-later");

        var snapshot = await _snapshots.CaptureAsync([path], CancellationToken.None);
        await File.WriteAllTextAsync(path, "we made this");

        await _snapshots.RestoreAsync(snapshot.Path, CancellationToken.None);

        File.Exists(path).Should().BeFalse("restoring must undo a file GitVault created");
    }

    [Fact]
    public async Task A_snapshot_records_which_files_it_holds()
    {
        var path = WriteFile("config", "original");

        var snapshot = await _snapshots.CaptureAsync([path], CancellationToken.None);

        snapshot.Files.Should().ContainKey(path);
        File.Exists(snapshot.Files[path]).Should().BeTrue();
    }

    [Fact]
    public async Task Old_snapshots_are_pruned()
    {
        var path = WriteFile("config", "original");

        for (var i = 0; i < SnapshotService.RetainedSnapshots + 5; i++)
        {
            await _snapshots.CaptureAsync([path], CancellationToken.None);
        }

        _snapshots.ListSnapshots().Should().HaveCount(SnapshotService.RetainedSnapshots);
    }

    [Fact]
    public async Task Restoring_a_snapshot_that_is_not_there_is_an_error()
    {
        var act = () => _snapshots.RestoreAsync(Path.Combine(_home, "nope"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
            // Leftover temp files are not worth failing the run over.
        }
    }
}

public sealed class ActivationStateStoreTests : IDisposable
{
    private readonly string _home =
        Path.Combine(Path.GetTempPath(), "gitvault-state", Guid.NewGuid().ToString("N"));

    private readonly ActivationStateStore _store;
    private readonly Guid _profileId = Guid.NewGuid();

    public ActivationStateStoreTests()
    {
        Directory.CreateDirectory(_home);
        _store = new ActivationStateStore(new ProfilePaths(_home));
    }

    private ActivationRecord Record(ActivationScope scope = ActivationScope.Global, string? repository = null) =>
        new(_profileId, "work", scope, repository, DateTimeOffset.UtcNow, "/snapshots/x")
        {
            Settings = [new WrittenSetting("user.email", GitConfigScope.Global, null, "old@example.com")],
            WroteSshConfigBlock = true,
        };

    [Fact]
    public async Task Records_round_trip()
    {
        await _store.RecordAsync(Record(), CancellationToken.None);

        var found = await _store.FindAsync(_profileId, ActivationScope.Global, null, CancellationToken.None);

        found.Should().NotBeNull();
        found!.ProfileName.Should().Be("work");
        found.Settings.Should().ContainSingle();
        found.Settings[0].PreviousValue.Should().Be("old@example.com");
        found.WroteSshConfigBlock.Should().BeTrue();
    }

    [Fact]
    public async Task Re_activating_replaces_the_previous_record()
    {
        await _store.RecordAsync(Record(), CancellationToken.None);
        await _store.RecordAsync(Record(), CancellationToken.None);

        var state = await _store.LoadAsync(CancellationToken.None);

        state.Activations.Should().ContainSingle(
            "a stale record would make deactivation restore the wrong previous value");
    }

    [Fact]
    public async Task The_same_profile_can_be_active_at_two_scopes()
    {
        await _store.RecordAsync(Record(), CancellationToken.None);
        await _store.RecordAsync(Record(ActivationScope.Repository, "/repo"), CancellationToken.None);

        (await _store.LoadAsync(CancellationToken.None)).Activations.Should().HaveCount(2);
    }

    [Fact]
    public async Task Forgetting_removes_only_the_matching_record()
    {
        await _store.RecordAsync(Record(), CancellationToken.None);
        await _store.RecordAsync(Record(ActivationScope.Repository, "/repo"), CancellationToken.None);

        var forgotten = await _store.ForgetAsync(_profileId, ActivationScope.Global, null, CancellationToken.None);

        forgotten.Should().NotBeNull();
        (await _store.LoadAsync(CancellationToken.None)).Activations.Should().ContainSingle();
    }

    [Fact]
    public async Task Forgetting_something_unknown_reports_nothing()
    {
        var forgotten = await _store.ForgetAsync(
            Guid.NewGuid(), ActivationScope.Global, null, CancellationToken.None);

        forgotten.Should().BeNull();
    }

    [Fact]
    public async Task A_corrupt_state_file_reads_as_empty()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_store.StateFilePath)!);
        await File.WriteAllTextAsync(_store.StateFilePath, "{ not json");

        (await _store.LoadAsync(CancellationToken.None)).Activations.Should().BeEmpty();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
            // Leftover temp files are not worth failing the run over.
        }
    }
}

public sealed class ProfileStoreTests : IDisposable
{
    private readonly string _home =
        Path.Combine(Path.GetTempPath(), "gitvault-profiles", Guid.NewGuid().ToString("N"));

    private readonly ProfileStore _store;

    public ProfileStoreTests()
    {
        Directory.CreateDirectory(_home);
        _store = new ProfileStore(new ProfilePaths(_home));
    }

    private static IdentityProfile Profile(string name = "work") =>
        new(
            Guid.NewGuid(),
            name,
            GitIdentity.Create("Ada Lovelace", "ada@example.com", IdentitySource.GitGlobalConfig, "/x"),
            SshKeyId: null,
            PreferredAgent: AgentKind.OpenSshUnix,
            CredentialHelper: "manager",
            ActivationScope.Global,
            RepositoryPath: null)
        {
            SshKeyPath = "/home/ada/.ssh/id_ed25519_work",
            HostAliases = [new SshHostAlias("github.com-work", "github.com", "git", "~/.ssh/id_ed25519_work")],
        };

    [Fact]
    public async Task Profiles_round_trip()
    {
        var profile = Profile();

        await _store.SaveAsync(profile, CancellationToken.None);
        var loaded = await _store.LoadAsync(CancellationToken.None);

        loaded.Should().ContainSingle();
        loaded[0].Name.Should().Be("work");
        loaded[0].Identity.Email.Should().Be("ada@example.com");
        loaded[0].HostAliases.Should().ContainSingle();
        loaded[0].HostAliases[0].Alias.Should().Be("github.com-work");
    }

    [Fact]
    public async Task Saving_the_same_profile_twice_replaces_it()
    {
        var profile = Profile();

        await _store.SaveAsync(profile, CancellationToken.None);
        await _store.SaveAsync(profile with { Name = "renamed" }, CancellationToken.None);

        var loaded = await _store.LoadAsync(CancellationToken.None);

        loaded.Should().ContainSingle();
        loaded[0].Name.Should().Be("renamed");
    }

    [Fact]
    public async Task Deleting_removes_a_profile()
    {
        var profile = Profile();
        await _store.SaveAsync(profile, CancellationToken.None);

        (await _store.DeleteAsync(profile.Id, CancellationToken.None)).Should().BeTrue();
        (await _store.LoadAsync(CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task An_export_carries_a_header_and_no_secret()
    {
        var destination = Path.Combine(_home, "export.json");

        await _store.ExportAsync([Profile()], destination, CancellationToken.None);

        var text = await File.ReadAllTextAsync(destination);

        text.Should().Contain("no private keys");
        text.Should().Contain("id_ed25519_work", "a key path is a reference, which is the point");
        text.Should().NotContain("BEGIN OPENSSH PRIVATE KEY");
        text.Should().NotContain("password");
    }

    [Fact]
    public async Task Importing_gives_every_profile_a_fresh_identifier()
    {
        var profile = Profile();
        var destination = Path.Combine(_home, "export.json");
        await _store.ExportAsync([profile], destination, CancellationToken.None);

        var imported = await _store.ImportAsync(destination, CancellationToken.None);

        imported.Should().ContainSingle();
        imported[0].Id.Should().NotBe(profile.Id, "an import must not silently replace an existing profile");
        imported[0].Name.Should().Be("work");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
            // Leftover temp files are not worth failing the run over.
        }
    }
}

public sealed class RepositoryScannerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "gitvault-repos", Guid.NewGuid().ToString("N"));

    public RepositoryScannerTests() => Directory.CreateDirectory(_root);

    private string MakeRepository(string relativePath, string? remoteUrl = null)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.Combine(path, ".git"));

        if (remoteUrl is not null)
        {
            File.WriteAllText(
                Path.Combine(path, ".git", "config"),
                $"[core]\n\tbare = false\n[remote \"origin\"]\n\turl = {remoteUrl}\n");
        }

        return path;
    }

    [Fact]
    public async Task Finds_repositories_under_a_root()
    {
        MakeRepository("alpha");
        MakeRepository(Path.Combine("nested", "beta"));

        var found = await new RepositoryScanner().ScanAsync([_root], 5, CancellationToken.None);

        found.Should().HaveCount(2);
        found.Select(r => r.Name).Should().BeEquivalentTo(["alpha", "beta"]);
    }

    [Fact]
    public async Task Reads_the_first_remote_url()
    {
        MakeRepository("alpha", "https://github.com/ada/alpha.git");

        var found = await new RepositoryScanner().ScanAsync([_root], 5, CancellationToken.None);

        found.Single().RemoteUrl.Should().Be("https://github.com/ada/alpha.git");
    }

    [Fact]
    public async Task Does_not_descend_into_a_repository_it_already_found()
    {
        var outer = MakeRepository("outer");
        Directory.CreateDirectory(Path.Combine(outer, "inner", ".git"));

        var found = await new RepositoryScanner().ScanAsync([_root], 5, CancellationToken.None);

        found.Should().ContainSingle("a working tree is not searched for more repositories");
    }

    [Fact]
    public async Task Noisy_directories_are_skipped()
    {
        MakeRepository(Path.Combine("node_modules", "package"));
        MakeRepository("real");

        var found = await new RepositoryScanner().ScanAsync([_root], 5, CancellationToken.None);

        found.Should().ContainSingle();
        found[0].Name.Should().Be("real");
    }

    [Fact]
    public async Task The_depth_limit_is_respected()
    {
        MakeRepository(Path.Combine("a", "b", "c", "deep"));

        var shallow = await new RepositoryScanner().ScanAsync([_root], 2, CancellationToken.None);
        var deep = await new RepositoryScanner().ScanAsync([_root], 6, CancellationToken.None);

        shallow.Should().BeEmpty();
        deep.Should().ContainSingle();
    }

    [Fact]
    public async Task A_root_that_does_not_exist_is_ignored()
    {
        var found = await new RepositoryScanner()
            .ScanAsync([Path.Combine(_root, "nope")], 5, CancellationToken.None);

        found.Should().BeEmpty();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Leftover temp files are not worth failing the run over.
        }
    }
}

public sealed class ActivationPlanTests
{
    [Fact]
    public void A_plan_with_only_no_ops_cannot_be_applied()
    {
        var plan = new ActivationPlan(Guid.NewGuid(), "work", ActivationScope.Global, null, false)
        {
            Changes = [new PlannedChange("Identity", ChangeKind.GitConfigSet, "user.name", "Ada", "Ada")],
        };

        plan.CanApply.Should().BeFalse();
    }

    [Fact]
    public void A_blocked_plan_cannot_be_applied()
    {
        var plan = new ActivationPlan(Guid.NewGuid(), "work", ActivationScope.Repository, null, false)
        {
            Changes = [new PlannedChange("Identity", ChangeKind.GitConfigSet, "user.name", null, "Ada")],
            Blockers = ["Repository scope needs a repository path."],
        };

        plan.CanApply.Should().BeFalse();
    }

    [Fact]
    public void The_diff_shows_both_sides_of_a_change()
    {
        var plan = new ActivationPlan(Guid.NewGuid(), "work", ActivationScope.Global, null, false)
        {
            Changes =
            [
                new PlannedChange("Identity", ChangeKind.GitConfigSet, "user.email", "old@example.com", "new@example.com"),
            ],
        };

        var diff = plan.ToDiff();

        diff.Should().Contain("- old@example.com");
        diff.Should().Contain("+ new@example.com");
    }

    [Fact]
    public void The_diff_marks_a_change_that_would_do_nothing()
    {
        var plan = new ActivationPlan(Guid.NewGuid(), "work", ActivationScope.Global, null, false)
        {
            Changes = [new PlannedChange("Identity", ChangeKind.GitConfigSet, "user.name", "Ada", "Ada")],
        };

        plan.ToDiff().Should().Contain("(no change)");
    }

    [Theory]
    [InlineData(ActivationScope.Global, GitConfigScope.Global)]
    [InlineData(ActivationScope.System, GitConfigScope.System)]
    [InlineData(ActivationScope.Repository, GitConfigScope.Local)]
    public void Activation_scopes_map_to_config_scopes(ActivationScope scope, GitConfigScope expected) =>
        ProfileActivator.ToConfigScope(scope).Should().Be(expected);

    [Fact]
    public void The_ssh_command_pins_the_key_and_nothing_else() =>
        ProfileActivator.BuildSshCommand("/home/ada/.ssh/id_ed25519")
            .Should().Be("ssh -i /home/ada/.ssh/id_ed25519 -o IdentitiesOnly=yes");

    [Fact]
    public void A_key_path_containing_a_space_is_quoted() =>
        ProfileActivator.BuildSshCommand("/home/ada/my keys/id_ed25519")
            .Should().Be("ssh -i \"/home/ada/my keys/id_ed25519\" -o IdentitiesOnly=yes");
}
