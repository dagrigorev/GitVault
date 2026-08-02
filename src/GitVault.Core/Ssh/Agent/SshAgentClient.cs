using GitVault.Core.Abstractions;
using GitVault.Core.Models;

namespace GitVault.Core.Ssh.Agent;

/// <summary>
/// Speaks the SSH agent protocol over any transport. One instance is bound to one endpoint and
/// opens the transport lazily, so constructing it is free and never blocks a scan.
/// </summary>
public sealed class SshAgentClient : ISshAgent, IDisposable
{
    private readonly AgentEndpoint _endpoint;
    private readonly ISshAgentTransportFactory _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    /// <summary>Creates a client for an endpoint.</summary>
    /// <param name="endpoint">Endpoint to talk to.</param>
    /// <param name="factory">Factory that can build a transport for it.</param>
    public SshAgentClient(AgentEndpoint endpoint, ISshAgentTransportFactory factory)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(factory);

        _endpoint = endpoint;
        _factory = factory;
    }

    /// <inheritdoc/>
    public SshAgentInfo Descriptor { get; private set; } =
        new(AgentKind.Unknown, string.Empty, IsRunning: false, SupportsAdd: false, SupportsConstraints: false);

    /// <summary>The endpoint this client talks to.</summary>
    public AgentEndpoint Endpoint => _endpoint;

    /// <summary>
    /// Contacts the agent once and builds its descriptor. A refusal or an absent endpoint is
    /// reported as a stopped agent, not as an exception.
    /// </summary>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns>What the endpoint turned out to be.</returns>
    public async Task<SshAgentInfo> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var identities = await ListIdentitiesAsync(cancellationToken).ConfigureAwait(false);

            Descriptor = new SshAgentInfo(
                _endpoint.Kind,
                _endpoint.Endpoint,
                IsRunning: true,
                SupportsAdd: _endpoint.SupportsWrites,
                SupportsConstraints: _endpoint.SupportsWrites)
            {
                LoadedKeys = identities,
            };
        }
        catch (SshAgentException ex)
        {
            Descriptor = new SshAgentInfo(
                _endpoint.Kind, _endpoint.Endpoint, IsRunning: false, SupportsAdd: false, SupportsConstraints: false)
            {
                StatusDetail = ex.Message,
            };
        }
        catch (SshWireException ex)
        {
            Descriptor = new SshAgentInfo(
                _endpoint.Kind, _endpoint.Endpoint, IsRunning: false, SupportsAdd: false, SupportsConstraints: false)
            {
                StatusDetail = ex.Message,
            };
        }

        return Descriptor;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AgentKeyEntry>> ListIdentitiesAsync(CancellationToken cancellationToken)
    {
        var reply = await ExchangeAsync(
            AgentProtocol.FrameSimple(AgentProtocol.RequestIdentities), cancellationToken).ConfigureAwait(false);

        // A locked agent answers with an empty list rather than an error, which is exactly what
        // the caller should see: it is reachable, it just holds nothing usable right now.
        return AgentProtocol.ParseIdentitiesAnswer(reply);
    }

    /// <inheritdoc/>
    public async Task<bool> AddIdentityAsync(
        ReadOnlyMemory<byte> privateKeyBlob,
        string comment,
        int? lifetimeSeconds,
        bool requireConfirmation,
        CancellationToken cancellationToken)
    {
        if (!_endpoint.SupportsWrites)
        {
            return false;
        }

        var request = AgentProtocol.BuildAddIdentity(
            privateKeyBlob.Span, comment, lifetimeSeconds, requireConfirmation);

        return AgentProtocol.IsSuccess(await ExchangeAsync(request, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc/>
    public async Task<bool> RemoveIdentityAsync(ReadOnlyMemory<byte> publicKeyBlob, CancellationToken cancellationToken)
    {
        if (!_endpoint.SupportsWrites)
        {
            return false;
        }

        var request = AgentProtocol.BuildRemoveIdentity(publicKeyBlob.Span);
        return AgentProtocol.IsSuccess(await ExchangeAsync(request, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc/>
    public async Task<bool> RemoveAllIdentitiesAsync(CancellationToken cancellationToken)
    {
        if (!_endpoint.SupportsWrites)
        {
            return false;
        }

        var request = AgentProtocol.FrameSimple(AgentProtocol.RemoveAllIdentities);
        return AgentProtocol.IsSuccess(await ExchangeAsync(request, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc/>
    public async Task<bool> SetLockedAsync(
        ReadOnlyMemory<byte> passphrase,
        bool @lock,
        CancellationToken cancellationToken)
    {
        var request = AgentProtocol.BuildLock(passphrase.Span, @lock);
        return AgentProtocol.IsSuccess(await ExchangeAsync(request, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _gate.Dispose();
        _disposed = true;
    }

    /// <summary>
    /// Performs one request/reply exchange on a fresh transport.
    /// </summary>
    /// <remarks>
    /// A new connection per exchange costs a socket but keeps the client stateless, which matters
    /// because agents drop idle connections and because several pages may query the same agent.
    /// </remarks>
    private async Task<byte[]> ExchangeAsync(byte[] request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var transport = _factory.Create(_endpoint);
            return await transport.ExchangeAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new SshAgentException("The agent connection failed", ex);
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            throw new SshAgentException($"Could not reach the agent: {ex.SocketErrorCode}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new SshAgentException("Not permitted to talk to the agent", ex);
        }
        catch (TimeoutException ex)
        {
            throw new SshAgentException("The agent did not answer in time", ex);
        }
        finally
        {
            _gate.Release();
        }
    }
}
