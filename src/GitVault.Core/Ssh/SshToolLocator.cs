using GitVault.Core.Abstractions;

namespace GitVault.Core.Ssh;

/// <summary>Locates the OpenSSH command line tools.</summary>
public interface ISshToolLocator
{
    /// <summary>Finds <c>ssh-keygen</c>.</summary>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>An executable path, or null when it is not installed.</returns>
    Task<string?> LocateSshKeygenAsync(CancellationToken cancellationToken);

    /// <summary>Finds <c>ssh-add</c>.</summary>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>An executable path, or null when it is not installed.</returns>
    Task<string?> LocateSshAddAsync(CancellationToken cancellationToken);
}

/// <summary>Platform-specific places the OpenSSH tools are installed.</summary>
public interface ISshToolHints
{
    /// <summary>Candidate absolute paths for <c>ssh-keygen</c>, most likely first.</summary>
    IReadOnlyList<string> SshKeygenCandidates { get; }

    /// <summary>Candidate absolute paths for <c>ssh-add</c>, most likely first.</summary>
    IReadOnlyList<string> SshAddCandidates { get; }
}

/// <summary>
/// Probes <c>PATH</c> and then the platform's candidate paths, accepting a tool only once it
/// answers a harmless version query. Results are cached for the instance's lifetime.
/// </summary>
public sealed class SshToolLocator : ISshToolLocator
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private readonly IProcessRunner _runner;
    private readonly ISshToolHints _hints;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, string?> _cache = new(StringComparer.Ordinal);

    /// <summary>Creates the locator.</summary>
    /// <param name="runner">Process runner.</param>
    /// <param name="hints">Platform candidate paths.</param>
    public SshToolLocator(IProcessRunner runner, ISshToolHints hints)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(hints);

        _runner = runner;
        _hints = hints;
    }

    /// <inheritdoc/>
    public Task<string?> LocateSshKeygenAsync(CancellationToken cancellationToken) =>
        LocateAsync("ssh-keygen", _hints.SshKeygenCandidates, cancellationToken);

    /// <inheritdoc/>
    public Task<string?> LocateSshAddAsync(CancellationToken cancellationToken) =>
        LocateAsync("ssh-add", _hints.SshAddCandidates, cancellationToken);

    private async Task<string?> LocateAsync(
        string toolName,
        IReadOnlyList<string> candidates,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(toolName, out var cached))
            {
                return cached;
            }

            string? found = null;
            foreach (var candidate in Enumerable.Repeat(toolName, 1).Concat(candidates.Where(SafeExists)))
            {
                if (await RespondsAsync(candidate, cancellationToken).ConfigureAwait(false))
                {
                    found = candidate;
                    break;
                }
            }

            _cache[toolName] = found;
            return found;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool SafeExists(string path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task<bool> RespondsAsync(string executable, CancellationToken cancellationToken)
    {
        // Both tools print usage and exit non-zero for an unknown flag, which is enough to prove
        // they exist and are runnable without touching any key material.
        var result = await _runner
            .RunAsync(executable, ["-Q", "key"], null, ProbeTimeout, cancellationToken)
            .ConfigureAwait(false);

        if (result.Failed)
        {
            return false;
        }

        return result.IsSuccess
               || result.StandardError.Contains("usage", StringComparison.OrdinalIgnoreCase)
               || result.StandardOutput.Contains("ssh-", StringComparison.OrdinalIgnoreCase);
    }
}
