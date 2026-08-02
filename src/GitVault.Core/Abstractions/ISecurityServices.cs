using GitVault.Core.Models;

namespace GitVault.Core.Abstractions;

/// <summary>Redacts secrets from text before it reaches a log sink or a diagnostics bundle.</summary>
public interface ISecretRedactor
{
    /// <summary>Replaces anything that looks like a secret with a fixed placeholder.</summary>
    /// <param name="input">Text to scrub. May be null.</param>
    /// <returns>Scrubbed text; null input returns an empty string.</returns>
    string Redact(string? input);

    /// <summary>True when <paramref name="input"/> contains something that looks like a secret.</summary>
    /// <param name="input">Text to inspect.</param>
    /// <returns><see langword="true"/> when a redaction would change the text.</returns>
    bool ContainsSecret(string? input);
}

/// <summary>Reads and writes credential metadata in one platform vault.</summary>
public interface ICredentialVault
{
    /// <summary>Which store this instance talks to.</summary>
    VaultKind Kind { get; }

    /// <summary>True when the vault is present and reachable on this machine.</summary>
    bool IsAvailable { get; }

    /// <summary>True when the vault refuses writes from GitVault.</summary>
    bool IsReadOnly { get; }

    /// <summary>Lists entry metadata. Never returns secret bytes.</summary>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    /// <returns>Entry metadata.</returns>
    Task<IReadOnlyList<CredentialEntry>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reads the secret for one entry. Callers must zero the returned buffer when done.
    /// </summary>
    /// <param name="target">Native target string of the entry.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The secret bytes, or null when absent.</returns>
    Task<byte[]?> RevealAsync(string target, CancellationToken cancellationToken);

    /// <summary>Creates or replaces an entry.</summary>
    /// <param name="entry">Metadata describing the entry.</param>
    /// <param name="secret">Secret bytes; the caller retains ownership and must zero them.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the entry has been written.</returns>
    Task WriteAsync(CredentialEntry entry, ReadOnlyMemory<byte> secret, CancellationToken cancellationToken);

    /// <summary>Deletes an entry.</summary>
    /// <param name="target">Native target string of the entry.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns><see langword="true"/> when an entry was removed.</returns>
    Task<bool> DeleteAsync(string target, CancellationToken cancellationToken);
}

/// <summary>Talks to one SSH agent endpoint using the agent wire protocol.</summary>
public interface ISshAgent
{
    /// <summary>Describes the endpoint without contacting it.</summary>
    SshAgentInfo Descriptor { get; }

    /// <summary>Lists the identities the agent currently holds.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Identities held by the agent.</returns>
    Task<IReadOnlyList<AgentKeyEntry>> ListIdentitiesAsync(CancellationToken cancellationToken);

    /// <summary>Adds a private key to the agent.</summary>
    /// <param name="privateKeyBlob">Decoded private key in agent wire format.</param>
    /// <param name="comment">Comment to attach.</param>
    /// <param name="lifetimeSeconds">Optional lifetime constraint.</param>
    /// <param name="requireConfirmation">When true, requests the confirm constraint.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns><see langword="true"/> when the agent accepted the key.</returns>
    Task<bool> AddIdentityAsync(
        ReadOnlyMemory<byte> privateKeyBlob,
        string comment,
        int? lifetimeSeconds,
        bool requireConfirmation,
        CancellationToken cancellationToken);

    /// <summary>Removes one identity by its public key blob.</summary>
    /// <param name="publicKeyBlob">Public key blob to remove.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns><see langword="true"/> when the agent removed the key.</returns>
    Task<bool> RemoveIdentityAsync(ReadOnlyMemory<byte> publicKeyBlob, CancellationToken cancellationToken);

    /// <summary>Removes every identity the agent holds.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns><see langword="true"/> when the agent acknowledged.</returns>
    Task<bool> RemoveAllIdentitiesAsync(CancellationToken cancellationToken);

    /// <summary>Locks or unlocks the agent with a passphrase.</summary>
    /// <param name="passphrase">Passphrase bytes; the caller must zero them afterwards.</param>
    /// <param name="lock">True to lock, false to unlock.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns><see langword="true"/> when the agent acknowledged.</returns>
    Task<bool> SetLockedAsync(ReadOnlyMemory<byte> passphrase, bool @lock, CancellationToken cancellationToken);
}
