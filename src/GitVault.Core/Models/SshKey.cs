namespace GitVault.Core.Models;

/// <summary>An SSH key pair discovered on disk (or known only through an agent).</summary>
/// <param name="Id">Stable identifier for the lifetime of a scan result.</param>
/// <param name="PrivatePath">Path to the private key, when one exists on disk.</param>
/// <param name="PublicPath">Path to the <c>.pub</c> file, when one exists.</param>
/// <param name="Algorithm">Public key algorithm family.</param>
/// <param name="BitLength">Key size in bits where meaningful (RSA, DSA, ECDSA).</param>
/// <param name="FingerprintSha256">Canonical OpenSSH fingerprint, <c>SHA256:&lt;base64 no padding&gt;</c>.</param>
/// <param name="FingerprintMd5">Legacy colon-separated MD5 fingerprint, for PuTTY/TortoiseGit parity.</param>
/// <param name="Comment">Key comment, when the format carries one.</param>
/// <param name="Format">On-disk container format.</param>
/// <param name="IsEncrypted">True when the private key is passphrase protected.</param>
/// <param name="KdfRounds">KDF work factor when the container reports one.</param>
/// <param name="IsHardwareBacked">True for <c>sk-*</c> keys or keys only reachable through an agent.</param>
public sealed record SshKey(
    Guid Id,
    string? PrivatePath,
    string? PublicPath,
    SshKeyAlgorithm Algorithm,
    int? BitLength,
    string FingerprintSha256,
    string FingerprintMd5,
    string? Comment,
    SshKeyFormat Format,
    bool IsEncrypted,
    int? KdfRounds,
    bool IsHardwareBacked)
{
    /// <summary>Agents currently holding this key.</summary>
    public IReadOnlyList<AgentRef> LoadedInAgents { get; init; } = [];

    /// <summary>File permission details of the private key, used for the 0600 check.</summary>
    public FilePermissionInfo? Permissions { get; init; }

    /// <summary>Health findings for this key.</summary>
    public IReadOnlyList<KeyWarning> Warnings { get; init; } = [];

    /// <summary>The raw public key blob (SSH wire format), when known.</summary>
    public IReadOnlyList<byte> PublicKeyBlob { get; init; } = [];

    /// <summary>True when GitVault only knows this key through an agent, not from disk.</summary>
    public bool IsAgentOnly => PrivatePath is null && PublicPath is null;
}

/// <summary>A pointer to an agent that holds a key.</summary>
/// <param name="Kind">Agent kind.</param>
/// <param name="Endpoint">Socket path, pipe name or handle description.</param>
public sealed record AgentRef(AgentKind Kind, string Endpoint);

/// <summary>A single health finding about a key, config file or credential.</summary>
/// <param name="Code">Stable machine-readable code, also used as the localization key suffix.</param>
/// <param name="Severity">How urgent the finding is.</param>
/// <param name="Subject">Path or object the finding is about.</param>
/// <param name="IsAutoFixable">True when GitVault can remediate without extra user input.</param>
public sealed record KeyWarning(string Code, WarningSeverity Severity, string Subject, bool IsAutoFixable = false);

/// <summary>File permission and ownership snapshot, normalised across platforms.</summary>
/// <param name="Path">The file the information describes.</param>
/// <param name="PosixMode">POSIX mode bits (for example <c>0x180</c> for 0600), null on Windows.</param>
/// <param name="Owner">Owner name or SID/UID as text.</param>
/// <param name="IsWorldReadable">True when principals other than the owner can read the file.</param>
/// <param name="IsGroupReadable">True when the owning group can read the file.</param>
public sealed record FilePermissionInfo(
    string Path,
    int? PosixMode,
    string? Owner,
    bool IsWorldReadable,
    bool IsGroupReadable)
{
    /// <summary>Renders the POSIX mode as an octal string such as <c>0600</c>, or <c>null</c> on Windows.</summary>
    /// <returns>Four-digit octal mode, or <c>null</c>.</returns>
    public string? ToOctal() => PosixMode is null
        ? null
        : "0" + Convert.ToString(PosixMode.Value & 0x1FF, 8).PadLeft(3, '0');
}
