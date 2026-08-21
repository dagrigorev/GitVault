using FluentAssertions;
using GitVault.Core.Discovery;
using GitVault.Core.Git;
using GitVault.Core.Models;
using GitVault.Core.Profiles;
using Xunit;
using Xunit.Abstractions;

namespace GitVault.Core.Tests;

/// <summary>
/// Discovery, end to end, against real repositories built by the real git binary.
/// </summary>
/// <remarks>
/// The unit tests prove the parsers agree with their fixtures. These prove the whole path agrees
/// with git: a value written by git is discovered, attributed to the right scope, and resolved to
/// the same winner git itself reports. That is the claim the application makes on its Overview
/// page, and nothing below this level can check it.
/// </remarks>
public sealed class EndToEndDiscoveryTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Identities_are_discovered_from_every_scope_git_reports()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("alpha");

        environment.Git(environment.Home, "config", "--global", "user.name", "Global Person");
        environment.Git(environment.Home, "config", "--global", "user.email", "global@example.invalid");
        environment.Git(repository, "config", "--local", "user.name", "Local Person");
        environment.Git(repository, "config", "--local", "user.email", "local@example.invalid");

        var config = await environment.BuildConfigServiceAsync();
        var values = await config.ListAsync(repository, CancellationToken.None);

        var identities = GitIdentityProbe.BuildIdentities(values);

        identities.Select(i => i.Email).Should().Contain("global@example.invalid");
        identities.Select(i => i.Email).Should().Contain("local@example.invalid");

        identities.Should().Contain(i => i.Source == IdentitySource.GitGlobalConfig);
        identities.Should().Contain(i => i.Source == IdentitySource.RepoLocal);
    }

    [Fact]
    public async Task The_effective_identity_matches_what_git_itself_resolves()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("beta");

        environment.Git(environment.Home, "config", "--global", "user.email", "global@example.invalid");
        environment.Git(repository, "config", "--local", "user.email", "local@example.invalid");

        var config = await environment.BuildConfigServiceAsync();
        var resolver = new EffectiveIdentityResolver(config);

        var effective = await resolver.ResolveAsync(repository, CancellationToken.None);

        // The authority is git, not our expectation of git.
        var gitSays = environment.Git(repository, "config", "user.email");

        effective.Email.Value.Should().Be(gitSays);
        effective.Email.Value.Should().Be("local@example.invalid");
        effective.Email.Scope.Should().Be(GitConfigScope.Local, "the local value wins");
        effective.IsComplete.Should().BeTrue();
    }

    [Fact]
    public async Task Outside_a_repository_the_global_identity_wins()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        environment.Git(environment.Home, "config", "--global", "user.email", "global@example.invalid");

        var config = await environment.BuildConfigServiceAsync();
        var resolver = new EffectiveIdentityResolver(config);

        var effective = await resolver.ResolveAsync(null, CancellationToken.None);

        effective.Email.Value.Should().Be("global@example.invalid");
        effective.Email.Scope.Should().Be(GitConfigScope.Global);
    }

    [Fact]
    public async Task An_included_file_is_discovered_and_attributed_to_itself()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("work");

        var extra = Path.Combine(environment.Home, "work.gitconfig");
        await File.WriteAllTextAsync(extra, "[user]\n\temail = included@example.invalid\n");

        // A conditional include keyed on the repository's directory, which is the arrangement
        // people actually use to separate work from personal identities.
        var gitdir = repository.Replace('\\', '/').TrimEnd('/') + "/";
        environment.Git(
            environment.Home,
            "config", "--global", $"includeIf.gitdir:{gitdir}.path", extra.Replace('\\', '/'));

        var config = await environment.BuildConfigServiceAsync();
        var resolver = new EffectiveIdentityResolver(config);

        var effective = await resolver.ResolveAsync(repository, CancellationToken.None);
        var gitSays = environment.Git(repository, "config", "user.email");

        effective.Email.Value.Should().Be(gitSays);
        effective.Email.Value.Should().Be("included@example.invalid",
            "an includeIf match must be honoured exactly as git honours it");
    }

    [Fact]
    public async Task A_value_GitVault_writes_is_the_value_git_reads_back()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = environment.CreateRepository("roundtrip");
        var config = await environment.BuildConfigServiceAsync();

        // Values chosen to exercise the grammar: spaces, a quote, a backslash and a comment
        // character, all of which the writer has to escape and git has to read back unchanged.
        const string awkward = @"Name ""With"" \ Quotes # and a hash";

        await config.SetAsync("user.name", awkward, GitConfigScope.Local, repository, CancellationToken.None);

        environment.Git(repository, "config", "user.name").Should().Be(awkward);

        await config.UnsetAsync("user.name", GitConfigScope.Local, repository, CancellationToken.None);

        var remaining = await config.ListAsync(repository, CancellationToken.None);
        remaining.Should().NotContain(v =>
            v.Key == "user.name" && v.Scope == GitConfigScope.Local);
    }

    [Fact]
    public async Task Repository_scanning_finds_working_trees_and_respects_depth()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var root = Path.Combine(environment.Home, "src");
        Directory.CreateDirectory(root);

        environment.CreateRepository(Path.Combine("src", "shallow"));

        var deepDirectory = Path.Combine("src", "one", "two", "deep");
        Directory.CreateDirectory(Path.Combine(environment.Home, deepDirectory));
        environment.CreateRepository(deepDirectory);

        var scanner = new RepositoryScanner();

        var shallowOnly = await scanner.ScanAsync([root], 1, CancellationToken.None);
        shallowOnly.Select(r => r.Name).Should().Equal("shallow");

        var everything = await scanner.ScanAsync([root], 8, CancellationToken.None);
        everything.Select(r => r.Name).Should().BeEquivalentTo(["shallow", "deep"]);
    }

    [Fact]
    public async Task A_scan_root_that_does_not_exist_is_skipped_rather_than_thrown()
    {
        var scanner = new RepositoryScanner();

        var found = await scanner.ScanAsync(
            [Path.Combine(Path.GetTempPath(), "gitvault-absent-" + Guid.NewGuid().ToString("N"))],
            4,
            CancellationToken.None);

        found.Should().BeEmpty();
    }

    [Fact]
    public async Task The_remote_url_is_reported_for_a_discovered_repository()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var root = Path.Combine(environment.Home, "remotes");
        Directory.CreateDirectory(root);

        var repository = environment.CreateRepository(Path.Combine("remotes", "withremote"));
        environment.Git(repository, "remote", "add", "origin", "https://git.example.invalid/thing.git");

        var found = await new RepositoryScanner().ScanAsync([root], 2, CancellationToken.None);

        found.Should().ContainSingle()
            .Which.RemoteUrl.Should().Be("https://git.example.invalid/thing.git");
    }
}
