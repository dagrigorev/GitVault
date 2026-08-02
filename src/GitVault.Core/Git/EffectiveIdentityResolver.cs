using GitVault.Core.Abstractions;
using GitVault.Core.Models;

namespace GitVault.Core.Git;

/// <summary>The winning value of one configuration key, with the scope that produced it.</summary>
/// <param name="Key">Configuration key.</param>
/// <param name="Value">Winning value, or null when the key is unset everywhere.</param>
/// <param name="Scope">Scope the winning value came from.</param>
/// <param name="Origin">Origin string of the winning value.</param>
/// <param name="OverriddenIn">Scopes that also set this key but lost.</param>
public sealed record ResolvedSetting(
    string Key,
    string? Value,
    GitConfigScope Scope,
    string? Origin,
    IReadOnlyList<GitConfigScope> OverriddenIn)
{
    /// <summary>True when the key is set somewhere.</summary>
    public bool IsSet => Value is not null;
}

/// <summary>The identity and credential wiring in effect at a given path.</summary>
/// <param name="RepositoryPath">Path the answer applies to, or null for the user's context.</param>
public sealed record EffectiveIdentity(string? RepositoryPath)
{
    /// <summary>Resolved <c>user.name</c>.</summary>
    public required ResolvedSetting UserName { get; init; }

    /// <summary>Resolved <c>user.email</c>.</summary>
    public required ResolvedSetting Email { get; init; }

    /// <summary>Resolved <c>user.signingkey</c>.</summary>
    public required ResolvedSetting SigningKey { get; init; }

    /// <summary>Resolved <c>credential.helper</c>.</summary>
    public required ResolvedSetting CredentialHelper { get; init; }

    /// <summary>Resolved <c>core.sshcommand</c>.</summary>
    public required ResolvedSetting SshCommand { get; init; }

    /// <summary>Every resolved setting, in display order.</summary>
    public IReadOnlyList<ResolvedSetting> All => [UserName, Email, SigningKey, CredentialHelper, SshCommand];

    /// <summary>True when both name and e-mail are set, i.e. commits will be attributable.</summary>
    public bool IsComplete => UserName.IsSet && Email.IsSet;
}

/// <summary>Answers "which identity is active here?" for a repository or for the user's context.</summary>
public interface IEffectiveIdentityResolver
{
    /// <summary>Resolves the identity in effect at a path.</summary>
    /// <param name="repositoryPath">Repository to resolve for, or null for the user's context.</param>
    /// <param name="cancellationToken">Cancels the resolution.</param>
    /// <returns>The winning values and the scopes they came from.</returns>
    Task<EffectiveIdentity> ResolveAsync(string? repositoryPath, CancellationToken cancellationToken);
}

/// <summary>Resolves effective settings from a full configuration listing.</summary>
public sealed class EffectiveIdentityResolver : IEffectiveIdentityResolver
{
    private const string UserNameKey = "user.name";
    private const string EmailKey = "user.email";
    private const string SigningKeyKey = "user.signingkey";
    private const string CredentialHelperKey = "credential.helper";
    private const string SshCommandKey = "core.sshcommand";

    private readonly IGitConfigService _config;

    /// <summary>Creates the resolver.</summary>
    /// <param name="config">Configuration service to read from.</param>
    public EffectiveIdentityResolver(IGitConfigService config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
    }

    /// <inheritdoc/>
    public async Task<EffectiveIdentity> ResolveAsync(string? repositoryPath, CancellationToken cancellationToken)
    {
        var all = await _config.ListAsync(repositoryPath, cancellationToken).ConfigureAwait(false);

        return new EffectiveIdentity(repositoryPath)
        {
            UserName = Resolve(all, UserNameKey),
            Email = Resolve(all, EmailKey),
            SigningKey = Resolve(all, SigningKeyKey),
            CredentialHelper = Resolve(all, CredentialHelperKey),
            SshCommand = Resolve(all, SshCommandKey),
        };
    }

    /// <summary>Picks the winning value for a key from a full listing.</summary>
    /// <param name="all">Every visible entry, lowest precedence first.</param>
    /// <param name="key">Key to resolve.</param>
    /// <returns>The winning value and the scopes it overrode.</returns>
    internal static ResolvedSetting Resolve(IReadOnlyList<GitConfigValue> all, string key)
    {
        GitConfigValue? winner = null;
        var overridden = new List<GitConfigScope>();

        foreach (var entry in all)
        {
            if (!string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (winner is not null && winner.Scope != entry.Scope)
            {
                overridden.Add(winner.Scope);
            }

            winner = entry;
        }

        return winner is null
            ? new ResolvedSetting(key, null, GitConfigScope.Unknown, null, [])
            : new ResolvedSetting(key, winner.Value, winner.Scope, winner.Origin, overridden);
    }
}
