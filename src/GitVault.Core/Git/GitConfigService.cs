using GitVault.Core.Abstractions;
using GitVault.Core.Models;

namespace GitVault.Core.Git;

/// <summary>
/// Reads and writes git configuration. Shells out to the <c>git</c> binary when one is present,
/// because git is the authority on include resolution, conditional includes and path quirks;
/// falls back to <see cref="GitConfigParser"/> and <see cref="GitConfigWriter"/> when it is not.
/// </summary>
public sealed class GitConfigService : IGitConfigService
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);

    private readonly IProcessRunner _runner;
    private readonly IGitBinaryLocator _locator;
    private readonly IPlatformPaths _paths;
    private readonly GitConfigParser _parser;
    private readonly GitConfigWriter _writer;
    private GitBinaryInfo? _binary;

    /// <summary>Creates the service.</summary>
    /// <param name="runner">Process runner.</param>
    /// <param name="locator">Locator for the git executable.</param>
    /// <param name="paths">Platform paths, used by the fallback reader and writer.</param>
    public GitConfigService(
        IProcessRunner runner,
        IGitBinaryLocator locator,
        IPlatformPaths paths)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(paths);

        _runner = runner;
        _locator = locator;
        _paths = paths;
        _parser = new GitConfigParser(paths);
        _writer = new GitConfigWriter();
    }

    /// <inheritdoc/>
    public bool HasGitBinary => _binary is not null;

    /// <inheritdoc/>
    public string? GitBinaryPath => _binary?.Path;

    /// <inheritdoc/>
    public string? GitVersion => _binary?.Version;

    /// <summary>Locates the git binary once, so that <see cref="HasGitBinary"/> is meaningful.</summary>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>A task that completes when the search has finished.</returns>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _binary ??= await _locator.LocateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<GitConfigValue>> ListAsync(
        string? repositoryPath,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        return _binary is not null
            ? await ListWithGitAsync(_binary, repositoryPath, cancellationToken).ConfigureAwait(false)
            : ListWithParser(repositoryPath);
    }

    /// <inheritdoc/>
    public async Task<GitConfigValue?> GetEffectiveAsync(
        string key,
        string? repositoryPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var all = await ListAsync(repositoryPath, cancellationToken).ConfigureAwait(false);

        // Entries arrive lowest precedence first, so the last match wins — which is also how
        // git resolves a multi-valued key when a single value is requested.
        GitConfigValue? winner = null;
        foreach (var entry in all)
        {
            if (string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                winner = entry;
            }
        }

        return winner;
    }

    /// <inheritdoc/>
    public async Task SetAsync(
        string key,
        string value,
        GitConfigScope scope,
        string? repositoryPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        if (_binary is not null)
        {
            var arguments = BuildScopeArguments(scope, repositoryPath);
            arguments.AddRange(["--replace-all", key, value]);

            var result = await _runner
                .RunAsync(_binary.Path, arguments, WorkingDirectoryFor(scope, repositoryPath), CommandTimeout, cancellationToken)
                .ConfigureAwait(false);

            if (result.IsSuccess)
            {
                return;
            }

            throw new GitConfigException(
                $"git config failed with exit code {result.ExitCode}", result.StandardError.Trim());
        }

        var file = ResolveConfigFilePath(scope, repositoryPath)
            ?? throw new GitConfigException("No configuration file for the requested scope");

        var (section, subsection, name) = SplitKey(key);
        _writer.Set(file, section, subsection, name, value);
    }

    /// <inheritdoc/>
    public async Task UnsetAsync(
        string key,
        GitConfigScope scope,
        string? repositoryPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        if (_binary is not null)
        {
            var arguments = BuildScopeArguments(scope, repositoryPath);
            arguments.AddRange(["--unset-all", key]);

            var result = await _runner
                .RunAsync(_binary.Path, arguments, WorkingDirectoryFor(scope, repositoryPath), CommandTimeout, cancellationToken)
                .ConfigureAwait(false);

            // Exit code 5 means "the key was not set", which is the state the caller wanted.
            if (result.IsSuccess || result.ExitCode == 5)
            {
                return;
            }

            throw new GitConfigException(
                $"git config failed with exit code {result.ExitCode}", result.StandardError.Trim());
        }

        var file = ResolveConfigFilePath(scope, repositoryPath);
        if (file is null)
        {
            return;
        }

        var (section, subsection, name) = SplitKey(key);
        _writer.Unset(file, section, subsection, name);
    }

    /// <inheritdoc/>
    public string? ResolveConfigFilePath(GitConfigScope scope, string? repositoryPath) => scope switch
    {
        GitConfigScope.Global => _paths.GlobalGitConfigPath,
        GitConfigScope.System => _paths.SystemGitConfigCandidates.FirstOrDefault(File.Exists)
                                 ?? _paths.SystemGitConfigCandidates.FirstOrDefault(),
        GitConfigScope.Local => repositoryPath is null ? null : Path.Combine(GitDirectoryOf(repositoryPath), "config"),
        GitConfigScope.Worktree => repositoryPath is null
            ? null
            : Path.Combine(GitDirectoryOf(repositoryPath), "config.worktree"),
        _ => null,
    };

    /// <summary>Splits a fully qualified key into section, subsection and name.</summary>
    /// <param name="key">Key such as <c>credential.https://github.com.helper</c>.</param>
    /// <returns>The three parts. The subsection is null when the key has only two segments.</returns>
    internal static (string Section, string? Subsection, string Name) SplitKey(string key)
    {
        var firstDot = key.IndexOf('.', StringComparison.Ordinal);
        var lastDot = key.LastIndexOf('.');

        if (firstDot < 0)
        {
            return (key.ToLowerInvariant(), null, string.Empty);
        }

        var section = key[..firstDot].ToLowerInvariant();
        var name = key[(lastDot + 1)..].ToLowerInvariant();

        // A subsection may itself contain dots, so everything between the first and last dot
        // belongs to it. Two-segment keys have no subsection at all.
        var subsection = lastDot > firstDot ? key[(firstDot + 1)..lastDot] : null;

        return (section, subsection, name);
    }

    /// <summary>Parses the NUL-delimited output of <c>git config --list -z</c>.</summary>
    /// <param name="output">Raw stdout.</param>
    /// <returns>Entries in the order git reported them.</returns>
    internal static IReadOnlyList<GitConfigValue> ParseNullDelimitedList(string output)
    {
        var values = new List<GitConfigValue>();
        if (string.IsNullOrEmpty(output))
        {
            return values;
        }

        var fields = output.Split('\0', StringSplitOptions.None);

        // Each record is: scope NUL origin NUL key LF value.
        for (var i = 0; i + 2 < fields.Length; i += 3)
        {
            var scope = ParseScope(fields[i]);
            var origin = fields[i + 1];
            var pair = fields[i + 2];
            if (pair.Length == 0)
            {
                continue;
            }

            var newline = pair.IndexOf('\n', StringComparison.Ordinal);
            var key = newline < 0 ? pair : pair[..newline];
            var value = newline < 0 ? string.Empty : pair[(newline + 1)..];

            values.Add(new GitConfigValue(key.TrimEnd('\r'), value, scope, origin));
        }

        return values;
    }

    private static GitConfigScope ParseScope(string text) => text.Trim().ToLowerInvariant() switch
    {
        "system" => GitConfigScope.System,
        "global" => GitConfigScope.Global,
        "local" => GitConfigScope.Local,
        "worktree" => GitConfigScope.Worktree,
        "command" => GitConfigScope.Command,
        _ => GitConfigScope.Unknown,
    };

    private static string GitDirectoryOf(string repositoryPath) =>
        Path.GetFileName(repositoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) == ".git"
            ? repositoryPath
            : Path.Combine(repositoryPath, ".git");

    private static List<string> BuildScopeArguments(GitConfigScope scope, string? repositoryPath)
    {
        var arguments = new List<string>();

        if (scope is GitConfigScope.Local or GitConfigScope.Worktree && repositoryPath is not null)
        {
            arguments.AddRange(["-C", repositoryPath]);
        }

        arguments.Add("config");
        arguments.Add(scope switch
        {
            GitConfigScope.System => "--system",
            GitConfigScope.Global => "--global",
            GitConfigScope.Local => "--local",
            GitConfigScope.Worktree => "--worktree",
            _ => "--global",
        });

        return arguments;
    }

    private string WorkingDirectoryFor(GitConfigScope scope, string? repositoryPath) =>
        scope is GitConfigScope.Local or GitConfigScope.Worktree && repositoryPath is not null
            ? repositoryPath
            : _paths.HomeDirectory;

    private async Task<IReadOnlyList<GitConfigValue>> ListWithGitAsync(
        GitBinaryInfo binary,
        string? repositoryPath,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>();
        if (repositoryPath is not null)
        {
            arguments.AddRange(["-C", repositoryPath]);
        }

        arguments.AddRange(["config", "--list", "--show-origin", "--show-scope", "-z"]);

        var workingDirectory = repositoryPath ?? _paths.HomeDirectory;
        var result = await _runner
            .RunAsync(binary.Path, arguments, workingDirectory, CommandTimeout, cancellationToken)
            .ConfigureAwait(false);

        // Outside a repository git still reports system and global values, so a non-zero exit
        // here means something worse than "not a repository"; fall back rather than fail.
        return result.IsSuccess
            ? ParseNullDelimitedList(result.StandardOutput)
            : ListWithParser(repositoryPath);
    }

    private IReadOnlyList<GitConfigValue> ListWithParser(string? repositoryPath)
    {
        var context = GitConfigIncludeContext.ForRepository(repositoryPath);
        var entries = new List<GitConfigEntry>();

        foreach (var candidate in _paths.SystemGitConfigCandidates)
        {
            if (File.Exists(candidate))
            {
                entries.AddRange(_parser.ParseFile(candidate, GitConfigScope.System, context));
                break;
            }
        }

        if (File.Exists(_paths.GlobalGitConfigPath))
        {
            entries.AddRange(_parser.ParseFile(_paths.GlobalGitConfigPath, GitConfigScope.Global, context));
        }

        var local = ResolveConfigFilePath(GitConfigScope.Local, repositoryPath);
        if (local is not null && File.Exists(local))
        {
            entries.AddRange(_parser.ParseFile(local, GitConfigScope.Local, context));
        }

        var worktree = ResolveConfigFilePath(GitConfigScope.Worktree, repositoryPath);
        if (worktree is not null && File.Exists(worktree))
        {
            entries.AddRange(_parser.ParseFile(worktree, GitConfigScope.Worktree, context));
        }

        return [.. entries.Select(e => new GitConfigValue(e.Key, e.Value, e.Scope, "file:" + e.FilePath))];
    }
}

/// <summary>Raised when a configuration write could not be completed.</summary>
public sealed class GitConfigException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What failed.</param>
    /// <param name="detail">Redacted detail from git, when available.</param>
    public GitConfigException(string message, string? detail)
        : base(message) => Detail = detail;

    /// <summary>Creates the exception with no detail.</summary>
    public GitConfigException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What failed.</param>
    public GitConfigException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    /// <param name="message">What failed.</param>
    /// <param name="innerException">Underlying failure.</param>
    public GitConfigException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Redacted detail reported by git, when available.</summary>
    public string? Detail { get; }
}
