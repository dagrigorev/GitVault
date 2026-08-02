using FluentAssertions;
using GitVault.Core.Abstractions;
using GitVault.Core.Git;
using GitVault.Core.Models;
using GitVault.Core.Platform;
using GitVault.Core.Profiles;
using Xunit;
using Xunit.Abstractions;

namespace GitVault.Core.Tests;

file sealed class RoundTripPaths(string home) : PlatformPathsBase(home)
{
    public override string AppDataDirectory => Path.Combine(HomeDirectory, ".gitvault");

    public override IReadOnlyList<string> SystemGitConfigCandidates => [];

    public override IReadOnlyList<string> AdditionalKeyDirectories => [];
}

file sealed class NameOnlyGitHints : IGitInstallHints
{
    public string GitExecutableName => OperatingSystem.IsWindows() ? "git.exe" : "git";

    public IReadOnlyList<string> CandidateGitPaths => [];
}

/// <summary>
/// The acceptance test for the headline feature: activate a profile, confirm the configuration
/// says what it should, deactivate, and confirm the touched files are back byte-for-byte.
/// </summary>
/// <remarks>
/// This runs against the real <c>git</c> binary in a throwaway repository, because the whole
/// point is that GitVault's writes and git's own reading agree. It is skipped when git is absent.
/// </remarks>
public sealed class ActivationRoundTripTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _home =
        Path.Combine(Path.GetTempPath(), "gitvault-activate", Guid.NewGuid().ToString("N"));

    private string RepositoryPath => Path.Combine(_home, "repo");

    private static IdentityProfile Profile(string name = "work") =>
        new(
            Guid.NewGuid(),
            name,
            GitIdentity.Create("Ada Work", "ada@work.example", IdentitySource.GitGlobalConfig, "/x"),
            SshKeyId: null,
            PreferredAgent: null,
            CredentialHelper: "manager",
            ActivationScope.Repository,
            RepositoryPath: null)
        {
            SshKeyPath = "/home/ada/.ssh/id_ed25519_work",
            HostAliases = [new SshHostAlias("github.com-work", "github.com", "git", "~/.ssh/id_ed25519_work")],
        };

    private async Task<(ProfileActivator Activator, GitConfigService Config)?> SetUpAsync()
    {
        Directory.CreateDirectory(RepositoryPath);
        Directory.CreateDirectory(Path.Combine(_home, ".ssh"));

        var runner = new ProcessRunner();
        var locator = new GitBinaryLocator(runner, new NameOnlyGitHints());

        if (await locator.LocateAsync(CancellationToken.None) is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return null;
        }

        var init = await runner.RunAsync(
            OperatingSystem.IsWindows() ? "git.exe" : "git",
            ["init", "--quiet"],
            RepositoryPath,
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        init.IsSuccess.Should().BeTrue();

        var paths = new RoundTripPaths(_home);
        var config = new GitConfigService(runner, locator, paths);

        var activator = new ProfileActivator(
            config,
            new SnapshotService(paths),
            new ActivationStateStore(paths),
            paths);

        return (activator, config);
    }

    [Fact]
    public async Task Planning_writes_absolutely_nothing()
    {
        var setup = await SetUpAsync();
        if (setup is null)
        {
            return;
        }

        var (activator, _) = setup.Value;
        var configPath = Path.Combine(RepositoryPath, ".git", "config");
        var before = await File.ReadAllBytesAsync(configPath);

        var plan = await activator.PlanActivationAsync(
            Profile(), ActivationScope.Repository, RepositoryPath, CancellationToken.None);

        plan.Changes.Should().NotBeEmpty();
        (await File.ReadAllBytesAsync(configPath)).Should().Equal(before, "a dry run must not touch the file");
        File.Exists(activator.SshConfigPath).Should().BeFalse("a dry run must not create ~/.ssh/config");
    }

    [Fact]
    public async Task Activate_then_deactivate_restores_the_files_byte_for_byte()
    {
        var setup = await SetUpAsync();
        if (setup is null)
        {
            return;
        }

        var (activator, config) = setup.Value;
        var profile = Profile();

        // A pre-existing ssh config with the user's own content, which must survive untouched.
        const string UserSshConfig = "Host personal\n    HostName github.com\n    User git\n";
        await File.WriteAllTextAsync(activator.SshConfigPath, UserSshConfig);

        var configPath = Path.Combine(RepositoryPath, ".git", "config");
        var gitConfigBefore = await File.ReadAllBytesAsync(configPath);
        var sshConfigBefore = await File.ReadAllBytesAsync(activator.SshConfigPath);

        var activationPlan = await activator.PlanActivationAsync(
            profile, ActivationScope.Repository, RepositoryPath, CancellationToken.None);

        activationPlan.CanApply.Should().BeTrue();

        var activation = await activator.ApplyAsync(activationPlan, CancellationToken.None);
        activation.Succeeded.Should().BeTrue(
            string.Join("; ", activation.Steps.Where(s => s.Outcome == StepOutcome.Failed).Select(s => s.Detail)));

        // git itself must agree about what was written.
        var effective = await new EffectiveIdentityResolver(config)
            .ResolveAsync(RepositoryPath, CancellationToken.None);

        effective.UserName.Value.Should().Be("Ada Work");
        effective.Email.Value.Should().Be("ada@work.example");
        effective.Email.Scope.Should().Be(GitConfigScope.Local);
        effective.CredentialHelper.Value.Should().Be("manager");
        effective.SshCommand.Value.Should().Contain("IdentitiesOnly=yes");

        var sshAfterActivation = await File.ReadAllTextAsync(activator.SshConfigPath);
        sshAfterActivation.Should().StartWith(UserSshConfig, "the user's own block comes first and is untouched");
        sshAfterActivation.Should().Contain("Host github.com-work");

        var deactivationPlan = await activator.PlanDeactivationAsync(
            profile, ActivationScope.Repository, RepositoryPath, CancellationToken.None);

        var deactivation = await activator.ApplyAsync(deactivationPlan, CancellationToken.None);
        deactivation.Succeeded.Should().BeTrue();

        (await File.ReadAllBytesAsync(configPath)).Should().Equal(
            gitConfigBefore, "deactivation must restore the repository config byte for byte");

        (await File.ReadAllBytesAsync(activator.SshConfigPath)).Should().Equal(
            sshConfigBefore, "deactivation must restore ~/.ssh/config byte for byte");
    }

    [Fact]
    public async Task Deactivation_restores_a_value_the_user_had_set_rather_than_removing_it()
    {
        var setup = await SetUpAsync();
        if (setup is null)
        {
            return;
        }

        var (activator, config) = setup.Value;
        var profile = Profile();

        await config.SetAsync(
            "user.email", "original@example.com", GitConfigScope.Local, RepositoryPath, CancellationToken.None);

        var plan = await activator.PlanActivationAsync(
            profile, ActivationScope.Repository, RepositoryPath, CancellationToken.None);

        await activator.ApplyAsync(plan, CancellationToken.None);

        (await config.GetEffectiveAsync("user.email", RepositoryPath, CancellationToken.None))!
            .Value.Should().Be("ada@work.example");

        var deactivation = await activator.PlanDeactivationAsync(
            profile, ActivationScope.Repository, RepositoryPath, CancellationToken.None);

        await activator.ApplyAsync(deactivation, CancellationToken.None);

        (await config.GetEffectiveAsync("user.email", RepositoryPath, CancellationToken.None))!
            .Value.Should().Be("original@example.com", "the user's own value must come back, not vanish");
    }

    [Fact]
    public async Task Deactivating_something_that_was_never_activated_is_refused()
    {
        var setup = await SetUpAsync();
        if (setup is null)
        {
            return;
        }

        var (activator, _) = setup.Value;

        var plan = await activator.PlanDeactivationAsync(
            Profile(), ActivationScope.Repository, RepositoryPath, CancellationToken.None);

        plan.CanApply.Should().BeFalse();
        plan.Blockers.Should().ContainSingle().Which.Should().Contain("no record");
    }

    [Fact]
    public async Task Rolling_back_the_snapshot_undoes_an_activation()
    {
        var setup = await SetUpAsync();
        if (setup is null)
        {
            return;
        }

        var (activator, _) = setup.Value;
        var configPath = Path.Combine(RepositoryPath, ".git", "config");
        var before = await File.ReadAllBytesAsync(configPath);

        var plan = await activator.PlanActivationAsync(
            Profile(), ActivationScope.Repository, RepositoryPath, CancellationToken.None);

        var result = await activator.ApplyAsync(plan, CancellationToken.None);
        result.SnapshotPath.Should().NotBeNull();

        (await File.ReadAllBytesAsync(configPath)).Should().NotEqual(before);

        await activator.RollbackAsync(result.SnapshotPath!, CancellationToken.None);

        (await File.ReadAllBytesAsync(configPath)).Should().Equal(
            before, "rollback restores the snapshot taken before the first write");
    }

    [Fact]
    public async Task Repository_scope_without_a_repository_is_blocked()
    {
        var setup = await SetUpAsync();
        if (setup is null)
        {
            return;
        }

        var (activator, _) = setup.Value;

        var plan = await activator.PlanActivationAsync(
            Profile(), ActivationScope.Repository, null, CancellationToken.None);

        plan.CanApply.Should().BeFalse();
        plan.Blockers.Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_blocked_plan_applies_nothing()
    {
        var setup = await SetUpAsync();
        if (setup is null)
        {
            return;
        }

        var (activator, _) = setup.Value;
        var configPath = Path.Combine(RepositoryPath, ".git", "config");
        var before = await File.ReadAllBytesAsync(configPath);

        var plan = await activator.PlanActivationAsync(
            Profile(), ActivationScope.Repository, null, CancellationToken.None);

        var result = await activator.ApplyAsync(plan, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        (await File.ReadAllBytesAsync(configPath)).Should().Equal(before);
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_home, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
            // Leftover temp files are not worth failing the run over.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }
}
