using GitVault.Core.Models;

namespace GitVault.Core.Ssh.Agent;

/// <summary>Shells GitVault can emit an agent-environment snippet for.</summary>
public enum ShellKind
{
    /// <summary>POSIX <c>sh</c> / <c>bash</c>.</summary>
    Bash = 0,

    /// <summary><c>zsh</c>.</summary>
    Zsh,

    /// <summary><c>fish</c>.</summary>
    Fish,

    /// <summary>PowerShell.</summary>
    PowerShell,

    /// <summary>Windows <c>cmd.exe</c>.</summary>
    Cmd,
}

/// <summary>
/// Produces the line a user pastes into a shell so that <c>ssh</c> talks to the agent GitVault
/// found. Purely textual: GitVault never edits a shell profile on its own.
/// </summary>
public static class AgentShellSnippets
{
    /// <summary>Builds the export snippet for an agent.</summary>
    /// <param name="agent">Agent to point the shell at.</param>
    /// <param name="shell">Shell syntax to emit.</param>
    /// <param name="agentProcessId">Optional <c>SSH_AGENT_PID</c> value.</param>
    /// <returns>The snippet, without a trailing newline.</returns>
    public static string Build(SshAgentInfo agent, ShellKind shell, int? agentProcessId = null)
    {
        ArgumentNullException.ThrowIfNull(agent);

        // A named pipe is not something a shell variable can point at; Win32 OpenSSH finds its
        // own agent, so the honest answer is a comment rather than a broken export.
        if (agent.Kind is AgentKind.OpenSshWindowsPipe or AgentKind.Pageant)
        {
            return Comment(shell, agent.Endpoint);
        }

        var lines = new List<string>();
        var path = agent.Endpoint;

        switch (shell)
        {
            case ShellKind.Fish:
                lines.Add($"set -gx SSH_AUTH_SOCK {Quote(path, shell)}");
                if (agentProcessId is { } fishPid)
                {
                    lines.Add($"set -gx SSH_AGENT_PID {fishPid}");
                }

                break;

            case ShellKind.PowerShell:
                lines.Add($"$env:SSH_AUTH_SOCK = {Quote(path, shell)}");
                if (agentProcessId is { } psPid)
                {
                    lines.Add($"$env:SSH_AGENT_PID = '{psPid}'");
                }

                break;

            case ShellKind.Cmd:
                lines.Add($"set SSH_AUTH_SOCK={path}");
                if (agentProcessId is { } cmdPid)
                {
                    lines.Add($"set SSH_AGENT_PID={cmdPid}");
                }

                break;

            default:
                lines.Add($"export SSH_AUTH_SOCK={Quote(path, shell)}");
                if (agentProcessId is { } shPid)
                {
                    lines.Add($"export SSH_AGENT_PID={shPid}");
                }

                break;
        }

        return string.Join('\n', lines);
    }

    private static string Comment(ShellKind shell, string endpoint) => shell switch
    {
        ShellKind.Cmd => $"REM This agent is reached through {endpoint}, not through an environment variable.",
        _ => $"# This agent is reached through {endpoint}, not through an environment variable.",
    };

    private static string Quote(string value, ShellKind shell) => shell switch
    {
        // Single quotes are literal in every POSIX shell and in PowerShell; an embedded quote is
        // the only case needing care.
        ShellKind.PowerShell => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'",
        ShellKind.Cmd => value,
        _ => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'",
    };
}
