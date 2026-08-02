using System.Text.Json;
using GitVault.Core.Models;

namespace GitVault.Clients;

/// <summary>
/// GitHub Desktop.
/// </summary>
/// <remarks>
/// The account login is readable from the Electron app's own storage. The token is not: it is
/// held by <c>safeStorage</c>/keytar, which puts it in Credential Manager, the Keychain or Secret
/// Service. GitVault reports the account and the fact that a token exists, and leaves extraction
/// to the OS vault probes, where the user's own reveal confirmation applies.
///
/// VERIFY: which file carries the account list. Recent builds keep it in the Electron
/// <c>Local Storage</c> LevelDB rather than a JSON file, so both are probed.
/// </remarks>
public sealed class GitHubDesktopProbe : ClientProbeBase
{
    /// <summary>Creates the probe.</summary>
    /// <param name="environment">Filesystem to look at.</param>
    public GitHubDesktopProbe(IClientEnvironment environment)
        : base(environment)
    {
    }

    /// <inheritdoc/>
    public override GitClientKind ClientKind => GitClientKind.GitHubDesktop;

    /// <inheritdoc/>
    public override string DisplayName => "GitHub Desktop";

    /// <inheritdoc/>
    protected override IEnumerable<string> CandidateConfigRoots()
    {
        yield return Path.Combine(Environment.AppData, "GitHub Desktop");
        yield return Path.Combine(Environment.ApplicationSupport, "GitHub Desktop");

        // The Linux fork keeps the same layout under a slightly different name.
        yield return Path.Combine(Environment.Home, ".config", "GitHub Desktop");
    }

    /// <inheritdoc/>
    protected override IEnumerable<string> CandidateInstallPaths()
    {
        yield return Path.Combine(Environment.LocalAppData, "GitHubDesktop");
        yield return "/Applications/GitHub Desktop.app";
    }

    /// <inheritdoc/>
    protected override ClientReadResult ReadConfiguration(IReadOnlyList<string> roots)
    {
        var identities = new List<GitIdentity>();
        var credentials = new List<CredentialEntry>();
        var readAnything = false;

        foreach (var root in roots)
        {
            foreach (var accountsFile in new[] { "accounts.json", "user.json" })
            {
                var path = Path.Combine(root, accountsFile);
                using var document = ReadJson(path);
                if (document is null)
                {
                    continue;
                }

                readAnything = true;

                foreach (var account in ReadAccounts(document.RootElement, path))
                {
                    if (account.Identity is not null)
                    {
                        identities.Add(account.Identity);
                    }

                    credentials.Add(account.Credential);
                }
            }

            // Electron's local storage proves the app has been run and holds the account, but it
            // is a LevelDB that GitVault will not parse. Presence is still worth reporting.
            if (Environment.DirectoryExists(Path.Combine(root, "Local Storage")))
            {
                readAnything = true;
            }
        }

        return new ClientReadResult
        {
            Identities = identities,
            Credentials = credentials,
            IsOpaque = !readAnything || (identities.Count == 0 && credentials.Count == 0),
        };
    }

    /// <summary>Reads the account entries out of the app's JSON storage.</summary>
    /// <param name="root">Root element.</param>
    /// <param name="path">Path the document came from.</param>
    /// <returns>The accounts found.</returns>
    internal IEnumerable<(GitIdentity? Identity, CredentialEntry Credential)> ReadAccounts(
        JsonElement root,
        string path)
    {
        var array = root.ValueKind switch
        {
            JsonValueKind.Array => root,
            JsonValueKind.Object when root.TryGetProperty("accounts", out var wrapped)
                                      && wrapped.ValueKind == JsonValueKind.Array => wrapped,
            _ => default,
        };

        if (array.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var account in array.EnumerateArray())
        {
            var login = TryGetString(account, "login");
            if (string.IsNullOrWhiteSpace(login))
            {
                continue;
            }

            var endpoint = TryGetString(account, "endpoint") ?? "https://api.github.com";
            var host = Core.Credentials.CredentialTargetFilter.ExtractHost(endpoint);

            // GitHub Enterprise endpoints are api.<host>; show the forge the user recognises.
            if (host.StartsWith("api.", StringComparison.OrdinalIgnoreCase))
            {
                host = host["api.".Length..];
            }

            var identity = BuildIdentity(
                TryGetString(account, "name") ?? login,
                TryGetString(account, "emails") is null ? TryGetString(account, "email") : null,
                IdentitySource.GitHubDesktop,
                path,
                hosts: [host]);

            var credential = new CredentialEntry(
                VaultKind.Unknown,
                $"github-desktop:{host}",
                host,
                login,

                // The token lives in the OS vault, not here; we know it exists, not what it is.
                SecretPresent: true,
                Protocol: "https",
                Environment.LastWriteUtc(path),
                OwningClient: DisplayName,
                IsReadOnly: true)
            {
                SourcePath = path,
            };

            yield return (identity, credential);
        }
    }
}
