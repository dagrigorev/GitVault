using System.Text;
using FluentAssertions;
using GitVault.Core.Abstractions;
using GitVault.Core.Credentials;
using GitVault.Core.Diagnostics;
using GitVault.Core.Discovery;
using GitVault.Core.Git;
using GitVault.Core.Models;
using GitVault.Core.Platform;
using NSubstitute;
using Xunit;

namespace GitVault.Core.Tests;

file sealed class VaultPaths(string home) : PlatformPathsBase(home)
{
    public override string AppDataDirectory => Path.Combine(HomeDirectory, ".gitvault");

    public override IReadOnlyList<string> SystemGitConfigCandidates => [];

    public override IReadOnlyList<string> AdditionalKeyDirectories => [];
}

file sealed class OpenPermissions : IFilePermissionService
{
    public bool CanRestrictPermissions => true;

    public FilePermissionInfo? Read(string path) =>
        new(path, 0x1A4, "tester", IsWorldReadable: true, IsGroupReadable: true);

    public Task<bool> HardenAsync(string path, CancellationToken cancellationToken) => Task.FromResult(true);
}

public sealed class GitCredentialsFileVaultTests : IDisposable
{
    private readonly string _home =
        Path.Combine(Path.GetTempPath(), "gitvault-cred", Guid.NewGuid().ToString("N"));

    private readonly GitCredentialsFileVault _vault;

    public GitCredentialsFileVaultTests()
    {
        Directory.CreateDirectory(_home);
        _vault = new GitCredentialsFileVault(new VaultPaths(_home), new OpenPermissions());
    }

    private void WriteStore(string contents) =>
        File.WriteAllText(Path.Combine(_home, ".git-credentials"), contents);

    [Theory]
    [InlineData("https://octocat:hunter2@github.com", "https", "github.com", "octocat", true)]
    [InlineData("https://octocat@github.com", "https", "github.com", "octocat", false)]
    [InlineData("https://github.com", "https", "github.com", "", false)]
    [InlineData("ssh://git@git.example.com:2222", "ssh", "git.example.com:2222", "git", false)]
    [InlineData("https://user:p%40ss@host.example", "https", "host.example", "user", true)]
    public void Parses_store_lines(
        string line,
        string protocol,
        string host,
        string userName,
        bool hasPassword)
    {
        GitCredentialsFileVault.TryParseLine(line, out var p, out var h, out var u, out var pw)
            .Should().BeTrue();

        p.Should().Be(protocol);
        h.Should().Be(host);
        u.Should().Be(userName);
        pw.Should().Be(hasPassword);
    }

    [Theory]
    [InlineData("")]
    [InlineData("# a comment")]
    [InlineData("not a url")]
    public void Rejects_lines_that_are_not_credentials(string line) =>
        GitCredentialsFileVault.TryParseLine(line, out _, out _, out _, out _).Should().BeFalse();

    [Fact]
    public void A_password_containing_an_at_sign_is_split_at_the_last_one()
    {
        GitCredentialsFileVault.TryParseLine("https://user:p@ss@github.com", out _, out var host, out var user, out _)
            .Should().BeTrue();

        host.Should().Be("github.com");
        user.Should().Be("user");
        GitCredentialsFileVault.ExtractPassword("https://user:p@ss@github.com").Should().Be("p@ss");
    }

    [Fact]
    public void Percent_escapes_in_the_password_are_decoded() =>
        GitCredentialsFileVault.ExtractPassword("https://user:p%40ss@host.example").Should().Be("p@ss");

    [Fact]
    public async Task Lists_entries_and_flags_them_as_plaintext()
    {
        WriteStore("https://octocat:hunter2@github.com\nhttps://tanuki:secret@gitlab.com\n");

        var entries = await _vault.ListAsync(CancellationToken.None);

        entries.Should().HaveCount(2);
        entries.Should().OnlyContain(e => e.IsPlaintextStore);
        entries[0].Host.Should().Be("github.com");
        entries[0].SecretPresent.Should().BeTrue();
    }

    [Fact]
    public async Task Reveal_returns_the_password_for_a_target()
    {
        WriteStore("https://octocat:hunter2@github.com\n");

        var secret = await _vault.RevealAsync("https://github.com", CancellationToken.None);

        secret.Should().NotBeNull();
        Encoding.UTF8.GetString(secret!).Should().Be("hunter2");
    }

    [Fact]
    public async Task Reveal_of_an_unknown_target_returns_nothing()
    {
        WriteStore("https://octocat:hunter2@github.com\n");

        (await _vault.RevealAsync("https://nowhere.example", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Delete_removes_only_the_matching_line()
    {
        WriteStore("https://octocat:hunter2@github.com\nhttps://tanuki:secret@gitlab.com\n");

        (await _vault.DeleteAsync("https://github.com", CancellationToken.None)).Should().BeTrue();

        var remaining = await _vault.ListAsync(CancellationToken.None);
        remaining.Should().ContainSingle();
        remaining[0].Host.Should().Be("gitlab.com");
    }

    [Fact]
    public async Task Writing_into_a_plaintext_store_is_refused()
    {
        var entry = new CredentialEntry(
            VaultKind.GitCredentialsFile, "https://github.com", "github.com", "octocat",
            true, "https", null, null, false);

        var act = () => _vault.WriteAsync(entry, Encoding.UTF8.GetBytes("secret"), CancellationToken.None);

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task An_absent_store_lists_nothing()
    {
        _vault.IsAvailable.Should().BeFalse();
        (await _vault.ListAsync(CancellationToken.None)).Should().BeEmpty();
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

public sealed class CredentialTargetFilterTests
{
    private static CredentialEntry Entry(VaultKind vault, string target, string host = "") =>
        new(vault, target, host, "user", true, "https", null, null, false);

    [Theory]
    [InlineData("git:https://github.com")]
    [InlineData("LegacyGeneric:target=git:https://example.com")]
    [InlineData("https://gitlab.com")]
    [InlineData("bitbucket.org")]
    [InlineData("dev.azure.com/org")]
    [InlineData("my-git-server.internal")]
    public void Recognises_git_related_targets(string target) =>
        CredentialTargetFilter.IsGitRelated(Entry(VaultKind.WindowsCredentialManager, target))
            .Should().BeTrue();

    [Theory]
    [InlineData("MicrosoftOffice16_Data:ADAL")]
    [InlineData("https://mail.example.com")]
    public void Ignores_unrelated_targets(string target) =>
        CredentialTargetFilter.IsGitRelated(Entry(VaultKind.WindowsCredentialManager, target))
            .Should().BeFalse();

    [Fact]
    public void A_host_from_the_user_s_own_remotes_widens_the_filter()
    {
        var entry = Entry(VaultKind.WindowsCredentialManager, "https://forge.internal", "forge.internal");

        CredentialTargetFilter.IsGitRelated(entry).Should().BeFalse();
        CredentialTargetFilter.IsGitRelated(entry, ["forge.internal"]).Should().BeTrue();
    }

    [Fact]
    public void Entries_from_a_git_specific_store_are_always_relevant() =>
        CredentialTargetFilter.IsGitRelated(Entry(VaultKind.GcmDpapi, "anything"))
            .Should().BeTrue();

    [Theory]
    [InlineData("git:https://github.com", "github.com")]
    [InlineData("LegacyGeneric:target=git:https://user@dev.azure.com/org", "dev.azure.com")]
    [InlineData("https://git.example.com:8443/path", "git.example.com:8443")]
    [InlineData("github.com", "github.com")]
    [InlineData("", "")]
    public void Extracts_the_host(string target, string expected) =>
        CredentialTargetFilter.ExtractHost(target).Should().Be(expected);

    [Theory]
    [InlineData("git:https://github.com", "https")]
    [InlineData("git:ssh://git@github.com", "ssh")]
    [InlineData("git:http://old.example", "http")]
    [InlineData("github.com", "https")]
    public void Extracts_the_protocol(string target, string expected) =>
        CredentialTargetFilter.ExtractProtocol(target).Should().Be(expected);
}

public sealed class GitCredentialHelperClientTests
{
    private static IGitBinaryLocator Locator()
    {
        var locator = Substitute.For<IGitBinaryLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns(new GitBinaryInfo("/usr/bin/git", "2.45.0"));
        return locator;
    }

    [Fact]
    public void Builds_the_request_block_git_expects()
    {
        var request = GitCredentialHelperClient.BuildRequest("https", "github.com", null, null, default);

        request.Should().Be("protocol=https\nhost=github.com\n\n");
    }

    [Fact]
    public void Includes_the_username_and_password_when_supplied()
    {
        var request = GitCredentialHelperClient.BuildRequest(
            "https", "github.com", "org/repo", "octocat", Encoding.UTF8.GetBytes("hunter2"));

        request.Should().Contain("path=org/repo\n");
        request.Should().Contain("username=octocat\n");
        request.Should().Contain("password=hunter2\n");
        request.Should().EndWith("\n\n");
    }

    [Fact]
    public void Parses_the_reply_block()
    {
        var fields = GitCredentialHelperClient.ParseResponse(
            "protocol=https\nhost=github.com\nusername=octocat\npassword=hunter2\n");

        fields["host"].Should().Be("github.com");
        fields["username"].Should().Be("octocat");
        fields["password"].Should().Be("hunter2");
    }

    [Fact]
    public void Stops_at_the_blank_line_that_terminates_the_block()
    {
        var fields = GitCredentialHelperClient.ParseResponse("host=github.com\n\nusername=leaked\n");

        fields.Should().ContainKey("host");
        fields.Should().NotContainKey("username");
    }

    [Fact]
    public async Task Fill_reports_a_password_without_returning_it_unless_asked()
    {
        var runner = Substitute.For<IProcessRunner>();
        runner.RunWithInputAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(
                0, "protocol=https\nhost=github.com\nusername=octocat\npassword=hunter2\n", string.Empty, false, false));

        var client = new GitCredentialHelperClient(runner, Locator());

        var (description, password) = await client.FillAsync(
            "https", "github.com", null, revealPassword: false, CancellationToken.None);

        description.Should().NotBeNull();
        description!.UserName.Should().Be("octocat");
        description.HasPassword.Should().BeTrue();
        password.Should().BeNull("the caller did not ask to see it");
    }

    [Fact]
    public async Task Fill_returns_the_password_when_it_is_explicitly_requested()
    {
        var runner = Substitute.For<IProcessRunner>();
        runner.RunWithInputAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(
                0, "protocol=https\nhost=github.com\nusername=octocat\npassword=hunter2\n", string.Empty, false, false));

        var client = new GitCredentialHelperClient(runner, Locator());

        var (_, password) = await client.FillAsync(
            "https", "github.com", null, revealPassword: true, CancellationToken.None);

        Encoding.UTF8.GetString(password!).Should().Be("hunter2");
    }

    [Fact]
    public async Task The_password_goes_over_stdin_and_never_into_the_arguments()
    {
        IReadOnlyList<string>? capturedArguments = null;
        string? capturedInput = null;

        var runner = Substitute.For<IProcessRunner>();
        runner.RunWithInputAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedArguments = call.Arg<IReadOnlyList<string>>();
                capturedInput = call.ArgAt<string>(2);
                return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty, false, false));
            });

        var client = new GitCredentialHelperClient(runner, Locator());

        await client.ApproveAsync(
            "https", "github.com", "octocat", Encoding.UTF8.GetBytes("hunter2"), CancellationToken.None);

        capturedArguments.Should().BeEquivalentTo(["credential", "approve"]);
        capturedArguments.Should().NotContain(a => a.Contains("hunter2", StringComparison.Ordinal));
        capturedInput.Should().Contain("password=hunter2");
    }

    [Fact]
    public async Task Without_git_the_client_reports_nothing_rather_than_failing()
    {
        var locator = Substitute.For<IGitBinaryLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns((GitBinaryInfo?)null);

        var client = new GitCredentialHelperClient(Substitute.For<IProcessRunner>(), locator);

        var (description, _) = await client.FillAsync("https", "github.com", null, false, CancellationToken.None);

        description.Should().BeNull();
    }
}

public sealed class CredentialProbeTests
{
    private sealed class StubVault(VaultKind kind, params CredentialEntry[] entries) : ICredentialVault
    {
        public VaultKind Kind => kind;

        public bool IsAvailable { get; init; } = true;

        public bool IsReadOnly => false;

        public Func<Task>? OnList { get; init; }

        public async Task<IReadOnlyList<CredentialEntry>> ListAsync(CancellationToken cancellationToken)
        {
            if (OnList is not null)
            {
                await OnList().ConfigureAwait(false);
            }

            return entries;
        }

        public Task<byte[]?> RevealAsync(string target, CancellationToken cancellationToken) =>
            Task.FromResult<byte[]?>(null);

        public Task WriteAsync(CredentialEntry entry, ReadOnlyMemory<byte> secret, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<bool> DeleteAsync(string target, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }

    private static CredentialEntry Entry(VaultKind vault, string host) =>
        new(vault, $"https://{host}", host, "user", true, "https", null, null, false);

    [Fact]
    public async Task Collects_entries_from_every_available_vault()
    {
        var probe = new CredentialProbe(
        [
            new StubVault(VaultKind.WindowsCredentialManager, Entry(VaultKind.WindowsCredentialManager, "github.com")),
            new StubVault(VaultKind.GitCredentialsFile, Entry(VaultKind.GitCredentialsFile, "gitlab.com")),
        ]);

        var result = await probe.ProbeAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Credentials.Should().HaveCount(2);
    }

    [Fact]
    public async Task Unavailable_vaults_are_skipped()
    {
        var probe = new CredentialProbe(
        [
            new StubVault(VaultKind.MacKeychain, Entry(VaultKind.MacKeychain, "github.com")) { IsAvailable = false },
        ]);

        var result = await probe.ProbeAsync(CancellationToken.None);

        result.Value!.Credentials.Should().BeEmpty();
    }

    [Fact]
    public async Task A_plaintext_entry_raises_a_high_severity_warning()
    {
        var probe = new CredentialProbe(
        [
            new StubVault(VaultKind.GitCredentialsFile, Entry(VaultKind.GitCredentialsFile, "github.com")),
        ]);

        var result = await probe.ProbeAsync(CancellationToken.None);

        result.Value!.Warnings.Should().ContainSingle(w =>
            w.Code == CredentialProbe.PlaintextStoreCode && w.Severity == WarningSeverity.High);
    }

    [Fact]
    public async Task One_locked_vault_does_not_cost_us_the_others()
    {
        var probe = new CredentialProbe(
        [
            new StubVault(VaultKind.MacKeychain) { OnList = () => throw new UnauthorizedAccessException() },
            new StubVault(VaultKind.WindowsCredentialManager, Entry(VaultKind.WindowsCredentialManager, "github.com")),
        ]);

        var result = await probe.ProbeAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Credentials.Should().ContainSingle();
    }

    [Fact]
    public async Task When_every_vault_refuses_the_probe_reports_access_denied()
    {
        var probe = new CredentialProbe(
        [
            new StubVault(VaultKind.MacKeychain) { OnList = () => throw new UnauthorizedAccessException() },
        ]);

        var result = await probe.ProbeAsync(CancellationToken.None);

        result.Status.Should().Be(ProbeStatus.AccessDenied);
    }
}
