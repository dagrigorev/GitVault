using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitVault.Core.Git;
using GitVault.Core.Profiles;
using GitVault.Core.Settings;
using GitVault.Localization;

namespace GitVault.App.ViewModels;

/// <summary>One repository found under a scan root.</summary>
internal sealed partial class RepositoryRow(Localizer localizer, DiscoveredRepository repository) : ObservableObject
{
    [ObservableProperty]
    private string _effectiveIdentity = string.Empty;

    /// <summary>Bindable localizer.</summary>
    public Localizer L { get; } = localizer;

    /// <summary>The repository this row describes.</summary>
    public DiscoveredRepository Repository { get; } = repository;

    /// <summary>Directory name.</summary>
    public string Name => Repository.Name;

    /// <summary>Working tree path, shown verbatim.</summary>
    public string Path => Repository.Path;

    /// <summary>First remote URL, or an empty cell.</summary>
    public string RemoteUrl => Repository.RemoteUrl ?? string.Empty;

    /// <summary>Re-reads the localized members. Called when the culture changes.</summary>
    internal void RefreshCaptions() =>
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(string.Empty));
}

/// <summary>
/// Repositories under the user's scan roots, each showing the identity actually in effect there.
/// </summary>
/// <remarks>
/// The effective identity is resolved per repository rather than assumed from the global config,
/// because that is the whole question this page answers: "which identity will this repository
/// commit as?"
/// </remarks>
internal sealed partial class RepositoriesViewModel : ListPageViewModel
{
    /// <summary>How far below a scan root to look for working trees.</summary>
    public const int ScanDepth = 4;

    private readonly IRepositoryScanner _scanner;
    private readonly IEffectiveIdentityResolver _resolver;
    private readonly ISettingsService _settings;

    [ObservableProperty]
    private RepositoryRow? _selectedRepository;

    [ObservableProperty]
    private bool _isScanning;

    public RepositoriesViewModel(
        Localizer localizer,
        IRepositoryScanner scanner,
        IEffectiveIdentityResolver resolver,
        ISettingsService settings)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(settings);

        _scanner = scanner;
        _resolver = resolver;
        _settings = settings;
    }

    public override string NavKey => Keys.Nav_Repositories;

    public override string TitleKey => Keys.Repositories_Title;

    /// <inheritdoc/>
    public override string IconKey => "IconRepositories";

    public override string EmptyKey => Keys.Repositories_Empty;

    /// <inheritdoc/>
    public override bool IsEmpty => Rows.Count == 0;

    /// <summary>Repositories found.</summary>
    public ObservableCollection<RepositoryRow> Rows { get; } = [];

    /// <summary>True when the user has not chosen anywhere to search.</summary>
    public bool HasNoRoots => _settings.Current.RepositoryScanRoots.Count == 0;

    /// <summary>Localized prompt shown when there is nowhere to search.</summary>
    public string NoRootsCaption => L[Keys.Repositories_NoRoots];

    /// <inheritdoc/>
    public override Task OnActivatedAsync(CancellationToken cancellationToken) => ScanAsync(cancellationToken);

    /// <summary>Walks the configured roots and resolves each repository's identity.</summary>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>A task that completes once the list is filled.</returns>
    [RelayCommand]
    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        var roots = _settings.Current.RepositoryScanRoots;
        OnPropertyChanged(nameof(HasNoRoots));

        if (roots.Count == 0)
        {
            return;
        }

        IsScanning = true;
        try
        {
            var found = await _scanner.ScanAsync(roots, ScanDepth, cancellationToken).ConfigureAwait(true);

            Rows.Clear();
            foreach (var repository in found)
            {
                Rows.Add(new RepositoryRow(L, repository));
            }

            SelectedRepository = Rows.FirstOrDefault();
            OnPropertyChanged(nameof(IsEmpty));

            // Resolving runs git once per repository, so it happens after the list is on screen
            // rather than holding it back.
            foreach (var row in Rows)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var effective = await _resolver
                    .ResolveAsync(row.Repository.Path, cancellationToken)
                    .ConfigureAwait(true);

                row.EffectiveIdentity = effective.IsComplete
                    ? L.Format(Keys.Identities_NameAndEmail, effective.UserName.Value, effective.Email.Value)
                    : L[Keys.Identities_Effective_Unset];
            }
        }
        catch (OperationCanceledException)
        {
            // Navigating away mid-scan is normal.
        }
        finally
        {
            IsScanning = false;
        }
    }

    /// <inheritdoc/>
    protected override void OnCultureChanged()
    {
        foreach (var row in Rows)
        {
            row.RefreshCaptions();
        }
    }
}
