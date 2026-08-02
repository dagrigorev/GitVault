using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace GitVault.Core.Ssh.Agent;

/// <summary>
/// Base transport for agents reachable as a byte stream. Owns the framing: a four-byte
/// big-endian length followed by that many bytes.
/// </summary>
public abstract class StreamAgentTransport : ISshAgentTransport
{
    private Stream? _stream;
    private bool _disposed;

    /// <summary>Opens the underlying stream. Called once, lazily.</summary>
    /// <param name="cancellationToken">Cancels the connection attempt.</param>
    /// <returns>A connected stream.</returns>
    protected abstract Task<Stream> ConnectAsync(CancellationToken cancellationToken);

    /// <inheritdoc/>
    public async Task<byte[]> ExchangeAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken)
    {
        _stream ??= await ConnectAsync(cancellationToken).ConfigureAwait(false);

        await _stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        var header = new byte[4];
        await ReadExactlyAsync(_stream, header, cancellationToken).ConfigureAwait(false);

        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length == 0 || length > AgentProtocol.MaxMessageLength)
        {
            throw new SshAgentException(
                $"Agent announced a {length}-byte message, which is outside the accepted range");
        }

        var payload = new byte[length];
        await ReadExactlyAsync(_stream, payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases the stream.</summary>
    /// <param name="disposing">True when called from <see cref="Dispose()"/>.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _stream?.Dispose();
            _stream = null;
        }

        _disposed = true;
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var chunk = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (chunk == 0)
            {
                throw new SshAgentException("Agent closed the connection mid-message");
            }

            read += chunk;
        }
    }
}

/// <summary>Talks to an agent listening on a unix domain socket.</summary>
/// <remarks>
/// Windows 10 1803 and later also support <c>AF_UNIX</c>, so this transport is not limited to
/// POSIX; it is simply the shape OpenSSH uses everywhere except its own Windows port.
/// </remarks>
public sealed class UnixSocketAgentTransport : StreamAgentTransport
{
    private readonly string _socketPath;
    private Socket? _socket;

    /// <summary>Creates the transport.</summary>
    /// <param name="socketPath">Path of the socket to connect to.</param>
    public UnixSocketAgentTransport(string socketPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
        _socketPath = socketPath;
    }

    /// <inheritdoc/>
    protected override async Task<Stream> ConnectAsync(CancellationToken cancellationToken)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            throw new SshAgentException($"Could not connect to the agent socket: {ex.SocketErrorCode}", ex);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        _socket = socket;
        return new NetworkStream(socket, ownsSocket: false);
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _socket?.Dispose();
            _socket = null;
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// Talks to a gpg-agent "emulated socket" on Windows: a small file whose first line is a TCP
/// port on the loopback interface, followed by a 16-byte nonce that must be sent before any
/// protocol traffic.
/// </summary>
/// <remarks>
/// VERIFY: against a real Gpg4win installation. The layout is stable in practice but is an
/// implementation detail of GnuPG rather than a specified format.
/// </remarks>
public sealed class EmulatedSocketAgentTransport : StreamAgentTransport
{
    /// <summary>Length of the nonce that follows the port line.</summary>
    public const int NonceLength = 16;

    private readonly string _socketFilePath;
    private Socket? _socket;

    /// <summary>Creates the transport.</summary>
    /// <param name="socketFilePath">Path of the emulated socket file.</param>
    public EmulatedSocketAgentTransport(string socketFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketFilePath);
        _socketFilePath = socketFilePath;
    }

    /// <summary>Reads the port and nonce out of an emulated socket file.</summary>
    /// <param name="contents">Raw file bytes.</param>
    /// <param name="port">Loopback port to connect to.</param>
    /// <param name="nonce">Nonce to send first.</param>
    /// <returns><see langword="true"/> when the file was understood.</returns>
    public static bool TryReadDescriptor(ReadOnlySpan<byte> contents, out int port, out byte[] nonce)
    {
        port = 0;
        nonce = [];

        var newline = contents.IndexOf((byte)'\n');
        if (newline <= 0 || contents.Length < newline + 1 + NonceLength)
        {
            return false;
        }

        var portText = System.Text.Encoding.ASCII.GetString(contents[..newline]).Trim();
        if (!int.TryParse(portText, CultureInfo.InvariantCulture, out port) || port is <= 0 or > 65535)
        {
            return false;
        }

        nonce = contents.Slice(newline + 1, NonceLength).ToArray();
        return true;
    }

    /// <inheritdoc/>
    protected override async Task<Stream> ConnectAsync(CancellationToken cancellationToken)
    {
        byte[] contents;
        try
        {
            contents = await File.ReadAllBytesAsync(_socketFilePath, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new SshAgentException("Could not read the emulated socket file", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new SshAgentException("Not permitted to read the emulated socket file", ex);
        }

        if (!TryReadDescriptor(contents, out var port, out var nonce))
        {
            throw new SshAgentException("The emulated socket file did not contain a port and a nonce");
        }

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), cancellationToken)
                .ConfigureAwait(false);

            // The nonce proves we could read the file, which is what authenticates us.
            await socket.SendAsync(nonce, SocketFlags.None, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            throw new SshAgentException($"Could not connect to gpg-agent: {ex.SocketErrorCode}", ex);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        _socket = socket;
        return new NetworkStream(socket, ownsSocket: false);
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _socket?.Dispose();
            _socket = null;
        }

        base.Dispose(disposing);
    }
}

/// <summary>Creates the transports that need no platform-specific code.</summary>
public sealed class PortableAgentTransportFactory : ISshAgentTransportFactory
{
    /// <inheritdoc/>
    public bool CanHandle(AgentEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return endpoint.Transport is AgentTransportKind.UnixSocket or AgentTransportKind.EmulatedSocket;
    }

    /// <inheritdoc/>
    public ISshAgentTransport Create(AgentEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        return endpoint.Transport switch
        {
            AgentTransportKind.UnixSocket => new UnixSocketAgentTransport(endpoint.Endpoint),
            AgentTransportKind.EmulatedSocket => new EmulatedSocketAgentTransport(endpoint.Endpoint),
            _ => throw new SshAgentException($"Unsupported transport {endpoint.Transport}"),
        };
    }
}
