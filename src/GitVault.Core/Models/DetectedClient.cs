namespace GitVault.Core.Models;

/// <summary>A third-party Git client found on the machine.</summary>
/// <param name="Kind">Which client it is.</param>
/// <param name="DisplayName">Name to show in the UI. Product names are not localized.</param>
/// <param name="Version">Version string when the client exposes one.</param>
/// <param name="InstallPath">Installation directory or executable path.</param>
public sealed record DetectedClient(
    GitClientKind Kind,
    string DisplayName,
    string? Version,
    string? InstallPath)
{
    /// <summary>Directories the client keeps its configuration in.</summary>
    public IReadOnlyList<string> ConfigRoots { get; init; } = [];

    /// <summary>Identities the client has configured.</summary>
    public IReadOnlyList<GitIdentity> Accounts { get; init; } = [];

    /// <summary>Credential entries attributable to this client.</summary>
    public IReadOnlyList<CredentialEntry> Credentials { get; init; } = [];

    /// <summary>How the client is wired up for SSH, when that could be determined.</summary>
    public ClientSshConfig? SshConfiguration { get; init; }

    /// <summary>Findings about this client's configuration.</summary>
    public IReadOnlyList<KeyWarning> Warnings { get; init; } = [];

    /// <summary>
    /// True when the client was detected but its configuration could not be read or understood.
    /// The UI shows it as "detected, unreadable" rather than hiding it.
    /// </summary>
    public bool IsOpaque { get; init; }
}

/// <summary>How a client is configured to perform SSH authentication.</summary>
/// <param name="SshExecutable">The <c>ssh</c>/<c>plink</c>/<c>TortoisePlink</c> binary the client invokes.</param>
/// <param name="PreferredAgent">Agent the client expects to talk to, when it pins one.</param>
public sealed record ClientSshConfig(string? SshExecutable, AgentKind? PreferredAgent)
{
    /// <summary>Key files the client references, keyed by the remote or host they are bound to.</summary>
    public IReadOnlyDictionary<string, string> BoundKeyFiles { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the client uses PuTTY-format keys.</summary>
    public bool UsesPuttyKeys { get; init; }
}

/// <summary>A <c>Host</c> block GitVault manages inside <c>~/.ssh/config</c>.</summary>
/// <param name="Alias">The <c>Host</c> pattern, e.g. <c>github.com-work</c>.</param>
/// <param name="HostName">Real host to connect to.</param>
/// <param name="User">SSH user, usually <c>git</c>.</param>
/// <param name="IdentityFile">Path to the private key.</param>
/// <param name="IdentitiesOnly">Whether to emit <c>IdentitiesOnly yes</c>.</param>
public sealed record SshHostAlias(
    string Alias,
    string HostName,
    string User,
    string? IdentityFile,
    bool IdentitiesOnly = true)
{
    /// <summary>Extra <c>key value</c> options to emit inside the block.</summary>
    public IReadOnlyDictionary<string, string> ExtraOptions { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
