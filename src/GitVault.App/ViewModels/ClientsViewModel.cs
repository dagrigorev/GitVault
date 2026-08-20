using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitVault.App.Services;
using GitVault.Core.Abstractions;
using GitVault.Core.Models;
using GitVault.Localization;

namespace GitVault.App.ViewModels;

/// <summary>One remote-to-key binding, as a client records it.</summary>
/// <param name="Remote">Remote name.</param>
/// <param name="KeyFile">Key file the remote is bound to.</param>
internal sealed record BoundKeyRow(string Remote, string KeyFile);

/// <summary>One detected client.</summary>
internal sealed class ClientCard(Localizer localizer, DetectedClient client) : ObservableObject
{
    /// <summary>Bindable localizer.</summary>
    public Localizer L { get; } = localizer;

    /// <summary>The client this card describes.</summary>
    public DetectedClient Client { get; } = client;

    /// <summary>Product name, never translated.</summary>
    public string Name => Client.DisplayName;

    /// <summary>Version string, or an empty cell.</summary>
    public string Version => Client.Version ?? string.Empty;

    /// <summary>Install path, or an empty cell.</summary>
    public string InstallPath => Client.InstallPath ?? string.Empty;

    /// <summary>Configuration directories, one per line.</summary>
    public string ConfigRoots => string.Join(System.Environment.NewLine, Client.ConfigRoots);

    /// <summary>Localized note shown when the client was found but could not be read.</summary>
    public string OpaqueCaption => L[Keys.Clients_Opaque];

    /// <summary>True when the client was detected but its configuration was unreadable.</summary>
    public bool IsOpaque => Client.IsOpaque;

    /// <summary>Identities the client has configured.</summary>
    public ObservableCollection<string> Accounts { get; } =
        [.. client.Accounts.Select(a => a.DisplayName)];

    /// <summary>Remote-to-key bindings the client records.</summary>
    public ObservableCollection<BoundKeyRow> BoundKeys { get; } =
    [
        .. (client.SshConfiguration?.BoundKeyFiles ?? new Dictionary<string, string>())
            .Select(kv => new BoundKeyRow(kv.Key, kv.Value))
            .OrderBy(b => b.Remote, StringComparer.OrdinalIgnoreCase)
    ];

    /// <summary>SSH program the client drives, or an empty cell.</summary>
    public string SshExecutable => Client.SshConfiguration?.SshExecutable ?? string.Empty;

    /// <summary>Localized, pluralized credential count.</summary>
    public string CredentialCountCaption => L.Plural("Plural_Credentials", Client.Credentials.Count);

    /// <summary>True when the client records any remote-to-key binding.</summary>
    public bool HasBoundKeys => BoundKeys.Count > 0;

    /// <summary>Re-reads the localized members. Called when the culture changes.</summary>
    internal void RefreshCaptions() =>
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(string.Empty));
}

/// <summary>Third-party Git clients detected on this machine.</summary>
internal sealed partial class ClientsViewModel : ListPageViewModel
{
    private readonly ScanCoordinator _scans;
    private readonly IShellLauncher _shell;

    [ObservableProperty]
    private ClientCard? _selectedClient;

    public ClientsViewModel(Localizer localizer, ScanCoordinator scans, IShellLauncher shell)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(scans);
        ArgumentNullException.ThrowIfNull(shell);

        _scans = scans;
        _shell = shell;
        _scans.ScanCompleted += OnScanCompleted;
    }

    public override string NavKey => Keys.Nav_Clients;

    public override string TitleKey => Keys.Clients_Title;

    /// <inheritdoc/>
    public override string SubtitleKey => Keys.Clients_Subtitle;

    /// <inheritdoc/>
    public override string IconKey => "IconClients";

    public override string EmptyKey => Keys.Clients_Empty;

    /// <inheritdoc/>
    public override bool IsEmpty => Cards.Count == 0;

    /// <summary>Detected clients.</summary>
    public ObservableCollection<ClientCard> Cards { get; } = [];

    /// <summary>Opens the selected client's first configuration folder.</summary>
    [RelayCommand]
    private void OpenConfigFolder()
    {
        var root = SelectedClient?.Client.ConfigRoots.FirstOrDefault();
        if (!string.IsNullOrEmpty(root))
        {
            _shell.OpenDirectory(root);
        }
    }


    /// <inheritdoc/>
    internal override void EnsureSelection()
    {
        if (Cards.Count == 0)
        {
            return;
        }

        var current = SelectedClient;
        SelectedClient = null;
        SelectedClient = current ?? Cards[0];
    }
    /// <inheritdoc/>
    protected override void OnCultureChanged()
    {
        foreach (var card in Cards)
        {
            card.RefreshCaptions();
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

    private void OnScanCompleted(object? sender, DiscoveryReport report) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Apply(report));

    /// <summary>Copies a report's clients into the page. Runs on the UI thread.</summary>
    /// <param name="report">Report to display.</param>
    internal void Apply(DiscoveryReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        Cards.Clear();

        // Clients we could read come first; the ones we only detected sit below them.
        foreach (var client in report.Clients
                     .OrderBy(c => c.IsOpaque)
                     .ThenBy(c => c.DisplayName, StringComparer.CurrentCulture))
        {
            Cards.Add(new ClientCard(L, client));
        }

        SelectedClient = Cards.FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty));
    }

    partial void OnSelectedClientChanged(ClientCard? value)
    {
        // A DataGrid pushes null back through the binding when it is first attached. A classic
        // list always has a current item, so re-assert the first row instead of letting the
        // properties pane blank itself the moment the page is shown.
        if (value is null && Cards.Count > 0)
        {
            SelectedClient = Cards[0];
            return;
        }

        _ = value;
        RebuildProperties();
    }

    /// <summary>Fills the properties pane for the selected client.</summary>
    private void RebuildProperties()
    {
        if (SelectedClient is not { } card)
        {
            SetProperties([]);
            return;
        }

        var entries = new List<PropertyEntry>
        {
            Property(Keys.Clients_Column_Client, card.Name),
            Property(Keys.Clients_Column_Version, card.Version),
            Property(Keys.Clients_InstallPath, card.InstallPath, PropertyStyle.Mono),
            Property(Keys.Clients_ConfigRoots, card.ConfigRoots, PropertyStyle.Mono),
            Property(Keys.Clients_Accounts, card.CredentialCountCaption),
        };

        // An opaque client is stated as opaque and left alone. GitVault reports what a store is;
        // it never tries to open one it was not given a supported way to read.
        if (card.IsOpaque)
        {
            entries.Add(Property(Keys.Clients_Column_Storage, card.OpaqueCaption, PropertyStyle.Badge));
        }

        if (card.SshExecutable.Length > 0)
        {
            entries.Add(Property(Keys.Clients_SshCommand, card.SshExecutable, PropertyStyle.Mono));
        }

        SetProperties(entries);
    }
}
