using GitVault.Core.Models;

namespace GitVault.Clients;

/// <summary>
/// GitHub CLI. <c>~/.config/gh/hosts.yml</c> lists each host with its account and how the token
/// is stored, in a small enough subset of YAML to read without a parser dependency.
/// </summary>
public sealed class GhCliProbe : ClientProbeBase
{
    /// <summary>Creates the probe.</summary>
    /// <param name="environment">Filesystem to look at.</param>
    public GhCliProbe(IClientEnvironment environment)
        : base(environment)
    {
    }

    /// <inheritdoc/>
    public override GitClientKind ClientKind => GitClientKind.GhCli;

    /// <inheritdoc/>
    public override string DisplayName => "GitHub CLI";

    /// <inheritdoc/>
    protected override IEnumerable<string> CandidateConfigRoots()
    {
        yield return Path.Combine(Environment.Home, ".config", "gh");
        yield return Path.Combine(Environment.AppData, "GitHub CLI");
    }

    /// <inheritdoc/>
    protected override ClientReadResult ReadConfiguration(IReadOnlyList<string> roots)
    {
        var identities = new List<GitIdentity>();
        var credentials = new List<CredentialEntry>();
        var readAnything = false;

        foreach (var root in roots)
        {
            var hostsPath = Path.Combine(root, "hosts.yml");
            var text = Environment.ReadAllText(hostsPath);
            if (text is null)
            {
                continue;
            }

            readAnything = true;

            foreach (var host in ParseHosts(text))
            {
                var identity = BuildIdentity(host.User, null, IdentitySource.GhCli, hostsPath, [host.Host]);
                if (identity is not null)
                {
                    identities.Add(identity);
                }

                credentials.Add(new CredentialEntry(
                    host.TokenInFile ? VaultKind.GcmPlaintext : VaultKind.Unknown,
                    $"gh:{host.Host}",
                    host.Host,
                    host.User ?? string.Empty,
                    SecretPresent: true,
                    Protocol: "https",
                    Environment.LastWriteUtc(hostsPath),
                    OwningClient: DisplayName,
                    IsReadOnly: true)
                {
                    SourcePath = hostsPath,
                });
            }
        }

        return new ClientReadResult
        {
            Identities = identities,
            Credentials = credentials,
            IsOpaque = !readAnything,
        };
    }

    /// <summary>One host block from <c>hosts.yml</c>.</summary>
    /// <param name="Host">Host name.</param>
    /// <param name="User">Account name, when present.</param>
    /// <param name="TokenInFile">True when the token is written into the file itself.</param>
    internal sealed record GhHost(string Host, string? User, bool TokenInFile);

    /// <summary>
    /// Reads the host blocks. The file is two levels deep and never uses flow style, so
    /// indentation alone is enough; a full YAML parser would be a dependency for no gain.
    /// </summary>
    /// <param name="text">File contents.</param>
    /// <returns>The hosts found.</returns>
    internal static IReadOnlyList<GhHost> ParseHosts(string text)
    {
        var hosts = new List<GhHost>();
        string? currentHost = null;
        string? user = null;
        var tokenInFile = false;

        foreach (var raw in (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (raw.Length == 0 || raw.TrimStart().StartsWith('#'))
            {
                continue;
            }

            var indented = char.IsWhiteSpace(raw[0]);
            var line = raw.Trim();

            if (!indented)
            {
                Flush();
                currentHost = line.TrimEnd(':').Trim();
                continue;
            }

            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim().Trim('"');

            if (string.Equals(key, "user", StringComparison.OrdinalIgnoreCase))
            {
                user = value;
            }
            else if (string.Equals(key, "oauth_token", StringComparison.OrdinalIgnoreCase))
            {
                tokenInFile = value.Length > 0;
            }
        }

        Flush();
        return hosts;

        void Flush()
        {
            if (!string.IsNullOrWhiteSpace(currentHost))
            {
                hosts.Add(new GhHost(currentHost, user, tokenInFile));
            }

            currentHost = null;
            user = null;
            tokenInFile = false;
        }
    }
}

/// <summary>GitLab CLI, which uses the same layout as the GitHub one.</summary>
public sealed class GlabCliProbe : ClientProbeBase
{
    /// <summary>Creates the probe.</summary>
    /// <param name="environment">Filesystem to look at.</param>
    public GlabCliProbe(IClientEnvironment environment)
        : base(environment)
    {
    }

    /// <inheritdoc/>
    public override GitClientKind ClientKind => GitClientKind.GlabCli;

    /// <inheritdoc/>
    public override string DisplayName => "GitLab CLI";

    /// <inheritdoc/>
    protected override IEnumerable<string> CandidateConfigRoots()
    {
        yield return Path.Combine(Environment.Home, ".config", "glab-cli");
        yield return Path.Combine(Environment.AppData, "glab-cli");
    }

    /// <inheritdoc/>
    protected override ClientReadResult ReadConfiguration(IReadOnlyList<string> roots)
    {
        var identities = new List<GitIdentity>();
        var credentials = new List<CredentialEntry>();
        var readAnything = false;

        foreach (var root in roots)
        {
            var configPath = Path.Combine(root, "config.yml");
            var text = Environment.ReadAllText(configPath);
            if (text is null)
            {
                continue;
            }

            readAnything = true;

            foreach (var host in GhCliProbe.ParseHosts(text))
            {
                // glab nests hosts under a "hosts:" key; that key itself is not a host.
                if (string.Equals(host.Host, "hosts", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var identity = BuildIdentity(host.User, null, IdentitySource.GlabCli, configPath, [host.Host]);
                if (identity is not null)
                {
                    identities.Add(identity);
                }

                credentials.Add(new CredentialEntry(
                    VaultKind.Unknown,
                    $"glab:{host.Host}",
                    host.Host,
                    host.User ?? string.Empty,
                    SecretPresent: true,
                    Protocol: "https",
                    Environment.LastWriteUtc(configPath),
                    OwningClient: DisplayName,
                    IsReadOnly: true)
                {
                    SourcePath = configPath,
                });
            }
        }

        return new ClientReadResult
        {
            Identities = identities,
            Credentials = credentials,
            IsOpaque = !readAnything,
        };
    }
}

/// <summary>
/// Windows Subsystem for Linux. Each distribution has its own home directory, its own
/// <c>.gitconfig</c> and its own <c>.ssh</c>, all reachable from Windows through <c>\\wsl$</c>.
/// </summary>
/// <remarks>
/// VERIFY: the <c>\\wsl$</c> and <c>\\wsl.localhost</c> share names across Windows builds. Both
/// are probed because Microsoft changed the canonical one mid-life.
/// </remarks>
public sealed class WslProbe : ClientProbeBase
{
    /// <summary>Creates the probe.</summary>
    /// <param name="environment">Filesystem to look at.</param>
    public WslProbe(IClientEnvironment environment)
        : base(environment)
    {
    }

    /// <inheritdoc/>
    public override GitClientKind ClientKind => GitClientKind.WslDistro;

    /// <inheritdoc/>
    public override string DisplayName => "WSL";

    /// <inheritdoc/>
    public override bool IsSupportedOnThisPlatform => OperatingSystem.IsWindows();

    /// <inheritdoc/>
    public override TimeSpan Timeout => TimeSpan.FromSeconds(10);

    /// <inheritdoc/>
    protected override IEnumerable<string> CandidateConfigRoots()
    {
        foreach (var share in new[] { @"\\wsl$", @"\\wsl.localhost" })
        {
            foreach (var distribution in Environment.EnumerateDirectories(share))
            {
                foreach (var home in Environment.EnumerateDirectories(Path.Combine(distribution, "home")))
                {
                    yield return home;
                }
            }
        }
    }

    /// <inheritdoc/>
    protected override ClientReadResult ReadConfiguration(IReadOnlyList<string> roots)
    {
        var identities = new List<GitIdentity>();
        var warnings = new List<KeyWarning>();

        foreach (var home in roots)
        {
            var configPath = Path.Combine(home, ".gitconfig");
            var text = Environment.ReadAllText(configPath);
            if (text is null)
            {
                continue;
            }

            var (name, email) = ReadIdentityFromConfig(text);
            var identity = BuildIdentity(name, email, IdentitySource.Wsl, configPath);
            if (identity is not null)
            {
                identities.Add(identity);
            }

            // Keys inside a distribution are a separate copy from the Windows ones, and people
            // routinely forget one of the two exists.
            var sshDirectory = Path.Combine(home, ".ssh");
            if (Environment.DirectoryExists(sshDirectory)
                && Environment.EnumerateFiles(sshDirectory, "*.pub").Count > 0)
            {
                warnings.Add(new KeyWarning(SeparateKeysCode, WarningSeverity.Info, sshDirectory));
            }
        }

        return new ClientReadResult
        {
            Identities = identities,
            Warnings = warnings,
            IsOpaque = identities.Count == 0,
        };
    }

    /// <summary>Warning code noting that a distribution keeps its own SSH keys.</summary>
    public const string SeparateKeysCode = "WslSeparateKeys";

    /// <summary>
    /// Pulls name and e-mail out of a <c>.gitconfig</c> without the full parser, because a WSL
    /// home directory is reached over a network share and the file may be unreadable mid-read.
    /// </summary>
    /// <param name="text">File contents.</param>
    /// <returns>The identity fields, either of which may be null.</returns>
    internal static (string? Name, string? Email) ReadIdentityFromConfig(string text)
    {
        string? name = null;
        string? email = null;
        var inUserSection = false;

        foreach (var raw in (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
            {
                continue;
            }

            if (line[0] == '[')
            {
                inUserSection = line.StartsWith("[user]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inUserSection)
            {
                continue;
            }

            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim().Trim('"');

            if (string.Equals(key, "name", StringComparison.OrdinalIgnoreCase))
            {
                name = value;
            }
            else if (string.Equals(key, "email", StringComparison.OrdinalIgnoreCase))
            {
                email = value;
            }
        }

        return (name, email);
    }
}
