using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitVault.App.Services;
using GitVault.Core.Models;
using GitVault.Core.Ssh.Agent;
using GitVault.Localization;

namespace GitVault.App.ViewModels;

/// <summary>One key an agent is holding.</summary>
internal sealed class AgentKeyRow(Localizer localizer, AgentKeyEntry entry) : ObservableObject
{
    /// <summary>Bindable localizer.</summary>
    public Localizer L { get; } = localizer;

    /// <summary>The identity as the agent reported it.</summary>
    public AgentKeyEntry Entry { get; } = entry;

    /// <summary>Algorithm name, a technical identifier shown verbatim.</summary>
    public string Algorithm => Entry.Algorithm.ToString();

    /// <summary>Canonical fingerprint, shown verbatim.</summary>
    public string Fingerprint => Entry.FingerprintSha256;

    /// <summary>Comment the agent stored with the key.</summary>
    public string Comment => Entry.Comment;

    /// <summary>Re-reads the localized members. Called when the culture changes.</summary>
    internal void RefreshCaptions() =>
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(string.Empty));
}

/// <summary>A shell the snippet generator can target.</summary>
internal sealed record ShellOption(ShellKind Kind, string Label)
{
    /// <inheritdoc/>
    public override string ToString() => Label;
}

/// <summary>One detected agent, with its keys and the actions available on it.</summary>
internal sealed partial class AgentCard : ObservableObject
{
    private readonly ISshAgentTransportFactory? _factory;

    [ObservableProperty]
    private ShellOption _selectedShell;

    [ObservableProperty]
    private bool _removeAllArmed;

    [ObservableProperty]
    private string? _lastError;

    internal AgentCard(Localizer localizer, SshAgentInfo agent, ISshAgentTransportFactory? factory)
    {
        L = localizer;
        Agent = agent;
        _factory = factory;

        Shells =
        [
            new ShellOption(ShellKind.Bash, "bash"),
            new ShellOption(ShellKind.Zsh, "zsh"),
            new ShellOption(ShellKind.Fish, "fish"),
            new ShellOption(ShellKind.PowerShell, "PowerShell"),
            new ShellOption(ShellKind.Cmd, "cmd"),
        ];

        _selectedShell = Shells[0];

        foreach (var entry in agent.LoadedKeys)
        {
            Keys.Add(new AgentKeyRow(localizer, entry));
        }
    }

    /// <summary>Bindable localizer.</summary>
    public Localizer L { get; }

    /// <summary>The agent this card describes.</summary>
    public SshAgentInfo Agent { get; }

    /// <summary>Keys the agent is holding.</summary>
    public ObservableCollection<AgentKeyRow> Keys { get; } = [];

    /// <summary>Shells the snippet can be generated for.</summary>
    public ObservableCollection<ShellOption> Shells { get; }

    /// <summary>Localized agent name.</summary>
    public string KindCaption => L[DisplayNames.AgentKindKey(Agent.Kind)];

    /// <summary>Endpoint, shown verbatim.</summary>
    public string Endpoint => Agent.Endpoint;

    /// <summary>Localized running state.</summary>
    public string StateCaption => Agent.IsRunning
        ? L[Localization.Keys.Agents_Running]
        : L[Localization.Keys.Agents_Stopped];

    /// <summary>Localized, pluralized count of held keys.</summary>
    public string KeyCountCaption => L.Plural("Plural_Keys", Keys.Count);

    /// <summary>True when the agent refuses key additions.</summary>
    public bool IsReadOnly => !Agent.SupportsAdd;

    /// <summary>The shell snippet for the currently selected shell.</summary>
    public string Snippet => AgentShellSnippets.Build(Agent, SelectedShell.Kind);

    /// <summary>Re-reads the localized members. Called when the culture changes.</summary>
    internal void RefreshCaptions()
    {
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(string.Empty));
        foreach (var key in Keys)
        {
            key.RefreshCaptions();
        }
    }

    /// <summary>Removes one key from the agent.</summary>
    /// <param name="row">Key to remove.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes when the agent has answered.</returns>
    internal async Task RemoveKeyAsync(AgentKeyRow row, CancellationToken cancellationToken)
    {
        if (_factory is null || row is null)
        {
            return;
        }

        using var client = new SshAgentClient(
            new AgentEndpoint(Agent.Kind, Agent.Endpoint, TransportFor(Agent.Kind), Agent.SupportsAdd),
            _factory);

        var removed = await client
            .RemoveIdentityAsync(row.Entry.Blob.ToArray(), cancellationToken)
            .ConfigureAwait(true);

        if (removed)
        {
            Keys.Remove(row);
            OnPropertyChanged(nameof(KeyCountCaption));
        }
    }

    /// <summary>
    /// Removes every key. The first invocation arms the action and the second performs it, so a
    /// single mis-click cannot empty an agent.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes when the agent has answered.</returns>
    internal async Task RemoveAllAsync(CancellationToken cancellationToken)
    {
        if (!RemoveAllArmed)
        {
            RemoveAllArmed = true;
            return;
        }

        RemoveAllArmed = false;

        if (_factory is null)
        {
            return;
        }

        using var client = new SshAgentClient(
            new AgentEndpoint(Agent.Kind, Agent.Endpoint, TransportFor(Agent.Kind), Agent.SupportsAdd),
            _factory);

        if (await client.RemoveAllIdentitiesAsync(cancellationToken).ConfigureAwait(true))
        {
            Keys.Clear();
            OnPropertyChanged(nameof(KeyCountCaption));
        }
    }

    private static AgentTransportKind TransportFor(AgentKind kind) => kind switch
    {
        AgentKind.OpenSshWindowsPipe => AgentTransportKind.NamedPipe,
        AgentKind.Pageant => AgentTransportKind.NamedPipe,
        AgentKind.GpgAgent when OperatingSystem.IsWindows() => AgentTransportKind.EmulatedSocket,
        _ => AgentTransportKind.UnixSocket,
    };

    partial void OnSelectedShellChanged(ShellOption value)
    {
        _ = value;
        OnPropertyChanged(nameof(Snippet));
    }
}

/// <summary>SSH agents detected on this machine.</summary>
internal sealed partial class AgentsViewModel : ListPageViewModel
{
    private readonly ScanCoordinator _scans;
    private readonly IClipboardService _clipboard;
    private readonly IEnumerable<ISshAgentTransportFactory> _factories;

    [ObservableProperty]
    private AgentCard? _selectedAgent;

    public AgentsViewModel(
        Localizer localizer,
        ScanCoordinator scans,
        IClipboardService clipboard,
        IEnumerable<ISshAgentTransportFactory> factories)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(scans);
        ArgumentNullException.ThrowIfNull(clipboard);
        ArgumentNullException.ThrowIfNull(factories);

        _scans = scans;
        _clipboard = clipboard;
        _factories = factories;
        _scans.ScanCompleted += OnScanCompleted;
    }

    public override string NavKey => Localization.Keys.Nav_Agents;

    public override string TitleKey => Localization.Keys.Agents_Title;

    /// <inheritdoc/>
    public override string IconKey => "IconAgents";

    public override string EmptyKey => Localization.Keys.Agents_Empty;

    /// <inheritdoc/>
    public override bool IsEmpty => Cards.Count == 0;

    /// <summary>Detected agents.</summary>
    public ObservableCollection<AgentCard> Cards { get; } = [];

    /// <summary>Copies the selected agent's shell snippet.</summary>
    /// <param name="cancellationToken">Cancels the copy.</param>
    /// <returns>A task that completes once the clipboard has been set.</returns>
    [RelayCommand]
    private async Task CopySnippetAsync(CancellationToken cancellationToken)
    {
        if (SelectedAgent is not null)
        {
            await _clipboard.CopyAsync(SelectedAgent.Snippet, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Removes one key from its agent.</summary>
    /// <param name="row">Key to remove.</param>
    /// <returns>A task that completes when the agent has answered.</returns>
    [RelayCommand]
    private async Task RemoveKeyAsync(AgentKeyRow? row)
    {
        if (SelectedAgent is not null && row is not null)
        {
            await SelectedAgent.RemoveKeyAsync(row, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Arms, then performs, removal of every key from the selected agent.</summary>
    /// <returns>A task that completes when the agent has answered.</returns>
    [RelayCommand]
    private async Task RemoveAllKeysAsync()
    {
        if (SelectedAgent is not null)
        {
            await SelectedAgent.RemoveAllAsync(CancellationToken.None).ConfigureAwait(false);
        }
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

    /// <summary>Copies a report's agents into the page. Runs on the UI thread.</summary>
    /// <param name="report">Report to display.</param>
    internal void Apply(DiscoveryReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        Cards.Clear();
        foreach (var agent in report.Agents)
        {
            var factory = _factories.FirstOrDefault(f =>
                f.CanHandle(new AgentEndpoint(agent.Kind, agent.Endpoint, AgentTransportKind.UnixSocket)));

            Cards.Add(new AgentCard(L, agent, factory));
        }

        SelectedAgent = Cards.FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty));
    }
}
