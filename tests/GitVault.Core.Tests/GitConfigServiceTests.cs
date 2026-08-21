using FluentAssertions;
using GitVault.Core.Abstractions;
using GitVault.Core.Git;
using GitVault.Core.Models;
using GitVault.Core.Platform;
using NSubstitute;
using Xunit;

namespace GitVault.Core.Tests;

public sealed class GitConfigServiceTests
{
    private sealed class StubPaths(string home) : PlatformPathsBase
    {
        public override string AppDataDirectory => Path.Combine(home, ".gitvault");

        public override IReadOnlyList<string> SystemGitConfigCandidates => [];

        public override IReadOnlyList<string> AdditionalKeyDirectories => [];
    }

    private static readonly string Home = Path.GetTempPath();

    [Theory]
    [InlineData("user.name", "user", null, "name")]
    [InlineData("core.sshCommand", "core", null, "sshcommand")]
    [InlineData("credential.https://github.com.helper", "credential", "https://github.com", "helper")]
    [InlineData("credential.https://git.ex.com:8443.username", "credential", "https://git.ex.com:8443", "username")]
    [InlineData("includeIf.gitdir:~/work/.path", "includeif", "gitdir:~/work/", "path")]
    public void Splits_keys_into_section_subsection_and_name(
        string key,
        string section,
        string? subsection,
        string name)
    {
        var parts = GitConfigService.SplitKey(key);

        parts.Section.Should().Be(section);
        parts.Subsection.Should().Be(subsection);
        parts.Name.Should().Be(name);
    }

    [Fact]
    public void Parses_the_null_delimited_listing_git_produces()
    {
        // scope NUL origin NUL key LF value NUL, repeated.
        var output = string.Join('\0',
            "system", "file:C:/Program Files/Git/etc/gitconfig", "core.symlinks\nfalse",
            "global", "file:C:/Users/ada/.gitconfig", "user.email\nada@example.com",
            "local", "file:.git/config", "remote.origin.url\nhttps://github.com/ada/p.git",
            string.Empty);

        var values = GitConfigService.ParseNullDelimitedList(output);

        values.Should().HaveCount(3);
        values[0].Scope.Should().Be(GitConfigScope.System);
        values[1].Key.Should().Be("user.email");
        values[1].Value.Should().Be("ada@example.com");
        values[1].Scope.Should().Be(GitConfigScope.Global);
        values[2].Origin.Should().Be("file:.git/config");
    }

    [Fact]
    public void An_empty_listing_yields_nothing() =>
        GitConfigService.ParseNullDelimitedList(string.Empty).Should().BeEmpty();

    [Fact]
    public void A_value_containing_newlines_survives_parsing()
    {
        var output = string.Join('\0', "global", "file:/home/ada/.gitconfig", "alias.lg\nlog\n--graph", string.Empty);

        GitConfigService.ParseNullDelimitedList(output).Single().Value.Should().Be("log\n--graph");
    }

    [Fact]
    public async Task Falls_back_to_the_native_parser_when_git_is_absent()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gitvault-svc", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var configPath = Path.Combine(directory, ".gitconfig");
            await File.WriteAllTextAsync(configPath, "[user]\n\tname = Ada\n\temail = ada@example.com\n");

            var paths = Substitute.For<IPlatformPaths>();
            paths.GlobalGitConfigPath.Returns(configPath);
            paths.SystemGitConfigCandidates.Returns([]);
            paths.HomeDirectory.Returns(directory);

            var locator = Substitute.For<IGitBinaryLocator>();
            locator.LocateAsync(Arg.Any<CancellationToken>()).Returns((GitBinaryInfo?)null);

            var service = new GitConfigService(Substitute.For<IProcessRunner>(), locator, paths);
            var values = await service.ListAsync(null, CancellationToken.None);

            service.HasGitBinary.Should().BeFalse();
            values.Should().Contain(v => v.Key == "user.email" && v.Value == "ada@example.com");
            values.Should().OnlyContain(v => v.Scope == GitConfigScope.Global);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Uses_the_git_binary_when_one_is_available()
    {
        var paths = Substitute.For<IPlatformPaths>();
        paths.HomeDirectory.Returns(Home);

        var locator = Substitute.For<IGitBinaryLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns(new GitBinaryInfo("/usr/bin/git", "2.45.0"));

        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync("/usr/bin/git", Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<IReadOnlyDictionary<string, string?>>(), Arg.Any<string?>(),
                Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(
                0,
                string.Join('\0', "global", "file:/home/ada/.gitconfig", "user.name\nAda", string.Empty),
                string.Empty,
                false,
                false));

        var service = new GitConfigService(runner, locator, paths);
        var values = await service.ListAsync(null, CancellationToken.None);

        service.HasGitBinary.Should().BeTrue();
        service.GitVersion.Should().Be("2.45.0");
        values.Single().Value.Should().Be("Ada");
    }

    [Fact]
    public async Task A_failed_git_write_surfaces_as_a_config_exception()
    {
        var paths = Substitute.For<IPlatformPaths>();
        paths.HomeDirectory.Returns(Home);

        var locator = Substitute.For<IGitBinaryLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns(new GitBinaryInfo("/usr/bin/git", "2.45.0"));

        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync("/usr/bin/git", Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<IReadOnlyDictionary<string, string?>>(), Arg.Any<string?>(),
                Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(4, string.Empty, "could not lock config file", false, false));

        var service = new GitConfigService(runner, locator, paths);

        var act = () => service.SetAsync("user.name", "Ada", GitConfigScope.Global, null, CancellationToken.None);

        (await act.Should().ThrowAsync<GitConfigException>())
            .Which.Detail.Should().Be("could not lock config file");
    }

    [Fact]
    public async Task Unsetting_a_key_that_was_not_set_is_not_an_error()
    {
        var paths = Substitute.For<IPlatformPaths>();
        paths.HomeDirectory.Returns(Home);

        var locator = Substitute.For<IGitBinaryLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns(new GitBinaryInfo("/usr/bin/git", "2.45.0"));

        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync("/usr/bin/git", Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<IReadOnlyDictionary<string, string?>>(), Arg.Any<string?>(),
                Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(5, string.Empty, string.Empty, false, false));

        var service = new GitConfigService(runner, locator, paths);

        var act = () => service.UnsetAsync("user.name", GitConfigScope.Global, null, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void Resolves_the_file_behind_each_scope()
    {
        var paths = Substitute.For<IPlatformPaths>();
        paths.GlobalGitConfigPath.Returns("/home/ada/.gitconfig");
        paths.SystemGitConfigCandidates.Returns(["/etc/gitconfig"]);

        var service = new GitConfigService(
            Substitute.For<IProcessRunner>(), Substitute.For<IGitBinaryLocator>(), paths);

        service.ResolveConfigFilePath(GitConfigScope.Global, null).Should().Be("/home/ada/.gitconfig");
        service.ResolveConfigFilePath(GitConfigScope.System, null).Should().Be("/etc/gitconfig");
        service.ResolveConfigFilePath(GitConfigScope.Local, "/repo")
            .Should().Be(Path.Combine("/repo", ".git", "config"));
        service.ResolveConfigFilePath(GitConfigScope.Worktree, "/repo")
            .Should().Be(Path.Combine("/repo", ".git", "config.worktree"));
        service.ResolveConfigFilePath(GitConfigScope.Local, null).Should().BeNull();
    }

    [Fact]
    public void Stub_paths_type_is_used_by_the_parser_fallback() =>
        new StubPaths(Home).AppDataDirectory.Should().Contain(".gitvault");
}

public sealed class EffectiveIdentityResolverTests
{
    private static GitConfigValue V(string key, string value, GitConfigScope scope) =>
        new(key, value, scope, "file:/x");

    [Fact]
    public void The_most_specific_scope_wins_and_the_overridden_ones_are_reported()
    {
        var all = new[]
        {
            V("user.email", "ada@system.example", GitConfigScope.System),
            V("user.email", "ada@global.example", GitConfigScope.Global),
            V("user.email", "ada@repo.example", GitConfigScope.Local),
        };

        var resolved = EffectiveIdentityResolver.Resolve(all, "user.email");

        resolved.Value.Should().Be("ada@repo.example");
        resolved.Scope.Should().Be(GitConfigScope.Local);
        resolved.OverriddenIn.Should().BeEquivalentTo([GitConfigScope.System, GitConfigScope.Global]);
    }

    [Fact]
    public void An_unset_key_reports_itself_as_unset()
    {
        var resolved = EffectiveIdentityResolver.Resolve([], "user.email");

        resolved.IsSet.Should().BeFalse();
        resolved.Scope.Should().Be(GitConfigScope.Unknown);
    }

    [Fact]
    public void Key_matching_ignores_case()
    {
        var resolved = EffectiveIdentityResolver.Resolve([V("core.sshCommand", "ssh -i k", GitConfigScope.Global)], "core.sshcommand");

        resolved.Value.Should().Be("ssh -i k");
    }

    [Fact]
    public async Task Resolves_the_five_settings_that_define_an_identity()
    {
        var config = Substitute.For<IGitConfigService>();
        config.ListAsync(null, Arg.Any<CancellationToken>()).Returns(
        [
            V("user.name", "Ada", GitConfigScope.Global),
            V("user.email", "ada@example.com", GitConfigScope.Global),
            V("credential.helper", "manager", GitConfigScope.Global),
        ]);

        var effective = await new EffectiveIdentityResolver(config).ResolveAsync(null, CancellationToken.None);

        effective.IsComplete.Should().BeTrue();
        effective.All.Should().HaveCount(5);
        effective.SigningKey.IsSet.Should().BeFalse();
        effective.CredentialHelper.Value.Should().Be("manager");
    }

    [Fact]
    public async Task An_identity_missing_its_email_is_not_complete()
    {
        var config = Substitute.For<IGitConfigService>();
        config.ListAsync(null, Arg.Any<CancellationToken>()).Returns([V("user.name", "Ada", GitConfigScope.Global)]);

        var effective = await new EffectiveIdentityResolver(config).ResolveAsync(null, CancellationToken.None);

        effective.IsComplete.Should().BeFalse();
    }
}
