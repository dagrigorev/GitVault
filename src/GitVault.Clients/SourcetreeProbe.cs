using System.Text.Json;
using GitVault.Core.Models;

namespace GitVault.Clients;

/// <summary>
/// Atlassian Sourcetree.
/// </summary>
/// <remarks>
/// <c>accounts.json</c> lists the hosted-service accounts. The <c>passwd</c> file next to it is
/// Sourcetree's own encrypted store; GitVault records that it exists and never tries to decrypt
/// another application's proprietary format.
///
/// VERIFY: <c>accounts.json</c> has carried at least two different shapes across versions, so
/// every field is optional here and both known shapes are accepted.
/// </remarks>
public sealed class SourcetreeProbe : ClientProbeBase
{
    /// <summary>Creates the probe.</summary>
    /// <param name="environment">Filesystem to look at.</param>
    public SourcetreeProbe(IClientEnvironment environment)
        : base(environment)
    {
    }

    /// <inheritdoc/>
    public override GitClientKind ClientKind => GitClientKind.Sourcetree;

    /// <inheritdoc/>
    public override string DisplayName => "Sourcetree";

    /// <inheritdoc/>
    protected override IEnumerable<string> CandidateConfigRoots()
    {
        yield return Path.Combine(Environment.LocalAppData, "Atlassian", "SourceTree");
        yield return Path.Combine(Environment.ApplicationSupport, "SourceTree");
        yield return Path.Combine(Environment.Home, ".sourcetree");
    }

    /// <inheritdoc/>
    protected override IEnumerable<string> CandidateInstallPaths()
    {
        yield return Path.Combine(Environment.LocalAppData, "SourceTree");
        yield return "/Applications/Sourcetree.app";
    }

    /// <inheritdoc/>
    protected override ClientReadResult ReadConfiguration(IReadOnlyList<string> roots)
    {
        var identities = new List<GitIdentity>();
        var credentials = new List<CredentialEntry>();
        var warnings = new List<KeyWarning>();
        var readAnything = false;

        foreach (var root in roots)
        {
            var accountsPath = Path.Combine(root, "accounts.json");
            using (var document = ReadJson(accountsPath))
            {
                if (document is not null)
                {
                    readAnything = true;
                    foreach (var account in ReadAccounts(document.RootElement, accountsPath))
                    {
                        if (account.Identity is not null)
                        {
                            identities.Add(account.Identity);
                        }

                        credentials.Add(account.Credential);
                    }
                }
            }

            // The encrypted password store: presence only, never contents.
            var passwordStore = Path.Combine(root, "passwd");
            if (Environment.FileExists(passwordStore))
            {
                readAnything = true;
                credentials.Add(new CredentialEntry(
                    VaultKind.Unknown,
                    passwordStore,
                    Host: string.Empty,
                    UserName: string.Empty,
                    SecretPresent: true,
                    Protocol: "https",
                    Environment.LastWriteUtc(passwordStore),
                    OwningClient: DisplayName,
                    IsReadOnly: true)
                {
                    SourcePath = passwordStore,
                });
            }

            var userHosts = Path.Combine(root, "userhosts");
            if (Environment.FileExists(userHosts))
            {
                readAnything = true;
            }
        }

        return new ClientReadResult
        {
            Identities = identities,
            Credentials = credentials,
            Warnings = warnings,
            IsOpaque = !readAnything,
        };
    }

    /// <summary>Reads the account list, accepting both shapes Sourcetree has shipped.</summary>
    /// <param name="root">Root element of <c>accounts.json</c>.</param>
    /// <param name="path">Path the document came from.</param>
    /// <returns>The accounts found.</returns>
    internal IEnumerable<(GitIdentity? Identity, CredentialEntry Credential)> ReadAccounts(
        JsonElement root,
        string path)
    {
        // Newer builds write a bare array; older ones wrap it in an object.
        var array = root.ValueKind switch
        {
            JsonValueKind.Array => root,
            JsonValueKind.Object when root.TryGetProperty("Accounts", out var wrapped)
                                      && wrapped.ValueKind == JsonValueKind.Array => wrapped,
            _ => default,
        };

        if (array.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var account in array.EnumerateArray())
        {
            var userName = TryGetString(account, "UserName")
                           ?? TryGetString(account, "username")
                           ?? TryGetString(account, "$id");

            var host = ReadHost(account);
            var email = TryGetString(account, "Email") ?? TryGetString(account, "email");

            if (string.IsNullOrWhiteSpace(host) && string.IsNullOrWhiteSpace(userName))
            {
                continue;
            }

            var identity = BuildIdentity(
                TryGetString(account, "DisplayName") ?? userName,
                email,
                IdentitySource.Sourcetree,
                path,
                hosts: string.IsNullOrWhiteSpace(host) ? [] : [host]);

            var credential = new CredentialEntry(
                VaultKind.Unknown,
                $"sourcetree:{host}",
                host ?? string.Empty,
                userName ?? string.Empty,
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

    private static string? ReadHost(JsonElement account)
    {
        var direct = TryGetString(account, "Host") ?? TryGetString(account, "host");
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        // The hosted service is sometimes a nested object carrying the base URL.
        if (account.ValueKind == JsonValueKind.Object && account.TryGetProperty("HostInstance", out var instance))
        {
            var url = TryGetString(instance, "BaseUrl") ?? TryGetString(instance, "Host");
            if (!string.IsNullOrWhiteSpace(url))
            {
                return Core.Credentials.CredentialTargetFilter.ExtractHost(url);
            }

            if (instance.ValueKind == JsonValueKind.Object
                && instance.TryGetProperty("Host", out var nestedHost))
            {
                var nested = TryGetString(nestedHost, "BaseUrl") ?? TryGetString(nestedHost, "Name");
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return Core.Credentials.CredentialTargetFilter.ExtractHost(nested);
                }
            }
        }

        return null;
    }
}
