using FluentAssertions;
using GitVault.Core.Abstractions;
using GitVault.Core.Diagnostics;
using GitVault.Core.Discovery;
using GitVault.Core.Git;
using GitVault.Core.Models;
using NSubstitute;
using Xunit;

namespace GitVault.Core.Tests;

file sealed class FakeProbe(string id, Func<CancellationToken, Task<ProbeResult<ProbePayload>>> body) : IProbe
{
    public string ProbeId => id;

    public string DisplayName => id;

    public bool IsSupportedOnThisPlatform { get; init; } = true;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMilliseconds(300);

    public Task<ProbeResult<ProbePayload>> ProbeAsync(CancellationToken cancellationToken) =>
        body(cancellationToken);
}

public sealed class DiscoveryOrchestratorTests
{
    private static ProbePayload PayloadWith(params GitIdentity[] identities) =>
        new() { Identities = identities };

    [Fact]
    public async Task Merges_payloads_from_every_probe()
    {
        var a = new FakeProbe("a", _ => Task.FromResult(ProbeResult<ProbePayload>.Ok("a",
            PayloadWith(GitIdentity.Create("Ada", "ada@example.com", IdentitySource.GitGlobalConfig, "/g")))));
        var b = new FakeProbe("b", _ => Task.FromResult(ProbeResult<ProbePayload>.Ok("b",
            PayloadWith(GitIdentity.Create("Grace", "grace@example.com", IdentitySource.RepoLocal, "/r")))));

        var report = await new DiscoveryOrchestrator([a, b]).ScanAsync(CancellationToken.None);

        report.Identities.Should().HaveCount(2);
        report.ProbeStatuses.Should().OnlyContain(s => s.Status == ProbeStatus.Ok);
    }

    [Fact]
    public async Task A_throwing_probe_becomes_a_failed_status_and_does_not_abort_the_scan()
    {
        var bad = new FakeProbe("bad", _ => throw new InvalidOperationException("boom"));
        var good = new FakeProbe("good", _ => Task.FromResult(ProbeResult<ProbePayload>.Ok("good",
            PayloadWith(GitIdentity.Create("Ada", "ada@example.com", IdentitySource.GitGlobalConfig, "/g")))));

        var report = await new DiscoveryOrchestrator([bad, good]).ScanAsync(CancellationToken.None);

        report.Identities.Should().ContainSingle();
        report.ProbeStatuses.Single(s => s.ProbeId == "bad").Status.Should().Be(ProbeStatus.Failed);
        report.ProbeStatuses.Single(s => s.ProbeId == "bad").Diagnostics.Should().Be("boom");
    }

    [Fact]
    public async Task A_hanging_probe_times_out_without_stalling_the_scan()
    {
        var slow = new FakeProbe("slow", async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return ProbeResult<ProbePayload>.Ok("slow", ProbePayload.Empty);
        })
        { Timeout = TimeSpan.FromMilliseconds(50) };

        var report = await new DiscoveryOrchestrator([slow]).ScanAsync(CancellationToken.None);

        report.ProbeStatuses.Single().Status.Should().Be(ProbeStatus.Timeout);
    }

    [Fact]
    public async Task An_unsupported_probe_reports_not_applicable_without_running()
    {
        var ran = false;
        var probe = new FakeProbe("unsupported", _ =>
        {
            ran = true;
            return Task.FromResult(ProbeResult<ProbePayload>.Ok("unsupported", ProbePayload.Empty));
        })
        { IsSupportedOnThisPlatform = false };

        var report = await new DiscoveryOrchestrator([probe]).ScanAsync(CancellationToken.None);

        ran.Should().BeFalse();
        report.ProbeStatuses.Single().Status.Should().Be(ProbeStatus.NotApplicable);
    }

    [Fact]
    public async Task An_access_denied_probe_is_reported_as_such()
    {
        var probe = new FakeProbe("denied", _ => throw new UnauthorizedAccessException("keychain locked"));

        var report = await new DiscoveryOrchestrator([probe]).ScanAsync(CancellationToken.None);

        report.ProbeStatuses.Single().Status.Should().Be(ProbeStatus.AccessDenied);
    }

    [Fact]
    public void Identities_are_deduplicated_by_name_and_email_with_sources_merged()
    {
        var a = GitIdentity.Create("Ada", "ada@example.com", IdentitySource.GitGlobalConfig, "/g", hosts: ["github.com"]);
        var b = GitIdentity.Create("ada", "ADA@example.com", IdentitySource.GitKraken, "/k", hosts: ["gitlab.com"]);

        var merged = DiscoveryOrchestrator.DeduplicateIdentities([a, b]);

        merged.Should().ContainSingle();
        merged[0].Hosts.Should().BeEquivalentTo(["github.com", "gitlab.com"]);
        merged[0].Occurrences.Should().HaveCount(2);
    }

    [Fact]
    public void Keys_are_deduplicated_by_fingerprint_not_by_path()
    {
        var fingerprint = "SHA256:47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU";
        var fromDisk = new SshKey(Guid.NewGuid(), "/home/a/.ssh/id_ed25519", null, SshKeyAlgorithm.Ed25519,
            null, fingerprint, string.Empty, null, SshKeyFormat.OpenSsh, false, null, false);
        var fromAgent = new SshKey(Guid.NewGuid(), null, null, SshKeyAlgorithm.Ed25519,
            null, fingerprint, string.Empty, "work key", SshKeyFormat.Unknown, false, null, false)
        {
            LoadedInAgents = [new AgentRef(AgentKind.OpenSshUnix, "/tmp/agent.sock")],
        };

        var merged = DiscoveryOrchestrator.DeduplicateKeys([fromDisk, fromAgent]);

        merged.Should().ContainSingle();
        merged[0].PrivatePath.Should().Be("/home/a/.ssh/id_ed25519");
        merged[0].Comment.Should().Be("work key");
        merged[0].LoadedInAgents.Should().ContainSingle();
    }
}

public sealed class GitIdentityProbeTests
{
    private static GitConfigValue Value(string key, string value, GitConfigScope scope, string origin) =>
        new(key, value, scope, origin);

    [Fact]
    public void Builds_one_identity_per_originating_file()
    {
        var all = new[]
        {
            Value("user.name", "Ada", GitConfigScope.Global, "file:/home/ada/.gitconfig"),
            Value("user.email", "ada@home.example", GitConfigScope.Global, "file:/home/ada/.gitconfig"),
            Value("user.name", "Ada L", GitConfigScope.Global, "file:/home/ada/.gitconfig-work"),
            Value("user.email", "ada@work.example", GitConfigScope.Global, "file:/home/ada/.gitconfig-work"),
        };

        var identities = GitIdentityProbe.BuildIdentities(all);

        identities.Should().HaveCount(2);
        identities.Should().Contain(i => i.Email == "ada@work.example" && i.SourcePath == "/home/ada/.gitconfig-work");
    }

    [Fact]
    public void Files_with_no_identity_are_skipped()
    {
        var all = new[]
        {
            Value("core.autocrlf", "input", GitConfigScope.System, "file:/etc/gitconfig"),
            Value("user.email", "ada@example.com", GitConfigScope.Global, "file:/home/ada/.gitconfig"),
        };

        GitIdentityProbe.BuildIdentities(all).Should().ContainSingle();
    }

    [Fact]
    public void Signing_key_is_carried_across()
    {
        var all = new[]
        {
            Value("user.email", "ada@example.com", GitConfigScope.Global, "file:/home/ada/.gitconfig"),
            Value("user.signingkey", "ABCD1234", GitConfigScope.Global, "file:/home/ada/.gitconfig"),
        };

        GitIdentityProbe.BuildIdentities(all).Single().SigningKeyId.Should().Be("ABCD1234");
    }

    [Theory]
    [InlineData("credential.https://github.com.helper", "github.com")]
    [InlineData("credential.https://user@git.example.com:8443/path.username", "git.example.com:8443")]
    [InlineData("http.https://gitlab.com.sslVerify", "gitlab.com")]
    [InlineData("user.name", null)]
    [InlineData("credential.helper", null)]
    public void Extracts_hosts_from_url_subsections(string key, string? expected) =>
        GitIdentityProbe.ExtractHost(key).Should().Be(expected);

    [Fact]
    public async Task Raises_a_warning_when_no_identity_is_configured()
    {
        var config = Substitute.For<IGitConfigService>();
        config.HasGitBinary.Returns(true);
        config.ListAsync(null, Arg.Any<CancellationToken>()).Returns([]);

        var result = await new GitIdentityProbe(config).ProbeAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Warnings.Should().Contain(w => w.Code == "NoIdentityConfigured");
    }

    [Fact]
    public async Task Raises_a_warning_when_git_is_missing()
    {
        var config = Substitute.For<IGitConfigService>();
        config.HasGitBinary.Returns(false);
        config.ListAsync(null, Arg.Any<CancellationToken>()).Returns(
            [Value("user.name", "Ada", GitConfigScope.Global, "file:/home/ada/.gitconfig")]);

        var result = await new GitIdentityProbe(config).ProbeAsync(CancellationToken.None);

        result.Value!.Warnings.Should().Contain(w => w.Code == "GitNotFound");
    }
}
