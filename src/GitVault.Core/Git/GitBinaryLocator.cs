using System.Text.RegularExpressions;
using GitVault.Core.Abstractions;

namespace GitVault.Core.Git;

/// <summary>A located <c>git</c> executable.</summary>
/// <param name="Path">Absolute path to the executable.</param>
/// <param name="Version">Version string reported by <c>git --version</c>.</param>
public sealed record GitBinaryInfo(string Path, string Version);

/// <summary>Finds the <c>git</c> executable to shell out to.</summary>
public interface IGitBinaryLocator
{
    /// <summary>
    /// Locates git, checking <c>PATH</c> first and then the platform's install hints. The result
    /// is cached for the lifetime of the instance.
    /// </summary>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>The located binary, or null when git is not installed.</returns>
    Task<GitBinaryInfo?> LocateAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Default locator: <c>PATH</c>, then the candidates supplied by <see cref="IGitInstallHints"/>.
/// A candidate is accepted only once <c>git --version</c> answers.
/// </summary>
public sealed partial class GitBinaryLocator : IGitBinaryLocator
{
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(5);

    private readonly IProcessRunner _runner;
    private readonly IGitInstallHints _hints;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private GitBinaryInfo? _cached;
    private bool _searched;

    /// <summary>Creates the locator.</summary>
    /// <param name="runner">Process runner used to verify a candidate.</param>
    /// <param name="hints">Platform-specific candidate paths.</param>
    public GitBinaryLocator(IProcessRunner runner, IGitInstallHints hints)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(hints);
        _runner = runner;
        _hints = hints;
    }

    /// <inheritdoc/>
    public async Task<GitBinaryInfo?> LocateAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_searched)
            {
                return _cached;
            }

            foreach (var candidate in EnumerateCandidates())
            {
                var version = await TryVersionAsync(candidate, cancellationToken).ConfigureAwait(false);
                if (version is not null)
                {
                    _cached = new GitBinaryInfo(candidate, version);
                    break;
                }
            }

            _searched = true;
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Expands the search order: bare name first, then absolute hints that exist.</summary>
    /// <returns>Candidate executable paths, most likely first.</returns>
    private IEnumerable<string> EnumerateCandidates()
    {
        // A bare name lets the OS resolve it through PATH, which is what the user's shell does.
        yield return _hints.GitExecutableName;

        foreach (var candidate in _hints.CandidateGitPaths)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            bool exists;
            try
            {
                exists = File.Exists(candidate);
            }
            catch (IOException)
            {
                exists = false;
            }
            catch (UnauthorizedAccessException)
            {
                exists = false;
            }

            if (exists)
            {
                yield return candidate;
            }
        }
    }

    private async Task<string?> TryVersionAsync(string executable, CancellationToken cancellationToken)
    {
        var result = await _runner
            .RunAsync(executable, ["--version"], null, VersionTimeout, cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            return null;
        }

        var match = VersionRegex().Match(result.StandardOutput);
        return match.Success ? match.Groups[1].Value : result.StandardOutput.Trim();
    }

    [GeneratedRegex(@"git version\s+(\S+)", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex VersionRegex();
}
