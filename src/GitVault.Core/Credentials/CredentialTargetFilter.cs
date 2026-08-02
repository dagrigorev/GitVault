using GitVault.Core.Models;

namespace GitVault.Core.Credentials;

/// <summary>
/// Decides whether a vault entry is about git. A credential store holds everything the user has
/// ever saved; showing all of it would be both noisy and a privacy problem, so the default view
/// is filtered and the UI offers an explicit "show all".
/// </summary>
public static class CredentialTargetFilter
{
    /// <summary>Hosts that are git forges regardless of how the entry is named.</summary>
    public static IReadOnlyList<string> WellKnownHosts { get; } =
    [
        "github.com",
        "gitlab.com",
        "bitbucket.org",
        "dev.azure.com",
        "visualstudio.com",
        "sourceforge.net",
        "codeberg.org",
        "gitea.com",
        "git.sr.ht",
    ];

    /// <summary>Target prefixes the common helpers use.</summary>
    private static readonly string[] TargetPrefixes =
    [
        "git:",
        "LegacyGeneric:target=git:",
        "MicrosoftAccount:target=git:",
        "OAuth:target=git:",
        "gcm:",
        "github:",
    ];

    /// <summary>True when an entry is git-related.</summary>
    /// <param name="entry">Entry to test.</param>
    /// <param name="extraHosts">Hosts seen in the user's own remotes.</param>
    /// <returns><see langword="true"/> when the entry should be shown by default.</returns>
    public static bool IsGitRelated(CredentialEntry entry, IReadOnlyCollection<string>? extraHosts = null)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // Entries GitVault read out of a git-specific store are git-related by construction.
        if (entry.Vault is VaultKind.GitCredentialsFile
            or VaultKind.GcmPlaintext
            or VaultKind.GcmDpapi
            or VaultKind.GcmGpg
            or VaultKind.GitCredentialCache
            or VaultKind.GitKrakenBox)
        {
            return true;
        }

        if (TargetPrefixes.Any(p => entry.Target.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (MatchesHost(entry.Host, WellKnownHosts) || MatchesHost(entry.Target, WellKnownHosts))
        {
            return true;
        }

        if (extraHosts is { Count: > 0 }
            && (MatchesHost(entry.Host, extraHosts) || MatchesHost(entry.Target, extraHosts)))
        {
            return true;
        }

        // A "git" token anywhere in the target catches self-hosted forges named for what they are.
        return entry.Target.Contains("git", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Extracts a host from a native target string.</summary>
    /// <param name="target">Target such as <c>git:https://github.com</c>.</param>
    /// <returns>The host, or an empty string when none could be found.</returns>
    public static string ExtractHost(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return string.Empty;
        }

        var value = target;

        // Strip any of the helper prefixes, then anything before a scheme.
        foreach (var prefix in TargetPrefixes)
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                value = value[prefix.Length..];
                break;
            }
        }

        var scheme = value.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            value = value[(scheme + 3)..];
        }

        var at = value.LastIndexOf('@');
        if (at >= 0)
        {
            value = value[(at + 1)..];
        }

        var slash = value.IndexOfAny(['/', '\\']);
        if (slash >= 0)
        {
            value = value[..slash];
        }

        return value.Trim();
    }

    /// <summary>Extracts the protocol from a native target string.</summary>
    /// <param name="target">Target string.</param>
    /// <returns><c>https</c>, <c>http</c>, <c>ssh</c>, or <c>https</c> when nothing is stated.</returns>
    public static string ExtractProtocol(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return "https";
        }

        if (target.Contains("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            return "ssh";
        }

        if (target.Contains("http://", StringComparison.OrdinalIgnoreCase))
        {
            return "http";
        }

        return "https";
    }

    private static bool MatchesHost(string? candidate, IReadOnlyCollection<string> hosts)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        foreach (var host in hosts)
        {
            if (candidate.Contains(host, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
