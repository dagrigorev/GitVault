using System.Globalization;
using GitVault.Core.Abstractions;
using GitVault.Core.Models;

namespace GitVault.Core.Ssh.Agent;

/// <summary>Outcome of asking an agent to take a key.</summary>
/// <param name="Succeeded">Whether the key was loaded.</param>
/// <param name="Diagnostics">Redacted explanation when it was not.</param>
/// <param name="NeedsPassphrasePrompt">
/// True when the key is protected and the tool would have to prompt interactively, which a
/// windowed application cannot satisfy on its own.
/// </param>
public sealed record AgentLoadResult(bool Succeeded, string? Diagnostics, bool NeedsPassphrasePrompt = false)
{
    /// <summary>Builds a failure result.</summary>
    /// <param name="diagnostics">Redacted explanation.</param>
    /// <returns>The result.</returns>
    public static AgentLoadResult Failed(string diagnostics) => new(false, diagnostics);
}

/// <summary>Loads keys into an agent.</summary>
public interface IAgentKeyLoader
{
    /// <summary>True when an <c>ssh-add</c> executable was located.</summary>
    bool HasSshAdd { get; }

    /// <summary>Locates <c>ssh-add</c> once.</summary>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>A task that completes when the search has finished.</returns>
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>Asks the agent to load a key from disk.</summary>
    /// <param name="agent">Agent that should take the key.</param>
    /// <param name="privateKeyPath">Key to load.</param>
    /// <param name="lifetimeSeconds">Optional lifetime constraint.</param>
    /// <param name="requireConfirmation">Whether each use must be confirmed.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>What happened.</returns>
    Task<AgentLoadResult> AddAsync(
        SshAgentInfo agent,
        string privateKeyPath,
        int? lifetimeSeconds,
        bool requireConfirmation,
        CancellationToken cancellationToken);
}

/// <summary>
/// Loads keys into an agent by running <c>ssh-add</c>.
/// </summary>
/// <remarks>
/// GitVault could send <c>SSH_AGENTC_ADD_IDENTITY</c> itself, but doing so would mean reading the
/// private key into this process, decrypting it, and handing it to the agent. Delegating to
/// <c>ssh-add</c> keeps private key material out of GitVault entirely: the bytes go from the file
/// to <c>ssh-add</c> to the agent, and GitVault only ever sees the exit code.
///
/// The cost is that a passphrase-protected key needs an interactive prompt that a windowed
/// process cannot provide, which is reported rather than worked around.
/// </remarks>
public sealed class AgentKeyLoader : IAgentKeyLoader
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly IProcessRunner _runner;
    private readonly ISshToolLocator _locator;
    private string? _sshAddPath;

    /// <summary>Creates the loader.</summary>
    /// <param name="runner">Process runner.</param>
    /// <param name="locator">Locator for the OpenSSH tools.</param>
    public AgentKeyLoader(IProcessRunner runner, ISshToolLocator locator)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(locator);

        _runner = runner;
        _locator = locator;
    }

    /// <inheritdoc/>
    public bool HasSshAdd => _sshAddPath is not null;

    /// <inheritdoc/>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _sshAddPath ??= await _locator.LocateSshAddAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<AgentLoadResult> AddAsync(
        SshAgentInfo agent,
        string privateKeyPath,
        int? lifetimeSeconds,
        bool requireConfirmation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyPath);

        if (!agent.SupportsAdd)
        {
            return AgentLoadResult.Failed("this agent does not accept new keys");
        }

        if (!File.Exists(privateKeyPath))
        {
            return AgentLoadResult.Failed("the key file no longer exists");
        }

        // A protected key makes ssh-add prompt, and there is no console to prompt on.
        if (SshKeyReader.TryReadPrivateKeyFile(privateKeyPath, out var info) && info is { IsEncrypted: true })
        {
            return new AgentLoadResult(false, null, NeedsPassphrasePrompt: true);
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (_sshAddPath is null)
        {
            return AgentLoadResult.Failed("ssh-add was not found");
        }

        var arguments = new List<string>();

        if (lifetimeSeconds is > 0)
        {
            arguments.AddRange(["-t", lifetimeSeconds.Value.ToString(CultureInfo.InvariantCulture)]);
        }

        if (requireConfirmation)
        {
            arguments.Add("-c");
        }

        arguments.Add(privateKeyPath);

        var result = await _runner
            .RunAsync(_sshAddPath, arguments, null, Timeout, cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? new AgentLoadResult(true, null)
            : AgentLoadResult.Failed(result.StandardError.Trim());
    }
}
