namespace GitVault.Core.Models;

/// <summary>
/// A Git author identity (name / e-mail / signing key) as observed in one particular place.
/// </summary>
/// <param name="Id">Stable identifier for the lifetime of a scan result.</param>
/// <param name="DisplayName">Human-friendly label, usually <c>"Name &lt;email&gt;"</c>.</param>
/// <param name="UserName">Value of <c>user.name</c>.</param>
/// <param name="Email">Value of <c>user.email</c>.</param>
/// <param name="SigningKeyId">Value of <c>user.signingkey</c>, GPG key id or SSH key path.</param>
/// <param name="Source">Which store this identity came from.</param>
/// <param name="SourcePath">File path or registry key it was read from.</param>
/// <param name="Hosts">Hosts this identity is associated with, if any.</param>
/// <param name="Confidence">How authoritative the reading is.</param>
public sealed record GitIdentity(
    Guid Id,
    string DisplayName,
    string UserName,
    string Email,
    string? SigningKeyId,
    IdentitySource Source,
    string SourcePath,
    IReadOnlyList<string> Hosts,
    DetectionConfidence Confidence)
{
    /// <summary>
    /// Every source this identity was seen in. Populated by the deduplicating merge step;
    /// always contains at least <see cref="Source"/>.
    /// </summary>
    public IReadOnlyList<IdentityOccurrence> Occurrences { get; init; } = [];

    /// <summary>Case-insensitive key used to deduplicate identities across sources.</summary>
    public IdentityKey Key => new(UserName, Email);

    /// <summary>Creates an identity with a fresh id and a single occurrence.</summary>
    /// <param name="userName">Value of <c>user.name</c>.</param>
    /// <param name="email">Value of <c>user.email</c>.</param>
    /// <param name="source">Store the identity came from.</param>
    /// <param name="sourcePath">Path or registry key it was read from.</param>
    /// <param name="signingKeyId">Optional <c>user.signingkey</c> value.</param>
    /// <param name="hosts">Optional associated hosts.</param>
    /// <param name="confidence">How authoritative the reading is.</param>
    /// <returns>A new identity instance.</returns>
    public static GitIdentity Create(
        string userName,
        string email,
        IdentitySource source,
        string sourcePath,
        string? signingKeyId = null,
        IReadOnlyList<string>? hosts = null,
        DetectionConfidence confidence = DetectionConfidence.Certain)
    {
        var display = string.IsNullOrWhiteSpace(userName)
            ? email
            : string.IsNullOrWhiteSpace(email) ? userName : $"{userName} <{email}>";

        return new GitIdentity(
            Guid.NewGuid(),
            display,
            userName,
            email,
            signingKeyId,
            source,
            sourcePath,
            hosts ?? [],
            confidence)
        {
            Occurrences = [new IdentityOccurrence(source, sourcePath, confidence)],
        };
    }
}

/// <summary>One place an identity was observed.</summary>
/// <param name="Source">Store kind.</param>
/// <param name="Path">File path or registry key.</param>
/// <param name="Confidence">How authoritative that particular reading is.</param>
public sealed record IdentityOccurrence(IdentitySource Source, string Path, DetectionConfidence Confidence);

/// <summary>Deduplication key for identities. Comparison is case-insensitive and invariant.</summary>
/// <param name="UserName">Value of <c>user.name</c>.</param>
/// <param name="Email">Value of <c>user.email</c>.</param>
public readonly record struct IdentityKey(string UserName, string Email)
{
    /// <inheritdoc/>
    public bool Equals(IdentityKey other) =>
        string.Equals(UserName, other.UserName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Email, other.Email, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(
        UserName.ToUpperInvariant(),
        Email.ToUpperInvariant());
}
