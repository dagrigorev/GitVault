namespace GitVault.Core.Models;

/// <summary>
/// Metadata about a stored credential. The secret itself is deliberately absent: GitVault
/// only materialises secret bytes on an explicit, per-item reveal request.
/// </summary>
/// <param name="Vault">Backing store the entry lives in.</param>
/// <param name="Target">Native target/key string, e.g. <c>git:https://github.com</c>.</param>
/// <param name="Host">Host extracted from <paramref name="Target"/>.</param>
/// <param name="UserName">Account name stored with the entry.</param>
/// <param name="SecretPresent">True when a non-empty secret exists.</param>
/// <param name="Protocol">Protocol the credential applies to, <c>https</c> or <c>ssh</c>.</param>
/// <param name="LastWriteUtc">Last modification timestamp, when the store exposes one.</param>
/// <param name="OwningClient">Application that appears to own the entry, when identifiable.</param>
/// <param name="IsReadOnly">True when GitVault cannot modify the entry.</param>
public sealed record CredentialEntry(
    VaultKind Vault,
    string Target,
    string Host,
    string UserName,
    bool SecretPresent,
    string Protocol,
    DateTimeOffset? LastWriteUtc,
    string? OwningClient,
    bool IsReadOnly)
{
    /// <summary>True when the secret is stored without encryption and should be flagged in the UI.</summary>
    public bool IsPlaintextStore => Vault is VaultKind.GitCredentialsFile or VaultKind.GcmPlaintext;

    /// <summary>File path backing this entry, for file-based stores.</summary>
    public string? SourcePath { get; init; }
}
