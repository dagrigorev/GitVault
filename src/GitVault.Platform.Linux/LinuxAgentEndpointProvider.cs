using System.Runtime.Versioning;
using GitVault.Core.Abstractions;
using GitVault.Core.Models;
using GitVault.Core.Ssh.Agent;

namespace GitVault.Platform.Linux;

/// <summary>
/// Linux agent endpoints. Adds the desktop-session sockets that GNOME Keyring and KDE create,
/// plus the WSL relay socket, to the shared POSIX set.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxAgentEndpointProvider : PosixAgentEndpointProvider
{
    /// <summary>Creates the provider.</summary>
    /// <param name="paths">Platform paths.</param>
    public LinuxAgentEndpointProvider(IPlatformPaths paths)
        : base(paths)
    {
    }

    /// <inheritdoc/>
    protected override IEnumerable<AgentEndpoint> PlatformSpecificCandidates()
    {
        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrWhiteSpace(runtimeDir))
        {
            // GNOME Keyring's SSH agent, when the distribution still enables it.
            yield return new AgentEndpoint(
                AgentKind.OpenSshUnix,
                Path.Combine(runtimeDir, "keyring", "ssh"),
                AgentTransportKind.UnixSocket);

            yield return new AgentEndpoint(
                AgentKind.OpenSshUnix,
                Path.Combine(runtimeDir, "ssh-agent.socket"),
                AgentTransportKind.UnixSocket);
        }

        // A relay forwarding to Pageant or to the Windows OpenSSH agent from inside WSL.
        yield return new AgentEndpoint(
            AgentKind.WslRelay,
            Path.Combine(Paths.HomeDirectory, ".ssh", "agent.sock"),
            AgentTransportKind.UnixSocket);
    }
}
