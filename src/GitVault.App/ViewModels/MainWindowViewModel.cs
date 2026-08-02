using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitVault.App.Services;
using GitVault.Localization;

namespace GitVault.App.ViewModels;

/// <summary>Shell view model: owns the navigation rail, the current page and the rescan action.</summary>
internal sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly ScanCoordinator _scans;
    private readonly HashSet<PageViewModel> _activated = [];

    [ObservableProperty]
    private PageViewModel? _selectedPage;

    [ObservableProperty]
    private string _searchText = string.Empty;

    public MainWindowViewModel(Localizer localizer, IEnumerable<PageViewModel> pages, ScanCoordinator scans)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(scans);

        _scans = scans;
        _scans.PropertyChanged += OnScansPropertyChanged;

        foreach (var page in pages)
        {
            Pages.Add(page);
        }

        SelectedPage = Pages.FirstOrDefault();
    }

    /// <summary>Navigable pages, in rail order.</summary>
    public ObservableCollection<PageViewModel> Pages { get; } = [];

    /// <summary>Window title. The product name is not localized; the subtitle and separator are.</summary>
    public string WindowTitle => L.Format(Keys.App_WindowTitle, L[Keys.App_Title], L[Keys.App_Subtitle]);

    /// <summary>Localized placeholder for the search box.</summary>
    public string SearchPlaceholder => L[Keys.Common_Search];

    /// <summary>True while a scan is running; the rescan button reflects this.</summary>
    public bool IsScanning => _scans.IsScanning;

    /// <summary>Runs a full rescan. Bound to the toolbar button and to F5.</summary>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>A task that completes when the scan has finished.</returns>
    [RelayCommand]
    public async Task RescanAsync(CancellationToken cancellationToken)
    {
        await _scans.RescanAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _scans.PropertyChanged -= OnScansPropertyChanged;
            foreach (var page in Pages)
            {
                page.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    private void OnScansPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScanCoordinator.IsScanning))
        {
            OnPropertyChanged(nameof(IsScanning));
        }
    }

    partial void OnSelectedPageChanged(PageViewModel? value)
    {
        if (value is null || !_activated.Add(value))
        {
            return;
        }

        // First activation of a page may need to read something; failures land in the log
        // rather than in a dialog, because navigation must never be able to fail.
        _ = ActivateAsync(value);
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
