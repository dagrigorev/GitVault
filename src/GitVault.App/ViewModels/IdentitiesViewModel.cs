using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GitVault.App.Services;
using GitVault.Core.Git;
using GitVault.Core.Models;
using GitVault.Localization;

namespace GitVault.App.ViewModels;

/// <summary>One row of the identities grid.</summary>
internal sealed class IdentityRow(Localizer localizer, GitIdentity identity) : ObservableObject
{
    /// <summary>Bindable localizer.</summary>
    public Localizer L { get; } = localizer;

    /// <summary>The underlying identity.</summary>
    public GitIdentity Identity { get; } = identity;

    /// <summary>Value of <c>user.name</c>.</summary>
    public string UserName => Identity.UserName;

    /// <summary>Value of <c>user.email</c>.</summary>
    public string Email => Identity.Email;

    /// <summary>Value of <c>user.signingkey</c>, or an empty cell.</summary>
    public string SigningKey => Identity.SigningKeyId ?? string.Empty;

    /// <summary>File or registry key the identity was read from.</summary>
    public string Path => Identity.SourcePath;

    /// <summary>Hosts the identity is associated with, joined with the culture's list separator.</summary>
    public string Hosts => string.Join(L[Keys.Common_ListSeparator], Identity.Hosts);

    /// <summary>Localized source label.</summary>
    public string Source => DisplayNames.SourceLabel(Identity.Source, L);

    /// <summary>Localized confidence label.</summary>
    public string Confidence => L[DisplayNames.ConfidenceKey(Identity.Confidence)];

    /// <summary>Every place this identity was seen, one per line.</summary>
    public string Occurrences => string.Join(
        Environment.NewLine,
        Identity.Occurrences.Select(o => o.Path));

    /// <summary>Re-reads the localized members. Called when the culture changes.</summary>
    internal void RefreshCaptions() =>
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(string.Empty));
}

/// <summary>One row of the effective-settings table.</summary>
internal sealed class EffectiveSettingRow(Localizer localizer, ResolvedSetting setting) : ObservableObject
{
    /// <summary>Bindable localizer.</summary>
    public Localizer L { get; } = localizer;

    /// <summary>Configuration key, shown verbatim.</summary>
    public string Key { get; } = setting.Key;

    /// <summary>Winning value, or the localized "not set" placeholder.</summary>
    public string Value => setting.IsSet ? setting.Value! : L[Keys.Identities_Effective_Unset];

    /// <summary>Localized name of the scope that won.</summary>
    public string Scope => setting.IsSet ? L[DisplayNames.ScopeKey(setting.Scope)] : string.Empty;

    /// <summary>Localized note listing the scopes this value overrode, when any.</summary>
    public string Overrides => setting.OverriddenIn.Count == 0
        ? string.Empty
        : L.Format(
            Keys.Identities_Effective_OverriddenIn,
            string.Join(
                L[Keys.Common_ListSeparator],
                setting.OverriddenIn.Select(s => L[DisplayNames.ScopeKey(s)])));

    /// <summary>Re-reads the localized members. Called when the culture changes.</summary>
    internal void RefreshCaptions() =>
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(string.Empty));
}

/// <summary>Identities discovered across every git configuration scope.</summary>
internal sealed partial class IdentitiesViewModel : ListPageViewModel
{
    private readonly ScanCoordinator _scans;
    private readonly IEffectiveIdentityResolver _resolver;

    [ObservableProperty]
    private IdentityRow? _selectedRow;

    public IdentitiesViewModel(
        Localizer localizer,
        ScanCoordinator scans,
        IEffectiveIdentityResolver resolver)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(scans);
        ArgumentNullException.ThrowIfNull(resolver);

        _scans = scans;
        _resolver = resolver;
        _scans.ScanCompleted += OnScanCompleted;
    }

    public override string NavKey => Keys.Nav_Identities;

    public override string TitleKey => Keys.Identities_Title;

    /// <inheritdoc/>
    public override string IconKey => "IconIdentities";

    public override string EmptyKey => Keys.Identities_Empty;

    /// <inheritdoc/>
    public override bool IsEmpty => Rows.Count == 0;

    /// <summary>Discovered identities.</summary>
    public ObservableCollection<IdentityRow> Rows { get; } = [];

    /// <summary>The settings actually in effect for this user.</summary>
    public ObservableCollection<EffectiveSettingRow> EffectiveSettings { get; } = [];

    /// <inheritdoc/>
    public override async Task OnActivatedAsync(CancellationToken cancellationToken)
    {
        await RefreshEffectiveAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Re-reads the effective identity for the user's context.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>A task that completes when the table has been rebuilt.</returns>
    internal async Task RefreshEffectiveAsync(CancellationToken cancellationToken)
    {
        var effective = await _resolver.ResolveAsync(null, cancellationToken).ConfigureAwait(false);

        EffectiveSettings.Clear();
        foreach (var setting in effective.All)
        {
            EffectiveSettings.Add(new EffectiveSettingRow(L, setting));
        }
    }

    /// <inheritdoc/>
    protected override void OnCultureChanged()
    {
        foreach (var row in Rows)
        {
            row.RefreshCaptions();
        }

        foreach (var row in EffectiveSettings)
        {
            row.RefreshCaptions();
        }
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

    private void OnScanCompleted(object? sender, DiscoveryReport report)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Rows.Clear();
            foreach (var identity in report.Identities.OrderBy(i => i.DisplayName, StringComparer.CurrentCulture))
            {
                Rows.Add(new IdentityRow(L, identity));
            }

            SelectedRow = Rows.FirstOrDefault();
            OnPropertyChanged(nameof(IsEmpty));
        });
    }
}
