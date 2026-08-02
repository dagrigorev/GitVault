using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitVault.App.Logging;
using GitVault.Localization;

namespace GitVault.App.ViewModels;

/// <summary>Filterable view over the in-memory log sink.</summary>
internal sealed partial class LogsViewModel : PageViewModel
{
    private readonly InMemoryLogSink _sink;

    [ObservableProperty]
    private string _filter = string.Empty;

    public LogsViewModel(Localizer localizer, InMemoryLogSink sink)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;

        foreach (var line in _sink.Snapshot())
        {
            Lines.Add(line);
        }

        _sink.LineAppended += OnLineAppended;
    }

    public override string NavKey => Keys.Nav_Logs;

    public override string TitleKey => Keys.Logs_Title;

    /// <inheritdoc/>
    public override string IconKey => "IconLogs";

    /// <summary>Lines matching <see cref="Filter"/>, oldest first.</summary>
    public ObservableCollection<LogLine> Lines { get; } = [];

    /// <summary>Clears the view. The log files on disk are untouched.</summary>
    [RelayCommand]
    private void ClearView() => Lines.Clear();

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _sink.LineAppended -= OnLineAppended;
        }

        base.Dispose(disposing);
    }

    private void OnLineAppended(object? sender, LogLine line)
    {
        // The sink raises this on whichever thread logged; marshal to the UI thread.
        Dispatcher.UIThread.Post(() =>
        {
            if (Matches(line))
            {
                Lines.Add(line);
            }

            while (Lines.Count > InMemoryLogSink.Capacity)
            {
                Lines.RemoveAt(0);
            }
        });
    }

    private bool Matches(LogLine line) =>
        string.IsNullOrWhiteSpace(Filter)
        || line.Message.Contains(Filter, StringComparison.OrdinalIgnoreCase)
        || line.Level.Contains(Filter, StringComparison.OrdinalIgnoreCase);

    partial void OnFilterChanged(string value)
    {
        _ = value;
        Lines.Clear();
        foreach (var line in _sink.Snapshot().Where(Matches))
        {
            Lines.Add(line);
        }
    }
}
