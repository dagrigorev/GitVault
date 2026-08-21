using GitVault.Core.Abstractions;
using GitVault.Core.Diagnostics;
using GitVault.Core.Git;
using GitVault.Core.Models;

namespace GitVault.Core.Discovery;

/// <summary>
/// Reads author identities out of git's own configuration, one per originating file, so that a
/// user with several conditional includes sees each identity separately.
/// </summary>
public sealed class GitIdentityProbe : IProbe
{
    /// <summary>Warning code raised when no <c>git</c> executable could be located.</summary>
    public const string GitNotFoundCode = "GitNotFound";

    /// <summary>Warning code raised when neither name nor e-mail is set anywhere.</summary>
    public const string NoIdentityConfiguredCode = "NoIdentityConfigured";

    private readonly IGitConfigService _config;

    /// <summary>Creates the probe.</summary>
    /// <param name="config">Configuration service to read from.</param>
    public GitIdentityProbe(IGitConfigService config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
    }

    /// <inheritdoc/>
    public string ProbeId => "git.identities";

    /// <inheritdoc/>
    public string DisplayName => "Git configuration";

    /// <inheritdoc/>
    public bool IsSupportedOnThisPlatform => true;

    /// <inheritdoc/>
    public async Task<ProbeResult<ProbePayload>> ProbeAsync(CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();

        IReadOnlyList<GitConfigValue> all;
        try
        {
            all = await _config.ListAsync(null, cancellationToken).ConfigureAwait(false);
        }
        catch (GitConfigException ex)
        {
            return ProbeResult<ProbePayload>.Fail(ProbeId, ProbeStatus.ParseError, ex.Message, started.Elapsed);
        }
        catch (UnauthorizedAccessException ex)
        {
            return ProbeResult<ProbePayload>.Fail(ProbeId, ProbeStatus.AccessDenied, ex.Message, started.Elapsed);
        }

        var identities = BuildIdentities(all);
        var warnings = new List<KeyWarning>();

        if (!_config.HasGitBinary)
        {
            warnings.Add(new KeyWarning(GitNotFoundCode, WarningSeverity.Medium, string.Empty));
        }

        if (identities.Count == 0)
        {
            warnings.Add(new KeyWarning(NoIdentityConfiguredCode, WarningSeverity.Medium, string.Empty));
        }

        var payload = new ProbePayload { Identities = identities, Warnings = warnings };
        return ProbeResult<ProbePayload>.Ok(ProbeId, payload, started.Elapsed);
    }

    /// <summary>Groups name/e-mail/signing-key values by the file they came from.</summary>
    /// <param name="all">Every visible configuration entry.</param>
    /// <returns>One identity per originating file that defines a name or an e-mail.</returns>
    internal static IReadOnlyList<GitIdentity> BuildIdentities(IReadOnlyList<GitConfigValue> all)
    {
        var byOrigin = new Dictionary<string, IdentityDraft>(StringComparer.OrdinalIgnoreCase);
        var hostsByOrigin = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in all)
        {
            var origin = entry.Origin;
            var draft = byOrigin.TryGetValue(origin, out var existing)
                ? existing
                : byOrigin[origin] = new IdentityDraft(entry.Scope, StripFilePrefix(origin));

            switch (entry.Key.ToLowerInvariant())
            {
                case GitConfigKeys.UserName:
                    draft.UserName = entry.Value;
                    break;
                case GitConfigKeys.UserEmail:
                    draft.Email = entry.Value;
                    break;
                case GitConfigKeys.SigningKey:
                    draft.SigningKey = entry.Value;
                    break;
            }

            // credential.<url>.* subsections name the hosts this file is concerned with.
            var host = ExtractHost(entry.Key);
            if (host is not null)
            {
                if (!hostsByOrigin.TryGetValue(origin, out var set))
                {
                    hostsByOrigin[origin] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }

                set.Add(host);
            }
        }

        var identities = new List<GitIdentity>();
        foreach (var (origin, draft) in byOrigin)
        {
            if (string.IsNullOrWhiteSpace(draft.UserName) && string.IsNullOrWhiteSpace(draft.Email))
            {
                continue;
            }

            var hosts = hostsByOrigin.TryGetValue(origin, out var set)
                ? set.OrderBy(h => h, StringComparer.OrdinalIgnoreCase).ToArray()
                : [];

            identities.Add(GitIdentity.Create(
                draft.UserName ?? string.Empty,
                draft.Email ?? string.Empty,
                SourceFor(draft.Scope),
                draft.Path,
                draft.SigningKey,
                hosts));
        }

        return identities;
    }

    /// <summary>Extracts a host from a key such as <c>credential.https://github.com.helper</c>.</summary>
    /// <param name="key">Fully qualified configuration key.</param>
    /// <returns>The host, or null when the key names no host.</returns>
    internal static string? ExtractHost(string key)
    {
        var (section, subsection, _) = GitConfigService.SplitKey(key);
        if (subsection is null || section is not ("credential" or "http" or "url"))
        {
            return null;
        }

        var candidate = subsection;
        var scheme = candidate.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            candidate = candidate[(scheme + 3)..];
        }

        var slash = candidate.IndexOf('/', StringComparison.Ordinal);
        if (slash >= 0)
        {
            candidate = candidate[..slash];
        }

        var at = candidate.LastIndexOf('@');
        if (at >= 0)
        {
            candidate = candidate[(at + 1)..];
        }

        return string.IsNullOrWhiteSpace(candidate) ? null : candidate;
    }

    private static IdentitySource SourceFor(GitConfigScope scope) => scope switch
    {
        GitConfigScope.System => IdentitySource.GitSystemConfig,
        GitConfigScope.Global => IdentitySource.GitGlobalConfig,
        GitConfigScope.Local => IdentitySource.RepoLocal,
        GitConfigScope.Worktree => IdentitySource.RepoWorktree,
        _ => IdentitySource.Unknown,
    };

    private static string StripFilePrefix(string origin) =>
        origin.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? origin[5..] : origin;

    private sealed class IdentityDraft(GitConfigScope scope, string path)
    {
        internal GitConfigScope Scope { get; } = scope;

        internal string Path { get; } = path;

        internal string? UserName { get; set; }

        internal string? Email { get; set; }

        internal string? SigningKey { get; set; }
    }
}
