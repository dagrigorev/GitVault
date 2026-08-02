using GitVault.Core.Abstractions;
using GitVault.Core.Models;

namespace GitVault.Core.Ssh.Agent;

/// <summary>
/// Endpoint discovery shared by macOS and Linux: <c>SSH_AUTH_SOCK</c> first, then the
/// conventional socket locations for the agents people actually run.
/// </summary>
public abstract class PosixAgentEndpointProvider : IAgentEndpointProvider
{
    private readonly IPlatformPaths _paths;

    /// <summary>Creates the provider.</summary>
    /// <param name="paths">Platform paths, for home-relative socket locations.</param>
    protected PosixAgentEndpointProvider(IPlatformPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    /// <summary>Platform paths, for use by derived providers.</summary>
    protected IPlatformPaths Paths => _paths;

    /// <inheritdoc/>
    public IReadOnlyList<AgentEndpoint> GetEndpoints()
    {
        var endpoints = new List<AgentEndpoint>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(AgentKind kind, string path, bool supportsWrites = true)
        {
            if (!string.IsNullOrWhiteSpace(path) && seen.Add(path))
            {
                endpoints.Add(new AgentEndpoint(kind, path, AgentTransportKind.UnixSocket, supportsWrites));
            }
        }

        // Whatever the user's shell already points at wins: it is what ssh itself would use.
        var authSock = Environment.GetEnvironmentVariable("SSH_AUTH_SOCK");
        if (!string.IsNullOrWhiteSpace(authSock))
        {
            Add(ClassifyAuthSock(authSock), authSock);
        }

        // 1Password refuses key additions by design, so it is marked read-only up front.
        foreach (var candidate in OnePasswordSocketCandidates())
        {
            Add(AgentKind.OnePassword, candidate, supportsWrites: false);
        }

        foreach (var candidate in GpgAgentSocketCandidates())
        {
            Add(AgentKind.GpgAgent, candidate);
        }

        foreach (var candidate in PlatformSpecificCandidates())
        {
            Add(candidate.Kind, candidate.Endpoint, candidate.SupportsWrites);
        }

        foreach (var candidate in EnumerateTemporaryAgentSockets())
        {
            Add(AgentKind.OpenSshUnix, candidate);
        }

        return endpoints;
    }

    /// <summary>Extra endpoints specific to one operating system.</summary>
    /// <returns>Candidate endpoints.</returns>
    protected virtual IEnumerable<AgentEndpoint> PlatformSpecificCandidates() => [];

    /// <summary>Conventional 1Password agent socket locations.</summary>
    /// <returns>Candidate paths.</returns>
    protected virtual IEnumerable<string> OnePasswordSocketCandidates() =>
    [
        Path.Combine(_paths.HomeDirectory, ".1password", "agent.sock"),
    ];

    /// <summary>Conventional gpg-agent SSH socket locations.</summary>
    /// <returns>Candidate paths.</returns>
    protected virtual IEnumerable<string> GpgAgentSocketCandidates()
    {
        // gpgconf --list-dirs agent-ssh-socket is authoritative, but shelling out during
        // discovery costs a process launch per scan; these cover the default layouts.
        yield return Path.Combine(_paths.HomeDirectory, ".gnupg", "S.gpg-agent.ssh");

        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrWhiteSpace(runtimeDir))
        {
            yield return Path.Combine(runtimeDir, "gnupg", "S.gpg-agent.ssh");
        }
    }

    /// <summary>
    /// Sockets left in the temporary directory by <c>ssh-agent</c> when it is started manually.
    /// </summary>
    /// <returns>Candidate paths.</returns>
    protected static IEnumerable<string> EnumerateTemporaryAgentSockets()
    {
        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories("/tmp", "ssh-*");
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var directory in directories)
        {
            string[] sockets;
            try
            {
                sockets = Directory.GetFiles(directory, "agent.*");
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var socket in sockets)
            {
                yield return socket;
            }
        }
    }

    /// <summary>Guesses which agent a socket path belongs to from its shape.</summary>
    /// <param name="path">Socket path.</param>
    /// <returns>The most likely agent kind.</returns>
    internal static AgentKind ClassifyAuthSock(string path)
    {
        if (path.Contains("1password", StringComparison.OrdinalIgnoreCase))
        {
            return AgentKind.OnePassword;
        }

        if (path.Contains("gpg-agent", StringComparison.OrdinalIgnoreCase)
            || path.Contains("gnupg", StringComparison.OrdinalIgnoreCase))
        {
            return AgentKind.GpgAgent;
        }

        if (path.Contains("keeagent", StringComparison.OrdinalIgnoreCase))
        {
            return AgentKind.KeeAgent;
        }

        if (path.Contains("wsl", StringComparison.OrdinalIgnoreCase)
            || path.Contains("npiperelay", StringComparison.OrdinalIgnoreCase))
        {
            return AgentKind.WslRelay;
        }

        return AgentKind.OpenSshUnix;
    }
}
