using System.Text.Json;
using GitVault.Core.Models;

namespace GitVault.Clients;

/// <summary>
/// GitKraken Desktop.
/// </summary>
/// <remarks>
/// Identities live in <c>profiles/&lt;id&gt;/profile.json</c> under the application's data
/// directory. Credentials live in an encrypted secure-storage file and in the OS keychain;
/// GitVault reports that they exist and never attempts to read them.
///
/// VERIFY: the profile layout against a current GitKraken. It has changed between major
/// versions, so the reader treats every field as optional and reports the client as detected
/// even when nothing parses.
/// </remarks>
public sealed class GitKrakenProbe : ClientProbeBase
{
    /// <summary>Creates the probe.</summary>
    /// <param name="environment">Filesystem to look at.</param>
    public GitKrakenProbe(IClientEnvironment environment)
        : base(environment)
    {
    }

    /// <inheritdoc/>
    public override GitClientKind ClientKind => GitClientKind.GitKraken;

    /// <inheritdoc/>
    public override string DisplayName => "GitKraken";

    /// <inheritdoc/>
    protected override IEnumerable<string> CandidateConfigRoots()
    {
        // Windows keeps it under %APPDATA%; the other platforms use a dot directory in $HOME.
        yield return Path.Combine(Environment.AppData, ".gitkraken");
        yield return Path.Combine(Environment.Home, ".gitkraken");
    }

    /// <inheritdoc/>
    protected override IEnumerable<string> CandidateInstallPaths()
    {
        yield return Path.Combine(Environment.LocalAppData, "gitkraken");
        yield return Path.Combine(Environment.ProgramFiles, "GitKraken");
        yield return "/Applications/GitKraken.app";
        yield return "/opt/gitkraken";
    }

    /// <inheritdoc/>
    protected override ClientReadResult ReadConfiguration(IReadOnlyList<string> roots)
    {
        var identities = new List<GitIdentity>();
        var credentials = new List<CredentialEntry>();
        var readAnything = false;
        string? version = null;

        foreach (var root in roots)
        {
            version ??= ReadVersion(Path.Combine(root, "config"));

            foreach (var profileDirectory in Environment.EnumerateDirectories(Path.Combine(root, "profiles")))
            {
                var profilePath = Path.Combine(profileDirectory, "profile.json");
                using var document = ReadJson(profilePath);
                if (document is null)
                {
                    continue;
                }

                readAnything = true;

                var identity = ReadProfileIdentity(document.RootElement, profilePath);
                if (identity is not null)
                {
                    identities.Add(identity);
                }

                credentials.AddRange(ReadProviderAccounts(document.RootElement, profilePath));
            }

            // The secure box is opaque by design. Its presence is the reportable fact.
            foreach (var secureFile in new[] { "secBox", "secureStorage", "keytar.json" })
            {
                var path = Path.Combine(root, secureFile);
                if (Environment.FileExists(path))
                {
                    readAnything = true;
                    credentials.Add(new CredentialEntry(
                        VaultKind.GitKrakenBox,
                        path,
                        Host: string.Empty,
                        UserName: string.Empty,
                        SecretPresent: true,
                        Protocol: "https",
                        Environment.LastWriteUtc(path),
                        OwningClient: DisplayName,
                        IsReadOnly: true)
                    {
                        SourcePath = path,
                    });
                }
            }
        }

        return new ClientReadResult
        {
            Identities = identities,
            Credentials = credentials,
            Version = version,
            IsOpaque = !readAnything,
        };
    }

    /// <summary>Reads the author identity out of a profile document.</summary>
    /// <param name="root">Root element of <c>profile.json</c>.</param>
    /// <param name="path">Path the document came from.</param>
    /// <returns>The identity, or null.</returns>
    internal GitIdentity? ReadProfileIdentity(JsonElement root, string path)
    {
        // Different versions nest this differently, so several shapes are accepted.
        var name = TryGetString(root, "name")
                   ?? TryGetString(root, "userName")
                   ?? TryGetString(root, "gitName");

        var email = TryGetString(root, "email")
                    ?? TryGetString(root, "userEmail")
                    ?? TryGetString(root, "gitEmail");

        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("user", out var user))
        {
            name ??= TryGetString(user, "name");
            email ??= TryGetString(user, "email");
        }

        return BuildIdentity(name, email, IdentitySource.GitKraken, path);
    }

    /// <summary>Reads the hosted-provider accounts a profile lists.</summary>
    /// <param name="root">Root element of <c>profile.json</c>.</param>
    /// <param name="path">Path the document came from.</param>
    /// <returns>One credential record per provider account.</returns>
    internal IEnumerable<CredentialEntry> ReadProviderAccounts(JsonElement root, string path)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("providers", out var providers)
            || providers.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var provider in providers.EnumerateArray())
        {
            var host = TryGetString(provider, "host") ?? TryGetString(provider, "domain");
            var login = TryGetString(provider, "username") ?? TryGetString(provider, "login");

            if (string.IsNullOrWhiteSpace(host))
            {
                continue;
            }

            yield return new CredentialEntry(
                VaultKind.GitKrakenBox,
                $"gitkraken:{host}",
                host,
                login ?? string.Empty,
                SecretPresent: true,
                Protocol: "https",
                Environment.LastWriteUtc(path),
                OwningClient: DisplayName,
                IsReadOnly: true)
            {
                SourcePath = path,
            };
        }
    }

    private string? ReadVersion(string configPath)
    {
        using var document = ReadJson(configPath);
        return document is null ? null : TryGetString(document.RootElement, "appVersion")
                                         ?? TryGetString(document.RootElement, "version");
    }
}
