using GitVault.App.Logging;
using GitVault.App.Services;
using GitVault.Clients;
using GitVault.App.ViewModels;
using GitVault.Core.Abstractions;
using GitVault.Core.Credentials;
using GitVault.Core.Diagnostics;
using GitVault.Core.Discovery;
using GitVault.Core.Git;
using GitVault.Core.Platform;
using GitVault.Core.Profiles;
using GitVault.Core.Repository;
using GitVault.Core.Ssh;
using GitVault.Core.Ssh.Agent;
using GitVault.Core.Security;
using GitVault.Core.Settings;
using GitVault.Localization;
using Microsoft.Extensions.DependencyInjection;

namespace GitVault.App.Composition;

/// <summary>Registers every GitVault service. This is the only composition root.</summary>
internal static class ServiceCollectionExtensions
{
    /// <summary>Registers core services, localization and the view models.</summary>
    /// <param name="services">Service collection to add to.</param>
    /// <returns>The same collection, for chaining.</returns>
    internal static IServiceCollection AddGitVault(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddPlatformServices();

        services.AddSingleton<ISecretRedactor, SecretRedactor>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<InMemoryLogSink>();

        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IGitBinaryLocator, GitBinaryLocator>();
        services.AddSingleton<GitConfigService>();
        services.AddSingleton<IGitConfigService>(sp => sp.GetRequiredService<GitConfigService>());
        services.AddSingleton<IEffectiveIdentityResolver, EffectiveIdentityResolver>();

        services.AddSingleton<ISshToolLocator, SshToolLocator>();
        services.AddSingleton<ISshKeyScanner, SshKeyScanner>();
        services.AddSingleton<ISshKeyGenerator, SshKeyGenerator>();

        services.AddSingleton<ISshAgentTransportFactory, PortableAgentTransportFactory>();
        services.AddSingleton<IAgentKeyLoader, AgentKeyLoader>();

        services.AddSingleton<IGitCredentialHelperClient, GitCredentialHelperClient>();
        services.AddSingleton<ICredentialVault, GitCredentialsFileVault>();
        services.AddSingleton<ICredentialVault, GcmPlaintextVault>();

        services.AddSingleton<IProbe, GitIdentityProbe>();
        services.AddSingleton<IProbe, SshKeyProbe>();
        services.AddSingleton<IProbe, SshAgentProbe>();
        services.AddSingleton<IProbe, CredentialProbe>();
        services.AddClientProbes();

        services.AddSingleton<IProfileStore, ProfileStore>();
        services.AddSingleton<ISnapshotService, SnapshotService>();
        services.AddSingleton<IActivationStateStore, ActivationStateStore>();
        services.AddSingleton<IProfileActivator, ProfileActivator>();
        services.AddSingleton<IRepositoryScanner, RepositoryScanner>();
        services.AddSingleton<IConfigEditor, ConfigEditor>();
        services.AddSingleton<IProjectSettingsStore, ProjectSettingsStore>();
        services.AddSingleton<IGitCommandRunner, GitCommandRunner>();
        services.AddSingleton<IRepositoryInspector, RepositoryInspector>();
        services.AddSingleton<IRefBackupService, RefBackupService>();
        services.AddSingleton<IRepositoryPlanApplier, RepositoryPlanApplier>();
        services.AddSingleton<IGitObjectEditor, GitObjectEditor>();
        services.AddSingleton<ICommitReader, CommitReader>();
        services.AddSingleton<IContentMerger, ContentMerger>();
        services.AddSingleton<IFileContentReader, FileContentReader>();
        services.AddSingleton<ITreeBuilder, TreeBuilder>();
        services.AddSingleton<IHistoryRewriter, HistoryRewriter>();
        services.AddSingleton<IHistoryTools, HistoryTools>();
        services.AddSingleton<IRepositoryFileEditor, RepositoryFileEditor>();
        services.AddSingleton<IHookEditor, HookEditor>();
        services.AddSingleton<IWorktreeEditor, WorktreeEditor>();
        services.AddSingleton<IStashEditor, StashEditor>();
        services.AddSingleton<ISubmoduleEditor, SubmoduleEditor>();
        services.AddSingleton<IDiagnosticsBundleBuilder, DiagnosticsBundleBuilder>();
        services.AddSingleton<IDiscoveryOrchestrator, DiscoveryOrchestrator>();
        services.AddSingleton<ScanCoordinator>();

        services.AddSingleton<ClipboardService>();
        services.AddSingleton<IClipboardService>(sp => sp.GetRequiredService<ClipboardService>());
        services.AddSingleton<StatusService>();
        services.AddSingleton<RepositoryContext>();
        services.AddSingleton<IDialogService, DialogService>();

        services.AddSingleton<IPluralizer, CldrPluralizer>();
        services.AddSingleton<ILocalizationService, LocalizationService>();
        services.AddSingleton<Localizer>();

        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<IdentitiesViewModel>();
        services.AddSingleton<SshKeysViewModel>();
        services.AddSingleton<AgentsViewModel>();
        services.AddSingleton<CredentialsViewModel>();
        services.AddSingleton<ClientsViewModel>();
        services.AddSingleton<ProfilesViewModel>();
        services.AddSingleton<RepositoriesViewModel>();
        services.AddSingleton<SnapshotsViewModel>();
        services.AddSingleton<RepositoryConfigViewModel>();
        services.AddSingleton<ProjectSettingsViewModel>();
        services.AddSingleton<RemotesViewModel>();
        services.AddSingleton<BranchesViewModel>();
        services.AddSingleton<TagsViewModel>();
        services.AddSingleton<CommitsViewModel>();
        services.AddSingleton<HistoryToolsViewModel>();
        services.AddSingleton<RepositoryFilesViewModel>();
        services.AddSingleton<HooksViewModel>();
        services.AddSingleton<WorktreesViewModel>();
        services.AddSingleton<StashesViewModel>();
        services.AddSingleton<SubmodulesViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<LogsViewModel>();

        // Tree order. Registering the sequence explicitly keeps navigation deterministic
        // instead of depending on registration order inside the container.
        services.AddSingleton<IEnumerable<PageViewModel>>(sp =>
        [
            sp.GetRequiredService<DashboardViewModel>(),
            sp.GetRequiredService<IdentitiesViewModel>(),
            sp.GetRequiredService<SshKeysViewModel>(),
            sp.GetRequiredService<AgentsViewModel>(),
            sp.GetRequiredService<CredentialsViewModel>(),
            sp.GetRequiredService<ClientsViewModel>(),
            sp.GetRequiredService<ProfilesViewModel>(),
            sp.GetRequiredService<RepositoriesViewModel>(),
            sp.GetRequiredService<SnapshotsViewModel>(),
            sp.GetRequiredService<LogsViewModel>(),
            sp.GetRequiredService<SettingsViewModel>(),
        ]);

        services.AddSingleton<MainWindowViewModel>();

        return services;
    }

    /// <summary>
    /// Creates the app data directories and applies the persisted language, so the first
    /// window that opens is already in the user's chosen culture.
    /// </summary>
    /// <param name="provider">Built service provider.</param>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <returns>A task that completes once settings are loaded and applied.</returns>
    internal static async Task InitializeGitVaultAsync(
        this IServiceProvider provider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);

        provider.GetRequiredService<PlatformPathsBase>().EnsureAppDirectories();

        var settings = await provider.GetRequiredService<ISettingsService>()
            .LoadAsync(cancellationToken).ConfigureAwait(false);

        provider.GetRequiredService<ILocalizationService>()
            .SetCulture(LocalizationService.ResolveSupported(settings.Language));

        // Locating git once at startup keeps the first page render free of a process launch.
        await provider.GetRequiredService<GitConfigService>()
            .InitializeAsync(cancellationToken).ConfigureAwait(false);
    }
}
