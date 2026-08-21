using GitVault.Core.Abstractions;

namespace GitVault.Core.Repository;

/// <summary>Runs git inside a repository.</summary>
public interface IGitCommandRunner
{
    /// <summary>True when a git binary was located.</summary>
    bool IsAvailable { get; }

    /// <summary>Runs git in a repository and returns what it produced.</summary>
    /// <param name="repositoryPath">Working tree to run in.</param>
    /// <param name="arguments">Arguments after <c>git</c>, already split.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The process result.</returns>
    Task<ProcessResult> RunAsync(
        string repositoryPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);

    /// <summary>Runs git and returns its trimmed standard output, or null when it failed.</summary>
    /// <param name="repositoryPath">Working tree to run in.</param>
    /// <param name="arguments">Arguments after <c>git</c>, already split.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>Standard output, or null.</returns>
    Task<string?> ReadAsync(
        string repositoryPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);
}

/// <summary>
/// The one place git is invoked for repository work.
/// </summary>
/// <remarks>
/// Every call is given the same pinned environment as the configuration service, for the same
/// reason: what git reads and writes has to be something GitVault decided rather than something
/// it inferred from the surrounding process.
///
/// Two habits are enforced here rather than left to callers. Arguments are always passed as a
/// list, never a command string, so nothing is ever handed to a shell to re-parse. And a
/// terminating <c>--</c> is the caller's job where a ref name could be mistaken for an option —
/// this class refuses arguments beginning with a dash in the positions where that matters, which
/// is checked in <c>GitCommandRunnerTests</c>.
/// </remarks>
public sealed class GitCommandRunner : IGitCommandRunner
{
    /// <summary>Time budget for a single git invocation.</summary>
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    private readonly IProcessRunner _runner;
    private readonly IGitConfigService _config;
    private readonly IPlatformPaths _paths;

    /// <summary>Creates the runner.</summary>
    /// <param name="runner">Process runner.</param>
    /// <param name="config">Configuration service, which owns the located binary.</param>
    /// <param name="paths">Platform paths, for the pinned configuration file.</param>
    public GitCommandRunner(IProcessRunner runner, IGitConfigService config, IPlatformPaths paths)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(paths);

        _runner = runner;
        _config = config;
        _paths = paths;
    }

    /// <inheritdoc/>
    public bool IsAvailable => _config.HasGitBinary;

    /// <inheritdoc/>
    public async Task<ProcessResult> RunAsync(
        string repositoryPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(arguments);

        if (_config.GitBinaryPath is not { Length: > 0 } binary)
        {
            return ProcessResult.LaunchFailed("git is not available");
        }

        var full = new List<string> { "-C", repositoryPath };
        full.AddRange(arguments);

        return await _runner
            .RunAsync(binary, full, Environment(), repositoryPath, CommandTimeout, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<string?> ReadAsync(
        string repositoryPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(repositoryPath, arguments, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? result.StandardOutput.TrimEnd('\n', '\r') : null;
    }

    /// <summary>
    /// The environment every invocation is given.
    /// </summary>
    /// <remarks>
    /// <c>GIT_CONFIG_GLOBAL</c> is pinned so that reading a branch's upstream and writing it later
    /// agree about which per-user file is in play. <c>GIT_TERMINAL_PROMPT</c> is switched off so a
    /// command that would otherwise wait for credentials fails immediately instead of hanging a
    /// UI thread on an invisible prompt — GitVault makes no network calls, but a misconfigured
    /// remote helper can still try.
    /// </remarks>
    private IReadOnlyDictionary<string, string?> Environment() =>
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["GIT_CONFIG_GLOBAL"] = _paths.GlobalGitConfigPath,
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_OPTIONAL_LOCKS"] = "0",
        };
}
