using System.Runtime.Versioning;
using GitVault.Core.Abstractions;
using GitVault.Core.Models;
using GitVault.Core.Ssh.Agent;

namespace GitVault.Platform.MacOS;

/// <summary>
/// macOS agent endpoints. Adds the launchd-managed socket and the 1Password location inside the
/// app's group container to the shared POSIX set.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacAgentEndpointProvider : PosixAgentEndpointProvider
{
    /// <summary>Creates the provider.</summary>
    /// <param name="paths">Platform paths.</param>
    public MacAgentEndpointProvider(IPlatformPaths paths)
        : base(paths)
    {
    }

    /// <inheritdoc/>
    protected override IEnumerable<string> OnePasswordSocketCandidates()
    {
        yield return Path.Combine(
            Paths.HomeDirectory,
            "Library",
            "Group Containers",
            "2BUA8C4S2C.com.1password",
            "t",
            "agent.sock");

        foreach (var candidate in base.OnePasswordSocketCandidates())
        {
            yield return candidate;
        }
    }

    /// <inheritdoc/>
    protected override IEnumerable<AgentEndpoint> PlatformSpecificCandidates()
    {
        // launchd hands the per-session socket path to processes through this variable; it is
        // the one the system ssh-agent actually listens on.
        var launchd = Environment.GetEnvironmentVariable("SSH_AUTH_SOCK");
        if (!string.IsNullOrWhiteSpace(launchd) && launchd.StartsWith("/private/tmp/", StringComparison.Ordinal))
        {
            yield return new AgentEndpoint(AgentKind.OpenSshUnix, launchd, AgentTransportKind.UnixSocket);
        }

        // VERIFY: against a real macOS session. The launchd socket lives under a per-boot
        // directory whose name is not predictable, so the environment variable is the only
        // reliable route; this entry exists to document that.
    }
}
