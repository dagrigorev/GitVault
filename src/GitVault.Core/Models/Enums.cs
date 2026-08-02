namespace GitVault.Core.Models;

/// <summary>Where a <see cref="GitIdentity"/> was discovered.</summary>
public enum IdentitySource
{
    /// <summary>Unknown or not yet attributed source.</summary>
    Unknown = 0,

    /// <summary>Machine-wide git configuration (<c>git config --system</c>).</summary>
    GitSystemConfig,

    /// <summary>Per-user git configuration (<c>~/.gitconfig</c>).</summary>
    GitGlobalConfig,

    /// <summary>Repository-local configuration (<c>.git/config</c>).</summary>
    RepoLocal,

    /// <summary>Per-worktree configuration (<c>.git/config.worktree</c>).</summary>
    RepoWorktree,

    /// <summary>A file pulled in through an <c>[include]</c> or <c>[includeIf]</c> directive.</summary>
    GitIncludedFile,

    /// <summary>GitKraken profile storage.</summary>
    GitKraken,

    /// <summary>Atlassian Sourcetree account storage.</summary>
    Sourcetree,

    /// <summary>TortoiseGit registry configuration.</summary>
    TortoiseGit,

    /// <summary>GitHub Desktop application storage.</summary>
    GitHubDesktop,

    /// <summary>Fork application storage.</summary>
    Fork,

    /// <summary>Sublime Merge application storage.</summary>
    SublimeMerge,

    /// <summary>SmartGit application storage.</summary>
    SmartGit,

    /// <summary>Git Extensions application storage.</summary>
    GitExtensions,

    /// <summary>GitButler application storage.</summary>
    GitButler,

    /// <summary>Tower application storage.</summary>
    Tower,

    /// <summary>Visual Studio Code (or VSCodium) settings.</summary>
    VsCode,

    /// <summary>A JetBrains IDE options directory.</summary>
    JetBrains,

    /// <summary>GitHub CLI (<c>gh</c>) host configuration.</summary>
    GhCli,

    /// <summary>GitLab CLI (<c>glab</c>) host configuration.</summary>
    GlabCli,

    /// <summary>lazygit configuration.</summary>
    Lazygit,

    /// <summary>A Windows Subsystem for Linux distribution.</summary>
    Wsl,

    /// <summary>A probe described purely by a JSON manifest.</summary>
    ManifestProbe,
}

/// <summary>How sure the discovery engine is about a detected artifact.</summary>
public enum DetectionConfidence
{
    /// <summary>Derived from a guess or a weak signal; show it, but mark it.</summary>
    Heuristic = 0,

    /// <summary>Read from a documented location, but the format was partially inferred.</summary>
    Probable = 1,

    /// <summary>Read verbatim from an authoritative source such as <c>git config</c>.</summary>
    Certain = 2,
}

/// <summary>SSH public key algorithm families GitVault understands.</summary>
public enum SshKeyAlgorithm
{
    /// <summary>Algorithm could not be determined.</summary>
    Unknown = 0,

    /// <summary><c>ssh-rsa</c>.</summary>
    Rsa,

    /// <summary><c>ssh-ed25519</c>.</summary>
    Ed25519,

    /// <summary>
    /// <c>ssh-ed448</c>. PuTTY can create these; OpenSSH cannot read them, so a key in this
    /// format works with TortoiseGit and Pageant but not with the <c>ssh</c> command.
    /// </summary>
    Ed448,

    /// <summary><c>sk-ssh-ed25519@openssh.com</c> (FIDO2 hardware backed).</summary>
    Ed25519Sk,

    /// <summary><c>ecdsa-sha2-nistp*</c>.</summary>
    Ecdsa,

    /// <summary><c>sk-ecdsa-sha2-nistp256@openssh.com</c> (FIDO2 hardware backed).</summary>
    EcdsaSk,

    /// <summary><c>ssh-dss</c>. Considered obsolete.</summary>
    Dsa,
}

/// <summary>On-disk container format of an SSH private key.</summary>
public enum SshKeyFormat
{
    /// <summary>Format could not be determined.</summary>
    Unknown = 0,

    /// <summary>OpenSSH v1 key container (<c>-----BEGIN OPENSSH PRIVATE KEY-----</c>).</summary>
    OpenSsh,

    /// <summary>Traditional PEM (<c>BEGIN RSA/DSA/EC PRIVATE KEY</c>), optionally with <c>DEK-Info</c>.</summary>
    Pem,

    /// <summary>PKCS#8, plain or encrypted.</summary>
    Pkcs8,

    /// <summary>PuTTY private key, version 2.</summary>
    Ppk2,

    /// <summary>PuTTY private key, version 3 (Argon2 KDF).</summary>
    Ppk3,

    /// <summary>A public key file only (<c>.pub</c> or RFC 4716).</summary>
    PublicOnly,
}

/// <summary>Kinds of SSH agent GitVault can talk to.</summary>
public enum AgentKind
{
    /// <summary>Unrecognised agent reached through <c>SSH_AUTH_SOCK</c>.</summary>
    Unknown = 0,

    /// <summary>OpenSSH agent on a unix domain socket.</summary>
    OpenSshUnix,

    /// <summary>OpenSSH agent on the Windows named pipe <c>\\.\pipe\openssh-ssh-agent</c>.</summary>
    OpenSshWindowsPipe,

    /// <summary>PuTTY Pageant.</summary>
    Pageant,

    /// <summary>GnuPG agent with <c>enable-ssh-support</c>.</summary>
    GpgAgent,

    /// <summary>1Password SSH agent.</summary>
    OnePassword,

    /// <summary>KeeAgent (KeePass plugin).</summary>
    KeeAgent,

    /// <summary>A relay that forwards to an agent inside WSL (or out of it).</summary>
    WslRelay,
}

/// <summary>Backing store a credential was found in.</summary>
public enum VaultKind
{
    /// <summary>Store could not be classified.</summary>
    Unknown = 0,

    /// <summary>Windows Credential Manager.</summary>
    WindowsCredentialManager,

    /// <summary>macOS Keychain.</summary>
    MacKeychain,

    /// <summary>freedesktop.org Secret Service over D-Bus.</summary>
    SecretService,

    /// <summary>KDE KWallet.</summary>
    KWallet,

    /// <summary>Git's <c>store</c> helper file. Plaintext.</summary>
    GitCredentialsFile,

    /// <summary>Git Credential Manager plaintext store.</summary>
    GcmPlaintext,

    /// <summary>Git Credential Manager DPAPI store (Windows).</summary>
    GcmDpapi,

    /// <summary>Git Credential Manager GPG/pass store.</summary>
    GcmGpg,

    /// <summary>GitKraken's opaque secure box.</summary>
    GitKrakenBox,

    /// <summary>Git's in-memory <c>cache</c> helper.</summary>
    GitCredentialCache,
}

/// <summary>Third-party Git clients GitVault probes for.</summary>
public enum GitClientKind
{
    /// <summary>Unrecognised client.</summary>
    Unknown = 0,

    /// <summary>GitKraken Desktop.</summary>
    GitKraken,

    /// <summary>TortoiseGit (Windows).</summary>
    TortoiseGit,

    /// <summary>Atlassian Sourcetree.</summary>
    Sourcetree,

    /// <summary>GitHub Desktop.</summary>
    GitHubDesktop,

    /// <summary>Fork.</summary>
    Fork,

    /// <summary>Sublime Merge.</summary>
    SublimeMerge,

    /// <summary>SmartGit.</summary>
    SmartGit,

    /// <summary>Git Extensions.</summary>
    GitExtensions,

    /// <summary>GitButler.</summary>
    GitButler,

    /// <summary>Tower.</summary>
    Tower,

    /// <summary>Visual Studio Code or VSCodium.</summary>
    VsCode,

    /// <summary>A JetBrains IDE.</summary>
    JetBrains,

    /// <summary>GitHub CLI.</summary>
    GhCli,

    /// <summary>GitLab CLI.</summary>
    GlabCli,

    /// <summary>lazygit.</summary>
    Lazygit,

    /// <summary>A WSL distribution.</summary>
    WslDistro,

    /// <summary>Git Credential Manager itself.</summary>
    GitCredentialManager,

    /// <summary>A client described by a JSON manifest under <c>clients/</c>.</summary>
    ManifestDefined,
}

/// <summary>Scope at which a profile is applied.</summary>
public enum ActivationScope
{
    /// <summary>Per-user git configuration.</summary>
    Global = 0,

    /// <summary>Machine-wide git configuration. Usually needs elevation.</summary>
    System,

    /// <summary>A single repository's local configuration.</summary>
    Repository,
}

/// <summary>Git configuration scopes, ordered from lowest to highest precedence.</summary>
public enum GitConfigScope
{
    /// <summary>Git's built-in defaults, reported by <c>--show-scope</c> as <c>command</c>.</summary>
    Unknown = 0,

    /// <summary>Machine-wide configuration.</summary>
    System,

    /// <summary>Per-user configuration on Windows (<c>%PROGRAMDATA%</c> style) — reported as <c>global</c> by git.</summary>
    Global,

    /// <summary>Repository-local configuration.</summary>
    Local,

    /// <summary>Per-worktree configuration.</summary>
    Worktree,

    /// <summary>Values supplied on the command line.</summary>
    Command,
}

/// <summary>Outcome of a single discovery probe.</summary>
public enum ProbeStatus
{
    /// <summary>Probe ran and produced a result.</summary>
    Ok = 0,

    /// <summary>The target application or store is not present on this machine.</summary>
    NotInstalled,

    /// <summary>The operating system refused access to the data.</summary>
    AccessDenied,

    /// <summary>The probe exceeded its time budget.</summary>
    Timeout,

    /// <summary>Data was found but could not be understood.</summary>
    ParseError,

    /// <summary>The probe does not apply to the current operating system.</summary>
    NotApplicable,

    /// <summary>An unexpected failure. Diagnostics carry the detail.</summary>
    Failed,
}

/// <summary>Severity of a health finding.</summary>
public enum WarningSeverity
{
    /// <summary>Informational only.</summary>
    Info = 0,

    /// <summary>Worth fixing but not dangerous.</summary>
    Low,

    /// <summary>Should be fixed.</summary>
    Medium,

    /// <summary>Fix now: credentials are exposed or effectively unusable.</summary>
    High,
}
