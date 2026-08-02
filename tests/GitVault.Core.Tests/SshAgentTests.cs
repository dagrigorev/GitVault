using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using GitVault.Core.Models;
using GitVault.Core.Ssh;
using GitVault.Core.Ssh.Agent;
using Xunit;
using Xunit.Abstractions;

namespace GitVault.Core.Tests;

public sealed class SshAgentClientTests
{
    private static readonly AgentEndpoint Endpoint =
        new(AgentKind.OpenSshUnix, "/tmp/agent.sock", AgentTransportKind.UnixSocket);

    private static byte[] PublicBlob(string fixture)
    {
        SshPublicKeyReader.TryParseFile(SshFixtures.Text(fixture + ".pub"), out var key).Should().BeTrue();
        return key!.Blob;
    }

    private static (SshAgentClient Client, FakeSshAgent Agent) Build(bool supportsWrites = true)
    {
        var agent = new FakeSshAgent();
        var endpoint = Endpoint with { SupportsWrites = supportsWrites };
        return (new SshAgentClient(endpoint, new FakeAgentTransportFactory(agent)), agent);
    }

    [Fact]
    public async Task Lists_the_identities_the_agent_holds()
    {
        var (client, agent) = Build();
        using var _ = client;

        agent.Seed(PublicBlob("ed25519_plain"), "ada@example.com");
        agent.Seed(PublicBlob("rsa4096_plain"), "rsa4096@example.com");

        var identities = await client.ListIdentitiesAsync(CancellationToken.None);

        identities.Should().HaveCount(2);
        identities[0].FingerprintSha256.Should().Be(SshFixtures.Expected["ed25519_plain"].Sha256);
        identities[0].Comment.Should().Be("ada@example.com");
        identities[0].Algorithm.Should().Be(SshKeyAlgorithm.Ed25519);
        identities[1].Algorithm.Should().Be(SshKeyAlgorithm.Rsa);
    }

    [Fact]
    public async Task An_empty_agent_reports_no_identities()
    {
        var (client, _) = Build();
        using var _1 = client;

        (await client.ListIdentitiesAsync(CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Probing_a_reachable_agent_fills_in_its_descriptor()
    {
        var (client, agent) = Build();
        using var _ = client;
        agent.Seed(PublicBlob("ed25519_plain"), "ada@example.com");

        var info = await client.ProbeAsync(CancellationToken.None);

        info.IsRunning.Should().BeTrue();
        info.Kind.Should().Be(AgentKind.OpenSshUnix);
        info.LoadedKeys.Should().ContainSingle();
        info.SupportsAdd.Should().BeTrue();
    }

    [Fact]
    public async Task Probing_an_unreachable_agent_reports_it_stopped_rather_than_throwing()
    {
        using var client = new SshAgentClient(
            Endpoint with { Endpoint = "/definitely/not/a/socket" },
            new PortableAgentTransportFactory());

        var info = await client.ProbeAsync(CancellationToken.None);

        info.IsRunning.Should().BeFalse();
        info.StatusDetail.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Adds_an_identity_and_the_agent_then_reports_it()
    {
        var (client, agent) = Build();
        using var _ = client;

        var added = await client.AddIdentityAsync(
            BuildEd25519PrivateBlob(), "added@example.com", null, false, CancellationToken.None);

        added.Should().BeTrue();
        agent.Count.Should().Be(1);
        agent.ReceivedMessageNumbers.Should().Contain(AgentProtocol.AddIdentity);
    }

    [Fact]
    public async Task A_lifetime_constraint_uses_the_constrained_message()
    {
        var (client, agent) = Build();
        using var _ = client;

        await client.AddIdentityAsync(
            BuildEd25519PrivateBlob(), "temp@example.com", 900, false, CancellationToken.None);

        agent.ReceivedMessageNumbers.Should().Contain(AgentProtocol.AddIdConstrained);
        agent.LastAddConstraints.Lifetime.Should().Be(900);
        agent.LastAddConstraints.Confirm.Should().BeFalse();
    }

    [Fact]
    public async Task A_confirmation_requirement_uses_the_constrained_message()
    {
        var (client, agent) = Build();
        using var _ = client;

        await client.AddIdentityAsync(
            BuildEd25519PrivateBlob(), "confirm@example.com", null, true, CancellationToken.None);

        agent.ReceivedMessageNumbers.Should().Contain(AgentProtocol.AddIdConstrained);
        agent.LastAddConstraints.Confirm.Should().BeTrue();
    }

    [Fact]
    public async Task Both_constraints_can_be_combined()
    {
        var (client, agent) = Build();
        using var _ = client;

        await client.AddIdentityAsync(
            BuildEd25519PrivateBlob(), "both@example.com", 60, true, CancellationToken.None);

        agent.LastAddConstraints.Should().Be((60, true));
    }

    [Fact]
    public async Task Removes_one_identity_by_blob()
    {
        var (client, agent) = Build();
        using var _ = client;

        var blob = PublicBlob("ed25519_plain");
        agent.Seed(blob, "ada@example.com");
        agent.Seed(PublicBlob("rsa2048_plain"), "other@example.com");

        (await client.RemoveIdentityAsync(blob, CancellationToken.None)).Should().BeTrue();

        agent.Count.Should().Be(1);
    }

    [Fact]
    public async Task Removing_an_identity_the_agent_does_not_hold_reports_failure()
    {
        var (client, _) = Build();
        using var _1 = client;

        (await client.RemoveIdentityAsync(PublicBlob("ed25519_plain"), CancellationToken.None))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Removes_every_identity()
    {
        var (client, agent) = Build();
        using var _ = client;

        agent.Seed(PublicBlob("ed25519_plain"), "a");
        agent.Seed(PublicBlob("rsa2048_plain"), "b");

        (await client.RemoveAllIdentitiesAsync(CancellationToken.None)).Should().BeTrue();

        agent.Count.Should().Be(0);
    }

    [Fact]
    public async Task Locking_hides_the_identities_and_unlocking_brings_them_back()
    {
        var (client, agent) = Build();
        using var _ = client;
        agent.Seed(PublicBlob("ed25519_plain"), "ada@example.com");

        var passphrase = Encoding.UTF8.GetBytes("lock me");

        (await client.SetLockedAsync(passphrase, true, CancellationToken.None)).Should().BeTrue();
        (await client.ListIdentitiesAsync(CancellationToken.None)).Should().BeEmpty();

        (await client.SetLockedAsync(passphrase, false, CancellationToken.None)).Should().BeTrue();
        (await client.ListIdentitiesAsync(CancellationToken.None)).Should().ContainSingle();
    }

    [Fact]
    public async Task A_read_only_agent_refuses_writes_without_sending_anything()
    {
        var (client, agent) = Build(supportsWrites: false);
        using var _ = client;

        (await client.AddIdentityAsync(BuildEd25519PrivateBlob(), "x", null, false, CancellationToken.None))
            .Should().BeFalse();
        (await client.RemoveAllIdentitiesAsync(CancellationToken.None)).Should().BeFalse();

        agent.ReceivedMessageNumbers.Should().BeEmpty("a read-only agent must not be bothered with writes");
    }

    [Fact]
    public async Task A_refusing_agent_produces_false_rather_than_an_exception()
    {
        var (client, agent) = Build();
        using var _ = client;
        agent.RefuseEverything = true;

        (await client.RemoveAllIdentitiesAsync(CancellationToken.None)).Should().BeFalse();
    }

    private static byte[] BuildEd25519PrivateBlob()
    {
        // The shape ssh-add sends: key type, public part, private part.
        var writer = new SshWireWriter();
        writer.WriteText("ssh-ed25519");
        writer.WriteString(new byte[32]);
        writer.WriteString(new byte[64]);
        return writer.ToArray();
    }
}

/// <summary>
/// Drives the client over a real unix domain socket, so the length framing and the socket
/// transport are covered rather than bypassed.
/// </summary>
public sealed class SshAgentSocketTransportTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _socketPath = Path.Combine(
        Path.GetTempPath(),
        "gv-" + Guid.NewGuid().ToString("N")[..8] + ".sock");

    [Fact]
    public async Task Talks_to_an_agent_over_a_unix_socket()
    {
        if (!Socket.OSSupportsUnixDomainSockets)
        {
            output.WriteLine("This platform has no AF_UNIX support; skipping.");
            return;
        }

        var agent = new FakeSshAgent();
        SshPublicKeyReader.TryParseFile(SshFixtures.Text("ed25519_plain.pub"), out var key).Should().BeTrue();
        agent.Seed(key!.Blob, "ada@example.com");

        using var server = new FakeAgentSocketServer(agent, _socketPath);
        using var client = new SshAgentClient(
            new AgentEndpoint(AgentKind.OpenSshUnix, _socketPath, AgentTransportKind.UnixSocket),
            new PortableAgentTransportFactory());

        var info = await client.ProbeAsync(CancellationToken.None);

        info.IsRunning.Should().BeTrue();
        info.LoadedKeys.Should().ContainSingle();
        info.LoadedKeys[0].FingerprintSha256.Should().Be(SshFixtures.Expected["ed25519_plain"].Sha256);
    }

    [Fact]
    public async Task Several_exchanges_in_a_row_work()
    {
        if (!Socket.OSSupportsUnixDomainSockets)
        {
            output.WriteLine("This platform has no AF_UNIX support; skipping.");
            return;
        }

        var agent = new FakeSshAgent();
        using var server = new FakeAgentSocketServer(agent, _socketPath);
        using var client = new SshAgentClient(
            new AgentEndpoint(AgentKind.OpenSshUnix, _socketPath, AgentTransportKind.UnixSocket),
            new PortableAgentTransportFactory());

        (await client.ListIdentitiesAsync(CancellationToken.None)).Should().BeEmpty();
        (await client.RemoveAllIdentitiesAsync(CancellationToken.None)).Should().BeTrue();
        (await client.ListIdentitiesAsync(CancellationToken.None)).Should().BeEmpty();
    }

    public void Dispose()
    {
        try
        {
            File.Delete(_socketPath);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
}

public sealed class AgentProtocolTests
{
    [Fact]
    public void Framing_prefixes_the_payload_length()
    {
        var framed = AgentProtocol.FrameSimple(AgentProtocol.RequestIdentities);

        framed.Should().BeEquivalentTo(new byte[] { 0, 0, 0, 1, AgentProtocol.RequestIdentities });
    }

    [Fact]
    public void A_reply_that_is_not_an_identities_answer_is_rejected()
    {
        var act = () => AgentProtocol.ParseIdentitiesAnswer(new byte[] { AgentProtocol.Failure });

        act.Should().Throw<SshWireException>();
    }

    [Fact]
    public void An_implausible_identity_count_is_rejected()
    {
        var writer = new SshWireWriter();
        writer.WriteByte(AgentProtocol.IdentitiesAnswer);
        writer.WriteUInt32(100000);

        var act = () => AgentProtocol.ParseIdentitiesAnswer(writer.ToArray());

        act.Should().Throw<SshWireException>();
    }

    [Fact]
    public void An_unmodelled_key_type_still_yields_a_fingerprint()
    {
        var blobWriter = new SshWireWriter();
        blobWriter.WriteText("ssh-something-new@example.com");
        blobWriter.WriteString([1, 2, 3]);
        var blob = blobWriter.ToArray();

        var writer = new SshWireWriter();
        writer.WriteByte(AgentProtocol.IdentitiesAnswer);
        writer.WriteUInt32(1);
        writer.WriteString(blob);
        writer.WriteText("exotic");

        var entries = AgentProtocol.ParseIdentitiesAnswer(writer.ToArray());

        entries.Should().ContainSingle();
        entries[0].Algorithm.Should().Be(SshKeyAlgorithm.Unknown);
        entries[0].FingerprintSha256.Should().Be(SshFingerprint.Sha256(blob));
    }

    [Fact]
    public void Add_without_constraints_uses_the_plain_message()
    {
        var request = AgentProtocol.BuildAddIdentity([1, 2, 3], "c", null, false);

        request[4].Should().Be(AgentProtocol.AddIdentity);
    }

    [Fact]
    public void A_zero_lifetime_is_not_treated_as_a_constraint()
    {
        var request = AgentProtocol.BuildAddIdentity([1, 2, 3], "c", 0, false);

        request[4].Should().Be(AgentProtocol.AddIdentity);
    }

    [Fact]
    public void Success_and_failure_replies_are_distinguished()
    {
        AgentProtocol.IsSuccess([AgentProtocol.Success]).Should().BeTrue();
        AgentProtocol.IsSuccess([AgentProtocol.Failure]).Should().BeFalse();
        AgentProtocol.IsSuccess([]).Should().BeFalse();
    }
}

public sealed class EmulatedSocketDescriptorTests
{
    [Fact]
    public void Reads_the_port_and_nonce_gpg_agent_writes()
    {
        var nonce = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        var contents = Encoding.ASCII.GetBytes("54321\n").Concat(nonce).ToArray();

        EmulatedSocketAgentTransport.TryReadDescriptor(contents, out var port, out var read).Should().BeTrue();

        port.Should().Be(54321);
        read.Should().BeEquivalentTo(nonce);
    }

    [Theory]
    [InlineData("")]
    [InlineData("54321")]
    [InlineData("54321\nshort")]
    [InlineData("notaport\n0123456789abcdef")]
    [InlineData("99999999\n0123456789abcdef")]
    public void Malformed_descriptors_are_rejected(string text) =>
        EmulatedSocketAgentTransport
            .TryReadDescriptor(Encoding.ASCII.GetBytes(text), out _, out _)
            .Should().BeFalse();
}

public sealed class AgentShellSnippetTests
{
    private static SshAgentInfo Agent(AgentKind kind, string endpoint) =>
        new(kind, endpoint, IsRunning: true, SupportsAdd: true, SupportsConstraints: true);

    [Theory]
    [InlineData(ShellKind.Bash, "export SSH_AUTH_SOCK='/tmp/agent.sock'")]
    [InlineData(ShellKind.Zsh, "export SSH_AUTH_SOCK='/tmp/agent.sock'")]
    [InlineData(ShellKind.Fish, "set -gx SSH_AUTH_SOCK '/tmp/agent.sock'")]
    [InlineData(ShellKind.PowerShell, "$env:SSH_AUTH_SOCK = '/tmp/agent.sock'")]
    [InlineData(ShellKind.Cmd, "set SSH_AUTH_SOCK=/tmp/agent.sock")]
    public void Each_shell_gets_its_own_syntax(ShellKind shell, string expected) =>
        AgentShellSnippets.Build(Agent(AgentKind.OpenSshUnix, "/tmp/agent.sock"), shell)
            .Should().Be(expected);

    [Fact]
    public void The_agent_pid_is_included_when_known()
    {
        var snippet = AgentShellSnippets.Build(
            Agent(AgentKind.OpenSshUnix, "/tmp/agent.sock"), ShellKind.Bash, agentProcessId: 4321);

        snippet.Should().Contain("export SSH_AGENT_PID=4321");
    }

    [Fact]
    public void A_path_containing_a_quote_is_escaped()
    {
        var snippet = AgentShellSnippets.Build(
            Agent(AgentKind.OpenSshUnix, "/tmp/it's/agent.sock"), ShellKind.Bash);

        snippet.Should().Be(@"export SSH_AUTH_SOCK='/tmp/it'\''s/agent.sock'");
    }

    [Theory]
    [InlineData(AgentKind.OpenSshWindowsPipe)]
    [InlineData(AgentKind.Pageant)]
    public void Pipe_based_agents_get_an_explanation_rather_than_a_broken_export(AgentKind kind)
    {
        var snippet = AgentShellSnippets.Build(Agent(kind, "openssh-ssh-agent"), ShellKind.Bash);

        snippet.Should().StartWith("#");
        snippet.Should().NotContain("export");
    }
}
