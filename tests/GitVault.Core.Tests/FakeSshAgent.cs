using System.Buffers.Binary;
using System.Net.Sockets;
using GitVault.Core.Ssh;
using GitVault.Core.Ssh.Agent;

namespace GitVault.Core.Tests;

/// <summary>
/// An in-process agent that speaks the real wire protocol. It is the reference the client is
/// tested against, so the tests exercise encoding and decoding rather than a mock's expectations.
/// </summary>
internal sealed class FakeSshAgent
{
    private readonly List<(byte[] Blob, string Comment)> _identities = [];

    /// <summary>Requests the agent has served, for assertions about what was sent.</summary>
    internal List<byte> ReceivedMessageNumbers { get; } = [];

    /// <summary>Constraints seen on the last add request: lifetime seconds, then confirm flag.</summary>
    internal (int? Lifetime, bool Confirm) LastAddConstraints { get; private set; }

    /// <summary>When true, every request is answered with a bare failure.</summary>
    internal bool RefuseEverything { get; set; }

    /// <summary>When true, the agent reports itself locked by holding no identities.</summary>
    internal bool IsLocked { get; private set; }

    /// <summary>Adds an identity the agent will report.</summary>
    /// <param name="blob">Public key blob.</param>
    /// <param name="comment">Comment to report with it.</param>
    internal void Seed(byte[] blob, string comment) => _identities.Add((blob, comment));

    /// <summary>Number of identities currently held.</summary>
    internal int Count => _identities.Count;

    /// <summary>Handles one framed request and produces one framed reply.</summary>
    /// <param name="framedRequest">Request including its length prefix.</param>
    /// <returns>The reply including its length prefix.</returns>
    internal byte[] Handle(ReadOnlySpan<byte> framedRequest)
    {
        var length = (int)BinaryPrimitives.ReadUInt32BigEndian(framedRequest);
        var payload = framedRequest.Slice(4, length);
        var messageNumber = payload[0];
        ReceivedMessageNumbers.Add(messageNumber);

        if (RefuseEverything)
        {
            return AgentProtocol.Frame([AgentProtocol.Failure]);
        }

        switch (messageNumber)
        {
            case AgentProtocol.RequestIdentities:
            {
                var writer = new SshWireWriter();
                writer.WriteByte(AgentProtocol.IdentitiesAnswer);
                writer.WriteUInt32((uint)(IsLocked ? 0 : _identities.Count));

                if (!IsLocked)
                {
                    foreach (var (blob, comment) in _identities)
                    {
                        writer.WriteString(blob);
                        writer.WriteText(comment);
                    }
                }

                return AgentProtocol.Frame(writer.ToArray());
            }

            case AgentProtocol.AddIdentity:
            case AgentProtocol.AddIdConstrained:
            {
                ReadAddRequest(payload[1..], messageNumber == AgentProtocol.AddIdConstrained);
                return AgentProtocol.Frame([AgentProtocol.Success]);
            }

            case AgentProtocol.RemoveIdentity:
            {
                var reader = new SshWireReader(payload[1..]);
                var blob = reader.ReadString().ToArray();
                var removed = _identities.RemoveAll(i => i.Blob.AsSpan().SequenceEqual(blob));
                return AgentProtocol.Frame([removed > 0 ? AgentProtocol.Success : AgentProtocol.Failure]);
            }

            case AgentProtocol.RemoveAllIdentities:
                _identities.Clear();
                return AgentProtocol.Frame([AgentProtocol.Success]);

            case AgentProtocol.Lock:
                IsLocked = true;
                return AgentProtocol.Frame([AgentProtocol.Success]);

            case AgentProtocol.Unlock:
                IsLocked = false;
                return AgentProtocol.Frame([AgentProtocol.Success]);

            default:
                return AgentProtocol.Frame([AgentProtocol.Failure]);
        }
    }

    private void ReadAddRequest(ReadOnlySpan<byte> body, bool constrained)
    {
        var reader = new SshWireReader(body);

        // The fake only models ed25519, which is enough to prove the framing round-trips.
        var keyType = reader.ReadText();
        var publicPart = reader.ReadString().ToArray();
        reader.ReadString();                         // private part, discarded

        var writer = new SshWireWriter();
        writer.WriteText(keyType);
        writer.WriteString(publicPart);

        var comment = reader.ReadText();
        _identities.Add((writer.ToArray(), comment));

        int? lifetime = null;
        var confirm = false;

        if (constrained)
        {
            while (reader.Remaining > 0)
            {
                var constraint = reader.ReadByte();
                if (constraint == AgentProtocol.ConstrainLifetime)
                {
                    lifetime = (int)reader.ReadUInt32();
                }
                else if (constraint == AgentProtocol.ConstrainConfirm)
                {
                    confirm = true;
                }
                else
                {
                    break;
                }
            }
        }

        LastAddConstraints = (lifetime, confirm);
    }
}

/// <summary>Hands the client a transport that talks to a <see cref="FakeSshAgent"/> directly.</summary>
internal sealed class FakeAgentTransportFactory(FakeSshAgent agent) : ISshAgentTransportFactory
{
    public bool CanHandle(AgentEndpoint endpoint) => true;

    public ISshAgentTransport Create(AgentEndpoint endpoint) => new FakeTransport(agent);

    private sealed class FakeTransport(FakeSshAgent agent) : ISshAgentTransport
    {
        public Task<byte[]> ExchangeAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken)
        {
            var framed = agent.Handle(request.Span);

            // Strip the length prefix, which is what a stream transport also returns.
            return Task.FromResult(framed[4..]);
        }

        public void Dispose()
        {
        }
    }
}

/// <summary>
/// Runs a <see cref="FakeSshAgent"/> behind a real unix domain socket, so the stream framing and
/// the socket transport are exercised end to end.
/// </summary>
internal sealed class FakeAgentSocketServer : IDisposable
{
    private readonly Socket _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _acceptLoop;

    internal FakeAgentSocketServer(FakeSshAgent agent, string socketPath)
    {
        SocketPath = socketPath;

        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        _listener.Listen(8);

        _acceptLoop = Task.Run(() => AcceptAsync(agent, _shutdown.Token));
    }

    /// <summary>Path the fake agent is listening on.</summary>
    internal string SocketPath { get; }

    public void Dispose()
    {
        _shutdown.Cancel();
        _listener.Dispose();

        try
        {
            _acceptLoop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // The loop ends by having its socket torn out from under it; that is the shutdown.
        }

        _shutdown.Dispose();

        try
        {
            File.Delete(SocketPath);
        }
        catch (IOException)
        {
            // Best effort: a leftover socket file in temp is harmless.
        }
    }

    private async Task AcceptAsync(FakeSshAgent agent, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket connection;
            try
            {
                connection = await _listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            using (connection)
            {
                await ServeAsync(agent, connection, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task ServeAsync(FakeSshAgent agent, Socket connection, CancellationToken cancellationToken)
    {
        var header = new byte[4];

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!await ReceiveExactlyAsync(connection, header, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(header);
            var payload = new byte[length];
            if (!await ReceiveExactlyAsync(connection, payload, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            var request = new byte[4 + length];
            header.CopyTo(request, 0);
            payload.CopyTo(request, 4);

            var reply = agent.Handle(request);
            await connection.SendAsync(reply, SocketFlags.None, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<bool> ReceiveExactlyAsync(
        Socket socket,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            int chunk;
            try
            {
                chunk = await socket.ReceiveAsync(buffer[read..], SocketFlags.None, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (SocketException)
            {
                return false;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }

            if (chunk == 0)
            {
                return false;
            }

            read += chunk;
        }

        return true;
    }
}
