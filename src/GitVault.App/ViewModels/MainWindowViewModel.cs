using System.Collections.ObjectModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitVault.App.Services;
using GitVault.Core.Models;
using GitVault.Localization;

namespace GitVault.App.ViewModels;

/// <summary>
/// Shell view model: owns the navigation tree, the current page, the toolbar and the status bar.
/// </summary>
/// <remarks>
/// The toolbar's Preview, Apply and Rollback are the profile and snapshot commands rather than
/// copies of them. Duplicating that logic here would mean two paths to a mutation, and one of
/// them would eventually stop enforcing the review gate.
/// </remarks>
internal sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly ScanCoordinator _scans;
    private readonly StatusService _status;
    private readonly IDialogService _dialogs;
    private readonly IClipboardService _clipboard;
    private readonly ProfilesViewModel _profiles;
    private readonly SnapshotsViewModel _snapshots;
    private readonly SettingsViewModel _settings;
    private readonly RepositoriesViewModel _repositories;
    private readonly RepositoryContext _repositoryContext;
    private readonly RepositoryConfigViewModel _repositoryConfig;
    private readonly ProjectSettingsViewModel _projectSettings;
    private readonly RemotesViewModel _remotes;
    private readonly BranchesViewModel _branches;
    private readonly TagsViewModel _tags;
    private readonly CommitsViewModel _commits;
    private readonly HistoryToolsViewModel _historyTools;
    private readonly HashSet<PageViewModel> _activated = [];

    [ObservableProperty]
    private PageViewModel? _selectedPage;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isPropertiesPaneVisible = true;

    [ObservableProperty]
    private NavigationNode? _selectedNode;

    public MainWindowViewModel(
        Localizer localizer,
        IEnumerable<PageViewModel> pages,
        ScanCoordinator scans,
        StatusService status,
        IDialogService dialogs,
        IClipboardService clipboard,
        ProfilesViewModel profiles,
        SnapshotsViewModel snapshots,
        SettingsViewModel settings,
        RepositoriesViewModel repositories,
        RepositoryContext repositoryContext,
        RepositoryConfigViewModel repositoryConfig,
        ProjectSettingsViewModel projectSettings,
        RemotesViewModel remotes,
        BranchesViewModel branches,
        TagsViewModel tags,
        CommitsViewModel commits,
        HistoryToolsViewModel historyTools)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(commits);
        ArgumentNullException.ThrowIfNull(remotes);
        ArgumentNullException.ThrowIfNull(branches);
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentNullException.ThrowIfNull(repositories);
        ArgumentNullException.ThrowIfNull(repositoryContext);
        ArgumentNullException.ThrowIfNull(repositoryConfig);
        ArgumentNullException.ThrowIfNull(projectSettings);
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(scans);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(clipboard);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(settings);

        _scans = scans;
        _status = status;
        _dialogs = dialogs;
        _clipboard = clipboard;
        _profiles = profiles;
        _snapshots = snapshots;
        _settings = settings;
        _repositories = repositories;
        _repositoryContext = repositoryContext;
        _repositoryConfig = repositoryConfig;
        _projectSettings = projectSettings;
        _remotes = remotes;
        _branches = branches;
        _tags = tags;
        _commits = commits;
        _historyTools = historyTools;

        _repositories.Rows.CollectionChanged += OnRepositoriesChanged;
        _scans.PropertyChanged += OnScansPropertyChanged;
        _scans.ScanCompleted += OnScanCompleted;
        _status.PropertyChanged += OnStatusPropertyChanged;
        _profiles.PropertyChanged += OnProfilesPropertyChanged;

        foreach (var page in pages)
        {
            Pages.Add(page);
        }

        var root = new NavigationNode(localizer, Keys.Nav_ThisComputer, "IconComputer");
        foreach (var page in Pages)
        {
            root.Children.Add(new NavigationNode(localizer, page));
        }

        RootNodes.Add(root);

        SelectedPage = Pages.FirstOrDefault();
        _selectedNode = root.Children.FirstOrDefault();
    }

    /// <summary>
    /// Rebuilds the repository subtree from the repositories page's current list.
    /// </summary>
    /// <remarks>
    /// One node per repository, and beneath it the pages that only make sense in a repository's
    /// context. Those pages are shared instances rather than one set per repository: a machine
    /// with three hundred repositories would otherwise pay for six hundred view models to render
    /// one of them.
    /// </remarks>
    internal void RebuildRepositoryNodes()
    {
        var parent = RootNodes
            .SelectMany(r => r.Children)
            .FirstOrDefault(n => n.Page is RepositoriesViewModel);

        if (parent is null)
        {
            return;
        }

        var selectedPath = SelectedNode?.RepositoryPath;

        parent.Children.Clear();
        foreach (var row in _repositories.Rows)
        {
            var node = NavigationNode.ForRepository(L, row.Name, row.Path);

            node.Children.Add(NavigationNode.ForRepositoryPage(L, _commits, row.Path));
            node.Children.Add(NavigationNode.ForRepositoryPage(L, _remotes, row.Path));
            node.Children.Add(NavigationNode.ForRepositoryPage(L, _branches, row.Path));
            node.Children.Add(NavigationNode.ForRepositoryPage(L, _tags, row.Path));
            node.Children.Add(NavigationNode.ForRepositoryPage(L, _repositoryConfig, row.Path));
            node.Children.Add(NavigationNode.ForRepositoryPage(L, _projectSettings, row.Path));
            node.Children.Add(NavigationNode.ForRepositoryPage(L, _historyTools, row.Path));

            parent.Children.Add(node);
        }

        // A rebuild must not silently move the user somewhere else.
        if (selectedPath is not null)
        {
            var again = parent.Children
                .SelectMany(n => n.Children.Prepend(n))
                .FirstOrDefault(n => n.RepositoryPath == selectedPath && n.Page == SelectedNode?.Page);

            if (again is not null)
            {
                SelectedNode = again;
            }
        }
    }

    private void OnRepositoriesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        _ = e;
        Avalonia.Threading.Dispatcher.UIThread.Post(RebuildRepositoryNodes);
    }

    /// <summary>Raised when the user picks File &gt; Exit.</summary>
    internal event EventHandler? ExitRequested;

    /// <summary>Navigable pages, in tree order.</summary>
    public ObservableCollection<PageViewModel> Pages { get; } = [];

    /// <summary>The navigation tree's roots. There is exactly one: this computer.</summary>
    public ObservableCollection<NavigationNode> RootNodes { get; } = [];

    /// <summary>Window title. The product name is not localized; the subtitle and separator are.</summary>
    public string WindowTitle => L.Format(Keys.App_WindowTitle, L[Keys.App_Title], L[Keys.App_Subtitle]);

    /// <summary>Caption of the tree's root node.</summary>
    public string RootCaption => L[Keys.Nav_ThisComputer];

    /// <summary>Localized placeholder for the search box.</summary>
    public string SearchPlaceholder => L[Keys.Common_Search];

    /// <summary>True while a scan is running.</summary>
    public bool IsScanning => _scans.IsScanning;

    /// <summary>True when a scan is not running, so the toolbar can bind without a converter.</summary>
    public bool IsNotScanning => !_scans.IsScanning;

    /// <summary>What the status bar is saying.</summary>
    public string StatusMessage => _status.Message;

    /// <summary>Localized count of everything the last scan found.</summary>
    public string ArtifactsCaption => L.Format(Keys.Status_Artifacts, ArtifactCount);

    /// <summary>
    /// Localized description of what GitVault is allowed to do right now. Discovery is read-only;
    /// this says so permanently rather than only in the documentation.
    /// </summary>
    public string ModeCaption => _profiles.HasReviewedPlan
        ? L[Keys.Status_Mode_PendingWrite]
        : L[Keys.Status_Mode_ReadOnly];

    /// <summary>The active culture's name, shown in the status bar the way a classic app does.</summary>
    public string LanguageCaption => L.Service.CurrentCulture.Name;

    /// <summary>Localized summary of the last scan, shown under the navigation tree.</summary>
    public string SidebarStatusCaption => _scans.HasScanned
        ? L[Keys.Status_ReadOnlyScanComplete]
        : L[Keys.Status_NoScanYet];

    /// <summary>Localized note about whether a write is pending.</summary>
    public string PendingWritesCaption => _profiles.HasReviewedPlan
        ? L[Keys.Status_PendingWrite]
        : L[Keys.Status_NoPendingWrites];

    /// <summary>True when nothing is waiting to be written.</summary>
    public bool HasNoPendingWrites => !_profiles.HasReviewedPlan;

    /// <summary>True when Edit &gt; Copy applies right now.</summary>
    public bool CanCopySelection => SelectedPage?.CanCopySelection == true;

    /// <summary>True once a plan has been previewed and reviewed, so Apply may run.</summary>
    public bool CanApply => _profiles.CanApply;

    /// <summary>True when there is a snapshot that can be rolled back.</summary>
    public bool CanRollback => _snapshots.HasSnapshots;

    /// <summary>Everything the last scan found, added up.</summary>
    private int ArtifactCount =>
        _scans.Report.Identities.Count
        + _scans.Report.Keys.Count
        + _scans.Report.Agents.Count
        + _scans.Report.Credentials.Count
        + _scans.Report.Clients.Count;

    /// <summary>Runs a full rescan. Bound to the toolbar, F5 and two menu entries.</summary>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>A task that completes when the scan has finished.</returns>
    [RelayCommand]
    public async Task RescanAsync(CancellationToken cancellationToken)
    {
        _status.Report(StatusKind.Busy, Keys.Status_Scanning);
        await _scans.RescanAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Navigates to a page by its type.</summary>
    /// <typeparam name="T">Page to show.</typeparam>
    internal void Navigate<T>()
        where T : PageViewModel
    {
        var page = Pages.OfType<T>().FirstOrDefault();
        if (page is not null)
        {
            SelectedPage = page;
        }
    }

    /// <summary>Shows the profiles page.</summary>
    [RelayCommand]
    private void OpenProfiles() => Navigate<ProfilesViewModel>();

    /// <summary>Shows the options page.</summary>
    [RelayCommand]
    private void OpenOptions() => Navigate<SettingsViewModel>();

    /// <summary>Shows the snapshots page.</summary>
    [RelayCommand]
    private void OpenSnapshots() => Navigate<SnapshotsViewModel>();

    /// <summary>Shows the logs page.</summary>
    [RelayCommand]
    private void OpenLogs() => Navigate<LogsViewModel>();

    /// <summary>Copies the current page's selection to the clipboard.</summary>
    /// <param name="cancellationToken">Cancels the copy.</param>
    /// <returns>A task that completes once the clipboard has been set.</returns>
    [RelayCommand]
    private async Task CopySelectionAsync(CancellationToken cancellationToken)
    {
        var text = SelectedPage?.BuildClipboardText() ?? string.Empty;
        if (text.Length == 0)
        {
            return;
        }

        // The properties pane never holds a secret, so this is the plain copy that stays on the
        // clipboard rather than the timed one used for credential values.
        if (await _clipboard.CopyAsync(text, cancellationToken).ConfigureAwait(true))
        {
            _status.Report(StatusKind.Done, Keys.Status_Copied);
        }
    }

    /// <summary>Opens the properties of the current selection in a dialog.</summary>
    /// <returns>A task that completes once the dialog closes.</returns>
    [RelayCommand]
    private async Task ShowPropertiesAsync()
    {
        if (SelectedPage is not { Properties.Count: > 0 } page)
        {
            return;
        }

        var dialog = new PropertiesDialogViewModel(L, page.Title, page.Properties);
        await _dialogs.ShowAsync(dialog).ConfigureAwait(true);
    }

    /// <summary>Shows or hides the properties pane.</summary>
    [RelayCommand]
    private void TogglePropertiesPane() => IsPropertiesPaneVisible = !IsPropertiesPaneVisible;

    /// <summary>Previews activating the selected profile. Writes nothing.</summary>
    /// <param name="cancellationToken">Cancels the planning.</param>
    /// <returns>A task that completes once the preview closes.</returns>
    [RelayCommand]
    private async Task PreviewActivationAsync(CancellationToken cancellationToken)
    {
        Navigate<ProfilesViewModel>();
        await _profiles.PreviewActivationCommand.ExecuteAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Previews deactivating the selected profile. Writes nothing.</summary>
    /// <param name="cancellationToken">Cancels the planning.</param>
    /// <returns>A task that completes once the preview closes.</returns>
    [RelayCommand]
    private async Task PreviewDeactivationAsync(CancellationToken cancellationToken)
    {
        Navigate<ProfilesViewModel>();
        await _profiles.PreviewDeactivationCommand.ExecuteAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Applies the reviewed plan.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the plan has been applied.</returns>
    [RelayCommand]
    private async Task ApplyAsync(CancellationToken cancellationToken)
    {
        Navigate<ProfilesViewModel>();
        await _profiles.ApplyCommand.ExecuteAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Previews rolling back the most recent snapshot.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the preview closes.</returns>
    [RelayCommand]
    private async Task RollbackAsync(CancellationToken cancellationToken)
    {
        Navigate<SnapshotsViewModel>();
        await _snapshots.RollbackLatestAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Shows the diagnostics bundle preview on the options page.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the preview is on screen.</returns>
    [RelayCommand]
    private async Task ExportDiagnosticsAsync(CancellationToken cancellationToken)
    {
        Navigate<SettingsViewModel>();
        await _settings.PreviewDiagnosticsCommand.ExecuteAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Shows the about box.</summary>
    /// <returns>A task that completes once the dialog closes.</returns>
    [RelayCommand]
    private async Task ShowAboutAsync()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? string.Empty;
        await _dialogs.ShowAsync(new AboutViewModel(L, version)).ConfigureAwait(true);
    }

    /// <summary>Closes the window.</summary>
    [RelayCommand]
    private void Exit() => ExitRequested?.Invoke(this, EventArgs.Empty);

    /// <inheritdoc/>
    protected override void OnCultureChanged()
    {
        OnPropertyChanged(nameof(LanguageCaption));

        foreach (var node in RootNodes)
        {
            node.RefreshCaptions();
        }
    }

    partial void OnSelectedNodeChanged(NavigationNode? value)
    {
        // The repository has to be in place before the page is shown, or the page renders the
        // previous repository's configuration for a frame and then corrects itself.
        if (value?.RepositoryPath is { Length: > 0 } path)
        {
            var name = value.Page is null
                ? value.Caption
                : RootNodes.SelectMany(r => r.Children)
                    .SelectMany(n => n.Children)
                    .FirstOrDefault(n => n.RepositoryPath == path && n.Page is null)?.Caption
                    ?? System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar));

            _repositoryContext.Select(path, name);
        }

        // Selecting the machine itself is not a navigation: the root is a heading, and blanking
        // the workspace because someone clicked it would be a worse answer than doing nothing.
        if (value?.Page is { } page)
        {
            SelectedPage = page;
        }
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _scans.PropertyChanged -= OnScansPropertyChanged;
            _scans.ScanCompleted -= OnScanCompleted;
            _status.PropertyChanged -= OnStatusPropertyChanged;
            _profiles.PropertyChanged -= OnProfilesPropertyChanged;
            _repositories.Rows.CollectionChanged -= OnRepositoriesChanged;

            foreach (var page in Pages)
            {
                page.Dispose();
            }

            // The repository-scoped pages are not in the navigation list, so they are not covered
            // by the loop above.
            _repositoryConfig.Dispose();
            _projectSettings.Dispose();
            _remotes.Dispose();
            _branches.Dispose();
            _tags.Dispose();
            _commits.Dispose();
            _historyTools.Dispose();
        }

        base.Dispose(disposing);
    }

    private void OnScansPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ScanCoordinator.IsScanning))
        {
            OnPropertyChanged(nameof(IsScanning));
            OnPropertyChanged(nameof(IsNotScanning));
        }
    }

    private void OnStatusPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(StatusService.Message))
        {
            OnPropertyChanged(nameof(StatusMessage));
        }
    }

    private void OnProfilesPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProfilesViewModel.CanApply) or nameof(ProfilesViewModel.HasReviewedPlan) or "")
        {
            OnPropertyChanged(nameof(CanApply));
            OnPropertyChanged(nameof(ModeCaption));
            OnPropertyChanged(nameof(PendingWritesCaption));
            OnPropertyChanged(nameof(HasNoPendingWrites));
        }
    }

    private void OnScanCompleted(object? sender, DiscoveryReport report) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyScanResult(report));

    /// <summary>Updates the status bar from a finished scan. Runs on the UI thread.</summary>
    /// <param name="report">The report that just completed.</param>
    internal void ApplyScanResult(DiscoveryReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        // A refused directory is a normal outcome, not an exception dialog. It is the one result
        // worth promoting over "scan complete", because it means the list on screen is short for
        // a reason the user can do something about.
        var denied = report.ProbeStatuses.Count(p => p.Status == ProbeStatus.AccessDenied);

        if (denied > 0)
        {
            _status.Report(StatusKind.Error, Keys.Status_InsufficientPermissions);
        }
        else
        {
            // The resource carries the numeric format, so the count of decimals is a translation
            // decision rather than a literal buried in a view model.
            _status.Report(StatusKind.Done, Keys.Status_ScanCompleted, report.Duration.TotalMilliseconds);
        }

        OnPropertyChanged(nameof(ArtifactsCaption));
        OnPropertyChanged(nameof(SidebarStatusCaption));
        OnPropertyChanged(nameof(CanRollback));
    }

    partial void OnSelectedPageChanged(PageViewModel? value)
    {
        OnPropertyChanged(nameof(CanCopySelection));

        if (value is null)
        {
            return;
        }

        // Keep the tree in step when navigation came from a menu or the toolbar rather than a
        // click on the tree itself.
        var node = RootNodes.SelectMany(r => r.Children).FirstOrDefault(n => ReferenceEquals(n.Page, value));
        if (node is not null && !ReferenceEquals(node, SelectedNode))
        {
            SelectedNode = node;
        }

        // The tab caption follows the page, and the status bar says where the user is.
        _status.ReportText(StatusKind.Ready, L.Format(Keys.Status_Viewing, value.Title));

        value.PropertyChanged += OnPagePropertyChanged;

        // The page's grid has not been attached yet at this point; re-assert the selection once
        // it has, so the highlighted row and the properties pane agree.
        Avalonia.Threading.Dispatcher.UIThread.Post(value.EnsureSelection);

        if (!_activated.Add(value))
        {
            return;
        }

        // First activation of a page may need to read something; failures land in the log
        // rather than in a dialog, because navigation must never be able to fail.
        _ = ActivateAsync(value);
    }

    private void OnPagePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PageViewModel.CanCopySelection) or nameof(PageViewModel.HasSelection) or "")
        {
            OnPropertyChanged(nameof(CanCopySelection));
        }
    }

    private static async Task ActivateAsync(PageViewModel page)
    {
        try
        {
            await page.OnActivatedAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Activating page {Page} failed", page.GetType().Name);
        }
    }
}
