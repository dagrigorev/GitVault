using GitVault.Core.Models;
using GitVault.Localization;

namespace GitVault.App.ViewModels;

/// <summary>
/// Maps domain enums to resource keys. Product names (GitKraken, Sourcetree, …) deliberately
/// have no key: a brand is the same word in every language, and inventing translations for
/// them would be wrong.
/// </summary>
internal static class DisplayNames
{
    /// <summary>Resource key for a configuration scope.</summary>
    /// <param name="scope">Scope to name.</param>
    /// <returns>A resource key.</returns>
    internal static string ScopeKey(GitConfigScope scope) => scope switch
    {
        GitConfigScope.System => Keys.Scope_System,
        GitConfigScope.Global => Keys.Scope_Global,
        GitConfigScope.Local => Keys.Scope_Local,
        GitConfigScope.Worktree => Keys.Scope_Worktree,
        GitConfigScope.Command => Keys.Scope_Command,
        _ => Keys.Scope_Unknown,
    };

    /// <summary>Resource key for a credential store.</summary>
    /// <param name="kind">Vault kind to name.</param>
    /// <returns>A resource key.</returns>
    internal static string VaultKindKey(VaultKind kind) => kind switch
    {
        VaultKind.WindowsCredentialManager => Keys.Vault_WindowsCredentialManager,
        VaultKind.MacKeychain => Keys.Vault_MacKeychain,
        VaultKind.SecretService => Keys.Vault_SecretService,
        VaultKind.KWallet => Keys.Vault_KWallet,
        VaultKind.GitCredentialsFile => Keys.Vault_GitCredentialsFile,
        VaultKind.GcmPlaintext => Keys.Vault_GcmPlaintext,
        VaultKind.GcmDpapi => Keys.Vault_GcmDpapi,
        VaultKind.GcmGpg => Keys.Vault_GcmGpg,
        VaultKind.GitKrakenBox => Keys.Vault_GitKrakenBox,
        VaultKind.GitCredentialCache => Keys.Vault_GitCredentialCache,
        _ => Keys.Vault_Unknown,
    };

    /// <summary>Resource key for an agent kind.</summary>
    /// <param name="kind">Agent kind to name.</param>
    /// <returns>A resource key.</returns>
    internal static string AgentKindKey(AgentKind kind) => kind switch
    {
        AgentKind.OpenSshUnix => Keys.AgentKind_OpenSshUnix,
        AgentKind.OpenSshWindowsPipe => Keys.AgentKind_OpenSshWindowsPipe,
        AgentKind.Pageant => Keys.AgentKind_Pageant,
        AgentKind.GpgAgent => Keys.AgentKind_GpgAgent,
        AgentKind.OnePassword => Keys.AgentKind_OnePassword,
        AgentKind.KeeAgent => Keys.AgentKind_KeeAgent,
        AgentKind.WslRelay => Keys.AgentKind_WslRelay,
        _ => Keys.AgentKind_Unknown,
    };

    /// <summary>Resource key for a private key container format.</summary>
    /// <param name="format">Format to name.</param>
    /// <returns>A resource key.</returns>
    internal static string KeyFormatKey(SshKeyFormat format) => format switch
    {
        SshKeyFormat.OpenSsh => Keys.Format_OpenSsh,
        SshKeyFormat.Pem => Keys.Format_Pem,
        SshKeyFormat.Pkcs8 => Keys.Format_Pkcs8,
        SshKeyFormat.Ppk2 => Keys.Format_Ppk2,
        SshKeyFormat.Ppk3 => Keys.Format_Ppk3,
        SshKeyFormat.PublicOnly => Keys.Format_PublicOnly,
        _ => Keys.Format_Unknown,
    };

    /// <summary>Resource key for a detection confidence level.</summary>
    /// <param name="confidence">Confidence to name.</param>
    /// <returns>A resource key.</returns>
    internal static string ConfidenceKey(DetectionConfidence confidence) => confidence switch
    {
        DetectionConfidence.Certain => Keys.Confidence_Certain,
        DetectionConfidence.Probable => Keys.Confidence_Probable,
        _ => Keys.Confidence_Heuristic,
    };

    /// <summary>
    /// Localized label for an identity source, or the product name verbatim when the source is
    /// a third-party application.
    /// </summary>
    /// <param name="source">Source to name.</param>
    /// <param name="localizer">Localizer to resolve keys with.</param>
    /// <returns>Text to show in the UI.</returns>
    internal static string SourceLabel(IdentitySource source, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);

        var key = source switch
        {
            IdentitySource.GitSystemConfig => Keys.Source_GitSystemConfig,
            IdentitySource.GitGlobalConfig => Keys.Source_GitGlobalConfig,
            IdentitySource.RepoLocal => Keys.Source_RepoLocal,
            IdentitySource.RepoWorktree => Keys.Source_RepoWorktree,
            IdentitySource.GitIncludedFile => Keys.Source_GitIncludedFile,
            IdentitySource.Unknown => Keys.Source_Unknown,
            _ => null,
        };

        return key is null ? source.ToString() : localizer[key];
    }
}
