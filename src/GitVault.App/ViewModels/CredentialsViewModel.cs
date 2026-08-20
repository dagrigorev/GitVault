using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitVault.App.Services;
using GitVault.Core.Abstractions;
using GitVault.Core.Credentials;
using GitVault.Core.Models;
using GitVault.Core.Settings;
using GitVault.Localization;

namespace GitVault.App.ViewModels;

/// <summary>One row of the credentials grid.</summary>
internal sealed class CredentialRow(Localizer localizer, CredentialEntry entry) : ObservableObject
{
    /// <summary>Bindable localizer.</summary>
    public Localizer L { get; } = localizer;

    /// <summary>The underlying entry.</summary>
    public CredentialEntry Entry { get; } = entry;

    /// <summary>Host, shown verbatim.</summary>
    public string Host => Entry.Host;

    /// <summary>Account name.</summary>
    public string UserName => Entry.UserName;

    /// <summary>Protocol, shown verbatim.</summary>
    public string Protocol => Entry.Protocol;

    /// <summary>Native target string, shown verbatim.</summary>
    public string Target => Entry.Target;

    /// <summary>Owning application, when identifiable. Product names are not translated.</summary>
    public string OwningClient => Entry.OwningClient ?? string.Empty;

    /// <summary>Localized vault name.</summary>
    public string Vault => L[DisplayNames.VaultKindKey(Entry.Vault)];

    /// <summary>Last write time in the current culture, or the localized "never".</summary>
    public string LastWrite => Entry.LastWriteUtc is { } written
        ? written.ToLocalTime().ToString("g", L.Service.CurrentCulture)
        : L[Keys.Common_Never];

    /// <summary>True when the entry lives in an unencrypted store.</summary>
    public bool IsPlaintext => Entry.IsPlaintextStore;

    /// <summary>Re-reads the localized members. Called when the culture changes.</summary>
    internal void RefreshCaptions() =>
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(string.Empty));
}

/// <summary>Credentials found across every reachable vault.</summary>
internal sealed partial class CredentialsViewModel : ListPageViewModel
{
    private readonly ScanCoordinator _scans;
    private readonly IEnumerable<ICredentialVault> _vaults;
    private readonly IClipboardService _clipboard;
    private readonly ISettingsService _settings;

    private DispatcherTimer? _hideTimer;

    [ObservableProperty]
    private CredentialRow? _selectedRow;

    [ObservableProperty]
    private bool _showAll;

    [ObservableProperty]
    private bool _revealConfirmationPending;

    [ObservableProperty]
    private string? _revealedSecret;

    [ObservableProperty]
    private int _secondsUntilHidden;

    public CredentialsViewModel(
        Localizer localizer,
        ScanCoordinator scans,
        IEnumerable<ICredentialVault> vaults,
        IClipboardService clipboard,
        ISettingsService settings)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(scans);
        ArgumentNullException.ThrowIfNull(vaults);
        ArgumentNullException.ThrowIfNull(clipboard);
        ArgumentNullException.ThrowIfNull(settings);

        _scans = scans;
        _vaults = vaults;
        _clipboard = clipboard;
        _settings = settings;
        _scans.ScanCompleted += OnScanCompleted;
    }

    public override string NavKey => Keys.Nav_Credentials;

    public override string TitleKey => Keys.Credentials_Title;

    /// <inheritdoc/>
    public override string SubtitleKey => Keys.Credentials_Subtitle;

    /// <inheritdoc/>
    public override string IconKey => "IconCredentials";

    public override string EmptyKey => Keys.Credentials_Empty;

    /// <inheritdoc/>
    public override bool IsEmpty => Rows.Count == 0;

    /// <summary>Rows currently shown, after the git-relevance filter.</summary>
    public ObservableCollection<CredentialRow> Rows { get; } = [];

    /// <summary>Every row from the last scan, before filtering.</summary>
    internal List<CredentialRow> AllRows { get; } = [];

    /// <summary>True while a secret is on screen.</summary>
    public bool IsSecretVisible => RevealedSecret is not null;

    /// <summary>Localized countdown telling the user when the secret disappears.</summary>
    public string HidingInCaption => L.Format(Keys.Credentials_HidingIn, SecondsUntilHidden);

    /// <summary>
    /// Reveals the selected entry's secret. The first invocation asks for confirmation and the
    /// second performs the read, so a secret is never displayed by a single stray click.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>A task that completes once the secret is on screen, or the confirmation is armed.</returns>
    [RelayCommand]
    private async Task RevealAsync(CancellationToken cancellationToken)
    {
        if (SelectedRow is null)
        {
            return;
        }

        if (_settings.Current.RevealPolicy.RequireConfirmation && !RevealConfirmationPending)
        {
            RevealConfirmationPending = true;
            return;
        }

        RevealConfirmationPending = false;

        var vault = _vaults.FirstOrDefault(v => v.Kind == SelectedRow.Entry.Vault && v.IsAvailable);
        if (vault is null)
        {
            return;
        }

        byte[]? secret = null;
        try
        {
            secret = await vault.RevealAsync(SelectedRow.Entry.Target, cancellationToken).ConfigureAwait(true);
            if (secret is null)
            {
                return;
            }

            RevealedSecret = Encoding.UTF8.GetString(secret);
            StartHideCountdown();
        }
        finally
        {
            if (secret is not null)
            {
                // Zero the buffer the vault handed us. The string copy is unavoidable for
                // display and is dropped when the countdown fires.
                CryptographicOperations.ZeroMemory(secret);
            }
        }
    }

    /// <summary>Hides a revealed secret immediately.</summary>
    [RelayCommand]
    private void HideSecret()
    {
        StopHideCountdown();
        RevealedSecret = null;
        SecondsUntilHidden = 0;
    }

    /// <summary>Copies a revealed secret, scheduling the clipboard to be cleared.</summary>
    /// <param name="cancellationToken">Cancels the copy.</param>
    /// <returns>A task that completes once the clipboard has been set.</returns>
    [RelayCommand]
    private async Task CopySecretAsync(CancellationToken cancellationToken)
    {
        if (RevealedSecret is not null)
        {
            await _clipboard.CopySecretAsync(RevealedSecret, cancellationToken).ConfigureAwait(false);
        }
    }


    /// <inheritdoc/>
    internal override void EnsureSelection()
    {
        if (Rows.Count == 0)
        {
            return;
        }

        var current = SelectedRow;
        SelectedRow = null;
        SelectedRow = current ?? Rows[0];
    }
    /// <inheritdoc/>
    protected override void OnCultureChanged()
    {
        foreach (var row in AllRows)
        {
            row.RefreshCaptions();
        }
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopHideCountdown();
            _scans.ScanCompleted -= OnScanCompleted;
        }

        base.Dispose(disposing);
    }

    private void StartHideCountdown()
    {
        StopHideCountdown();

        SecondsUntilHidden = Math.Max(1, _settings.Current.RevealPolicy.AutoHideSeconds);

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _hideTimer.Tick += (_, _) =>
        {
            SecondsUntilHidden--;
            if (SecondsUntilHidden <= 0)
            {
                HideSecret();
            }
        };

        _hideTimer.Start();
    }

    private void StopHideCountdown()
    {
        _hideTimer?.Stop();
        _hideTimer = null;
    }

    private void OnScanCompleted(object? sender, DiscoveryReport report) =>
        Dispatcher.UIThread.Post(() => Apply(report));

    /// <summary>Copies a report's credentials into the page. Runs on the UI thread.</summary>
    /// <param name="report">Report to display.</param>
    internal void Apply(DiscoveryReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        HideSecret();

        AllRows.Clear();
        foreach (var entry in report.Credentials
                     .OrderByDescending(c => c.IsPlaintextStore)
                     .ThenBy(c => c.Host, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(c => c.UserName, StringComparer.OrdinalIgnoreCase))
        {
            AllRows.Add(new CredentialRow(L, entry));
        }

        ApplyFilter(report);
    }

    private void ApplyFilter(DiscoveryReport report)
    {
        // Hosts the user's own git configuration mentions widen the filter beyond the
        // well-known forges, so a self-hosted server still shows up.
        //
        // Deliberately NOT seeded from the credential list: every entry's host would then be
        // "known", the filter would match everything, and the whole vault — password managers,
        // games, unrelated applications — would be on screen with the box unchecked.
        var knownHosts = report.Identities
            .SelectMany(i => i.Hosts)
            .Concat(report.Clients.SelectMany(c => c.Credentials).Select(c => c.Host))
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Rows.Clear();
        foreach (var row in AllRows)
        {
            if (ShowAll || CredentialTargetFilter.IsGitRelated(row.Entry, knownHosts))
            {
                Rows.Add(row);
            }
        }

        SelectedRow = Rows.FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty));
    }

    partial void OnShowAllChanged(bool value)
    {
        _ = value;
        ApplyFilter(_scans.Report);
    }

    partial void OnRevealedSecretChanged(string? value)
    {
        _ = value;
        OnPropertyChanged(nameof(IsSecretVisible));
    }

    partial void OnSecondsUntilHiddenChanged(int value)
    {
        _ = value;
        OnPropertyChanged(nameof(HidingInCaption));
    }

    partial void OnSelectedRowChanged(CredentialRow? value)
    {
        // A DataGrid pushes null back through the binding when it is first attached. A classic
        // list always has a current item, so re-assert the first row instead of letting the
        // properties pane blank itself the moment the page is shown.
        if (value is null && Rows.Count > 0)
        {
            SelectedRow = Rows[0];
            return;
        }

        _ = value;

        // Changing rows must never leave the previous row's secret on screen.
        HideSecret();
        RevealConfirmationPending = false;

        RebuildProperties();
    }

    /// <summary>
    /// Fills the properties pane for the selected credential.
    /// </summary>
    /// <remarks>
    /// The secret is never among these entries, revealed or not. The pane says whether the store
    /// keeps the value in the clear and whether GitVault can read it at all; reading it stays a
    /// separate, confirmed, time-limited action on the page itself.
    /// </remarks>
    private void RebuildProperties()
    {
        if (SelectedRow is not { } row)
        {
            SetProperties([]);
            return;
        }

        var entries = new List<PropertyEntry>
        {
            Property(Keys.Credentials_Column_Vault, row.Vault),
            Property(Keys.Credentials_Column_Host, row.Host),
            Property(Keys.Credentials_Column_Protocol, row.Protocol),
            Property(Keys.Credentials_Column_UserName, row.UserName),
            Property(Keys.Credentials_Column_Client, row.OwningClient),
            Property(Keys.Credentials_Detail_Target, row.Target, PropertyStyle.Mono),
            Property(Keys.Credentials_Column_LastWrite, row.LastWrite),
            Property(
                Keys.Credentials_Detail_Storage,
                L[row.IsPlaintext ? Keys.Credentials_PlaintextBadge : Keys.Credentials_ProtectedBadge],
                row.IsPlaintext ? PropertyStyle.BadgeWarn : PropertyStyle.BadgeOk),
            Property(Keys.Credentials_Detail_SecretValue, L[Keys.Credentials_NotRead], PropertyStyle.Badge),
        };

        SetProperties(entries);
    }
}
