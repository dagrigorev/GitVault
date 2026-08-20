using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GitVault.App.Services;
using GitVault.Core.Abstractions;
using GitVault.Core.Git;
using GitVault.Core.Discovery;
using GitVault.Core.Models;
using GitVault.Localization;

namespace GitVault.App.ViewModels;

/// <summary>One summary tile on the dashboard.</summary>
internal sealed partial class SummaryCard : ObservableObject
{
    [ObservableProperty]
    private long _count;

    internal SummaryCard(Localizer localizer, string captionKey, string pluralKeyPrefix, string iconKey)
    {
        L = localizer;
        CaptionKey = captionKey;
        PluralKeyPrefix = pluralKeyPrefix;
        IconKey = iconKey;
    }

    /// <summary>Bindable localizer.</summary>
    public Localizer L { get; }

    /// <summary>Key of the icon shown on the tile.</summary>
    public string IconKey { get; }

    /// <summary>Resource key of the tile caption.</summary>
    public string CaptionKey { get; }

    /// <summary>Key prefix used to pluralize <see cref="Count"/>.</summary>
    public string PluralKeyPrefix { get; }

    /// <summary>Localized tile caption.</summary>
    public string Caption => L[CaptionKey];

    /// <summary>Localized, pluralized count.</summary>
    public string CountCaption => L.Plural(PluralKeyPrefix, Count);

    /// <summary>Re-reads every localized member. Called when the culture changes.</summary>
    internal void RefreshCaptions() =>
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(string.Empty));

    partial void OnCountChanged(long value)
    {
        _ = value;
        OnPropertyChanged(nameof(CountCaption));
    }
}

/// <summary>One health finding, with its localized title and explanation.</summary>
internal sealed class WarningRow(Localizer localizer, KeyWarning warning) : ObservableObject
{
    /// <summary>Bindable localizer.</summary>
    public Localizer L { get; } = localizer;

    /// <summary>Severity, used to pick the chip colour.</summary>
    public WarningSeverity Severity { get; } = warning.Severity;

    /// <summary>Path or object the finding is about, shown verbatim.</summary>
    public string Subject { get; } = warning.Subject;

    /// <summary>Localized one-line title.</summary>
    public string Title => L[WarningKeys.Title(warning.Code)];

    /// <summary>Localized explanation, shown behind "What does this mean?".</summary>
    public string Body => L[WarningKeys.Body(warning.Code)];

    /// <summary>Localized severity name, for the properties pane.</summary>
    public string SeverityCaption => L[Severity switch
    {
        WarningSeverity.High => Keys.Severity_High,
        WarningSeverity.Medium => Keys.Severity_Medium,
        _ => Keys.Severity_Low,
    }];

    /// <summary>Re-reads the localized members. Called when the culture changes.</summary>
    internal void RefreshCaptions() =>
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(string.Empty));
}

/// <summary>Scan summary, environment facts and health warnings.</summary>
internal sealed partial class DashboardViewModel : PageViewModel
{
    private readonly IPlatformInfo _platformInfo;
    private readonly IPlatformPaths _paths;
    private readonly IGitConfigService _gitConfig;
    private readonly IEffectiveIdentityResolver _resolver;
    private readonly ScanCoordinator _scans;

    [ObservableProperty]
    private WarningRow? _selectedWarning;

    public DashboardViewModel(
        Localizer localizer,
        IPlatformInfo platformInfo,
        IPlatformPaths paths,
        IGitConfigService gitConfig,
        IEffectiveIdentityResolver resolver,
        ScanCoordinator scans)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(platformInfo);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(gitConfig);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(scans);

        _platformInfo = platformInfo;
        _paths = paths;
        _gitConfig = gitConfig;
        _resolver = resolver;
        _scans = scans;

        Cards =
        [
            new SummaryCard(localizer, Keys.Dashboard_Card_Identities, "Plural_Identities", "IconIdentities"),
            new SummaryCard(localizer, Keys.Dashboard_Card_Keys, "Plural_Keys", "IconKeys"),
            new SummaryCard(localizer, Keys.Dashboard_Card_Agents, "Plural_Agents", "IconAgents"),
            new SummaryCard(localizer, Keys.Dashboard_Card_Credentials, "Plural_Credentials", "IconCredentials"),
            new SummaryCard(localizer, Keys.Dashboard_Card_Clients, "Plural_Clients", "IconClients"),
        ];

        _scans.ScanCompleted += OnScanCompleted;
    }

    public override string NavKey => Keys.Nav_Dashboard;

    public override string TitleKey => Keys.Dashboard_Title;

    /// <inheritdoc/>
    public override string SubtitleKey => Keys.Dashboard_Subtitle;

    /// <inheritdoc/>
    public override string IconKey => "IconOverview";

    /// <summary>Summary tiles, in display order.</summary>
    public ObservableCollection<SummaryCard> Cards { get; }

    /// <summary>Health findings from the last scan.</summary>
    public ObservableCollection<WarningRow> Warnings { get; } = [];

    /// <summary>Operating system description, shown verbatim and never localized.</summary>
    public string OsDescription => _platformInfo.OsDescription;

    /// <summary>Process architecture, shown verbatim.</summary>
    public string Architecture => _platformInfo.Architecture;

    /// <summary>Directory holding settings, cache, snapshots and logs. Shown verbatim.</summary>
    public string AppDataDirectory => _paths.AppDataDirectory;

    /// <summary>Located git executable and version, or the localized "not found" title.</summary>
    public string GitDescription => _gitConfig.HasGitBinary
        ? L.Format(Keys.Dashboard_Env_GitPathAndVersion, _gitConfig.GitBinaryPath, _gitConfig.GitVersion)
        : L[WarningKeys.Title(GitIdentityProbe.GitNotFoundCode)];

    /// <summary>Localized "no scan yet" caption, shown until the first scan completes.</summary>
    public string NoScanCaption => L[Keys.Dashboard_NoScanYet];

    /// <summary>True while no scan has produced a report.</summary>
    public bool HasNoScan => !_scans.HasScanned;

    /// <summary>Localized warning-count caption.</summary>
    public string WarningCountCaption => L.Plural("Plural_Warnings", Warnings.Count);

    /// <summary>Localized scan-duration caption.</summary>
    public string ScanDurationCaption => L.Format(
        Keys.Dashboard_ScanDuration,
        (long)Math.Round(_scans.Report.Duration.TotalMilliseconds));

    /// <summary>Effective identity settings, as the overview lists them.</summary>
    public ObservableCollection<EffectiveSettingRow> EffectiveSettings { get; } = [];

    /// <summary>True once the effective identity has been resolved.</summary>
    public bool HasEffectiveSettings => EffectiveSettings.Count > 0;

    /// <inheritdoc/>
    public override async Task OnActivatedAsync(CancellationToken cancellationToken)
    {
        // Which identity actually wins is the overview's most useful fact, and it is a question
        // about configuration rather than about the scan, so it is resolved here directly.
        var effective = await _resolver.ResolveAsync(null, cancellationToken).ConfigureAwait(true);

        EffectiveSettings.Clear();
        foreach (var setting in effective.All)
        {
            EffectiveSettings.Add(new EffectiveSettingRow(L, setting));
        }

        RebuildProperties();
        OnPropertyChanged(nameof(HasEffectiveSettings));
    }

    /// <inheritdoc/>
    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();

        foreach (var card in Cards)
        {
            card.RefreshCaptions();
        }

        foreach (var warning in Warnings)
        {
            warning.RefreshCaptions();
        }

        foreach (var setting in EffectiveSettings)
        {
            setting.RefreshCaptions();
        }

        RebuildProperties();
    }

    partial void OnSelectedWarningChanged(WarningRow? value)
    {
        _ = value;
        RebuildProperties();
    }

    /// <summary>Fills the properties pane from the machine and the selected finding.</summary>
    private void RebuildProperties()
    {
        var entries = new List<PropertyEntry>
        {
            Property(Keys.Dashboard_Detail_Platform, OsDescription),
            Property(Keys.Dashboard_Detail_Architecture, Architecture),
            Property(Keys.Dashboard_Detail_Git, GitDescription),
            Property(Keys.Dashboard_Detail_AppData, AppDataDirectory, PropertyStyle.Mono),
        };

        if (SelectedWarning is { } warning)
        {
            entries.Add(Property(Keys.Dashboard_Detail_Finding, warning.Title));
            entries.Add(Property(Keys.Dashboard_Detail_Subject, warning.Subject, PropertyStyle.Mono));
            entries.Add(Property(
                Keys.Dashboard_Detail_Severity,
                warning.SeverityCaption,
                warning.Severity == WarningSeverity.High ? PropertyStyle.BadgeWarn : PropertyStyle.Badge));
        }

        SetProperties(entries);
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _scans.ScanCompleted -= OnScanCompleted;
        }

        base.Dispose(disposing);
    }

    private void OnScanCompleted(object? sender, DiscoveryReport report) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Apply(report));

    /// <summary>Copies a report into the bound collections. Runs on the UI thread.</summary>
    /// <param name="report">Report to display.</param>
    internal void Apply(DiscoveryReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        Cards[0].Count = report.Identities.Count;
        Cards[1].Count = report.Keys.Count;
        Cards[2].Count = report.Agents.Count;
        Cards[3].Count = report.Credentials.Count;
        Cards[4].Count = report.Clients.Count;

        Warnings.Clear();
        foreach (var warning in report.Warnings.OrderByDescending(w => w.Severity))
        {
            Warnings.Add(new WarningRow(L, warning));
        }

        SelectedWarning = Warnings.FirstOrDefault();
        RebuildProperties();

        OnPropertyChanged(nameof(HasNoScan));
        OnPropertyChanged(nameof(HasEffectiveSettings));
        OnPropertyChanged(nameof(WarningCountCaption));
        OnPropertyChanged(nameof(ScanDurationCaption));
        OnPropertyChanged(nameof(GitDescription));
    }
}
