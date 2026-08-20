using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitVault.App.Services;
using GitVault.Core.Abstractions;
using GitVault.Core.Models;
using GitVault.Core.Ssh;
using GitVault.Localization;

namespace GitVault.App.ViewModels;

/// <summary>One row of the SSH keys grid.</summary>
internal sealed class SshKeyRow(Localizer localizer, SshKey key) : ObservableObject
{
    /// <summary>Bindable localizer.</summary>
    public Localizer L { get; } = localizer;

    /// <summary>The underlying key.</summary>
    public SshKey Key { get; } = key;

    /// <summary>Algorithm name, a technical identifier shown verbatim.</summary>
    public string Algorithm => Key.Algorithm.ToString();

    /// <summary>Key size in bits, or an empty cell for algorithms without one.</summary>
    public string Bits => Key.BitLength?.ToString(L.Service.CurrentCulture) ?? string.Empty;

    /// <summary>Canonical fingerprint, shown verbatim.</summary>
    public string Fingerprint => Key.FingerprintSha256;

    /// <summary>Legacy MD5 fingerprint, shown verbatim.</summary>
    public string FingerprintMd5 => Key.FingerprintMd5;

    /// <summary>Comment stored with the key.</summary>
    public string Comment => Key.Comment ?? string.Empty;

    /// <summary>Private key path, or the public key path for an orphan.</summary>
    public string Path => Key.PrivatePath ?? Key.PublicPath ?? string.Empty;

    /// <summary>Localized container format.</summary>
    public string Format => L[DisplayNames.KeyFormatKey(Key.Format)];

    /// <summary>Localized protection state.</summary>
    public string Protection => Key.IsHardwareBacked
        ? L[Keys.Keys_Hardware]
        : Key.IsEncrypted ? L[Keys.Keys_Encrypted] : L[Keys.Keys_NotEncrypted];

    /// <summary>File mode of the private key, or an empty cell on Windows.</summary>
    public string Permissions => Key.Permissions?.ToOctal() ?? string.Empty;

    /// <summary>KDF work factor, when the container declares one.</summary>
    public string KdfRounds => Key.KdfRounds?.ToString(L.Service.CurrentCulture) ?? string.Empty;

    /// <summary>The public key as an OpenSSH line, for the copy button.</summary>
    public string PublicKeyLine => Key.PublicKeyBlob.Count == 0
        ? string.Empty
        : SshPublicKeyReader.FromBlob([.. Key.PublicKeyBlob], Key.Comment).ToOpenSshLine();

    /// <summary>True when this key has findings.</summary>
    public bool HasWarnings => Key.Warnings.Count > 0;

    /// <summary>Re-reads the localized members. Called when the culture changes.</summary>
    internal void RefreshCaptions() =>
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(string.Empty));
}

/// <summary>SSH keys discovered on disk.</summary>
internal sealed partial class SshKeysViewModel : ListPageViewModel
{
    private readonly ScanCoordinator _scans;
    private readonly IClipboardService _clipboard;
    private readonly IShellLauncher _shell;

    [ObservableProperty]
    private SshKeyRow? _selectedRow;

    [ObservableProperty]
    private bool _showCopyConfirmation;

    public SshKeysViewModel(
        Localizer localizer,
        ScanCoordinator scans,
        IClipboardService clipboard,
        IShellLauncher shell)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(scans);
        ArgumentNullException.ThrowIfNull(clipboard);
        ArgumentNullException.ThrowIfNull(shell);

        _scans = scans;
        _clipboard = clipboard;
        _shell = shell;
        _scans.ScanCompleted += OnScanCompleted;
    }

    public override string NavKey => Keys.Nav_Keys;

    public override string TitleKey => Keys.Keys_Title;

    /// <inheritdoc/>
    public override string SubtitleKey => Keys.Keys_Subtitle;

    /// <inheritdoc/>
    public override string IconKey => "IconKeys";

    public override string EmptyKey => Keys.Keys_Empty;

    /// <inheritdoc/>
    public override bool IsEmpty => Rows.Count == 0;

    /// <summary>Discovered keys.</summary>
    public ObservableCollection<SshKeyRow> Rows { get; } = [];

    /// <summary>Copies the selected key's public half.</summary>
    /// <param name="cancellationToken">Cancels the copy.</param>
    /// <returns>A task that completes once the clipboard has been set.</returns>
    [RelayCommand]
    private async Task CopyPublicKeyAsync(CancellationToken cancellationToken)
    {
        if (SelectedRow is null || SelectedRow.PublicKeyLine.Length == 0)
        {
            return;
        }

        // A public key is not a secret, so it stays on the clipboard until the user replaces it.
        ShowCopyConfirmation = await _clipboard
            .CopyAsync(SelectedRow.PublicKeyLine, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Copies the selected key's fingerprint.</summary>
    /// <param name="cancellationToken">Cancels the copy.</param>
    /// <returns>A task that completes once the clipboard has been set.</returns>
    [RelayCommand]
    private async Task CopyFingerprintAsync(CancellationToken cancellationToken)
    {
        if (SelectedRow is null)
        {
            return;
        }

        ShowCopyConfirmation = await _clipboard
            .CopyAsync(SelectedRow.Fingerprint, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Opens the file manager with the selected key highlighted.</summary>
    [RelayCommand]
    private void RevealInFileManager()
    {
        if (SelectedRow is { Path.Length: > 0 })
        {
            _shell.RevealFile(SelectedRow.Path);
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
        base.OnCultureChanged();

        foreach (var row in Rows)
        {
            row.RefreshCaptions();
        }

        RebuildProperties();
    }

    partial void OnSelectedRowChanged(SshKeyRow? value)
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
        RebuildProperties();
    }

    /// <summary>
    /// Fills the properties pane for the selected key.
    /// </summary>
    /// <remarks>
    /// The private half is named and then explicitly not shown. Saying "hidden by design" where
    /// the value would be is the point: it tells the user the material was found and deliberately
    /// withheld, rather than leaving them to wonder whether the row is simply incomplete.
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
            Property(Keys.Keys_Column_Algorithm, row.Algorithm),
            Property(Keys.Keys_Column_Bits, row.Bits),
            Property(Keys.Keys_Column_Fingerprint, row.Fingerprint, PropertyStyle.Mono),
            Property(Keys.Keys_Column_Comment, row.Comment),
            Property(Keys.Keys_Column_Format, row.Format),
            Property(Keys.Keys_Column_Path, row.Path, PropertyStyle.Mono),
            Property(Keys.Keys_Detail_PrivateMaterial, L[Keys.Keys_HiddenByDesign], PropertyStyle.Badge),
            Property(
                Keys.Keys_Column_Protection,
                row.Protection,
                row.Key.IsEncrypted || row.Key.IsHardwareBacked ? PropertyStyle.BadgeOk : PropertyStyle.BadgeWarn),
        };

        if (row.Permissions.Length > 0)
        {
            entries.Add(Property(Keys.Keys_Detail_Permissions, row.Permissions, PropertyStyle.Mono));
        }

        if (row.KdfRounds.Length > 0)
        {
            entries.Add(Property(Keys.Keys_Detail_KdfRounds, row.KdfRounds));
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

    /// <summary>Copies a report's keys into the grid. Runs on the UI thread.</summary>
    /// <param name="report">Report to display.</param>
    internal void Apply(DiscoveryReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        Rows.Clear();

        // Keys with findings first, then by algorithm, so the ones needing attention are visible
        // without scrolling.
        foreach (var key in report.Keys
                     .OrderByDescending(k => k.Warnings.Count > 0)
                     .ThenBy(k => k.Algorithm.ToString(), StringComparer.Ordinal)
                     .ThenBy(k => k.PrivatePath ?? k.PublicPath, StringComparer.OrdinalIgnoreCase))
        {
            Rows.Add(new SshKeyRow(L, key));
        }

        SelectedRow = Rows.FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty));
    }
}
