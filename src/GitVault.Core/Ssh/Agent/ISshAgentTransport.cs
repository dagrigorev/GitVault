using GitVault.Core.Models;

namespace GitVault.Core.Ssh.Agent;

/// <summary>One place an agent can be reached.</summary>
/// <param name="Kind">What kind of agent is expected there.</param>
/// <param name="Endpoint">Socket path, pipe name, or window descriptor.</param>
/// <param name="Transport">Which transport can talk to it.</param>
/// <param name="SupportsWrites">
/// Whether the agent is expected to accept key additions. Some agents (1Password) deliberately
/// refuse; surfacing that up front is better than a failed operation.
/// </param>
public sealed record AgentEndpoint(
    AgentKind Kind,
    string Endpoint,
    AgentTransportKind Transport,
    bool SupportsWrites = true);

/// <summary>How to reach an agent endpoint.</summary>
public enum AgentTransportKind
{
    /// <summary>A unix domain socket.</summary>
    UnixSocket = 0,

    /// <summary>A Windows named pipe.</summary>
    NamedPipe,

    /// <summary>Pageant's window-message channel.</summary>
    PageantWindow,

    /// <summary>
    /// A gpg-agent "emulated socket": a file holding a TCP port and a nonce that must be sent
    /// before anything else.
    /// </summary>
    EmulatedSocket,
}

/// <summary>A duplex channel to an agent, framed by the caller.</summary>
public interface ISshAgentTransport : IDisposable
{
    /// <summary>Sends one framed request and reads one framed reply.</summary>
    /// <param name="request">Complete framed request.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The reply payload, with its length prefix removed.</returns>
    Task<byte[]> ExchangeAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken);
}

/// <summary>Creates transports for endpoints.</summary>
public interface ISshAgentTransportFactory
{
    /// <summary>True when this factory can reach the endpoint.</summary>
    /// <param name="endpoint">Endpoint to test.</param>
    /// <returns><see langword="true"/> when <see cref="Create"/> will work.</returns>
    bool CanHandle(AgentEndpoint endpoint);

    /// <summary>Creates a transport. The caller owns and disposes it.</summary>
    /// <param name="endpoint">Endpoint to connect to.</param>
    /// <returns>A connected-on-demand transport.</returns>
    ISshAgentTransport Create(AgentEndpoint endpoint);
}

/// <summary>Lists the agent endpoints that plausibly exist on this machine.</summary>
public interface IAgentEndpointProvider
{
    /// <summary>Enumerates candidate endpoints, most likely first.</summary>
    /// <returns>Endpoints to try. Existence is not guaranteed; probing decides.</returns>
    IReadOnlyList<AgentEndpoint> GetEndpoints();
}

/// <summary>Raised when an agent could not be reached or spoke nonsense.</summary>
public sealed class SshAgentException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What went wrong.</param>
    public SshAgentException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public SshAgentException()
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">Underlying failure.</param>
    public SshAgentException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
