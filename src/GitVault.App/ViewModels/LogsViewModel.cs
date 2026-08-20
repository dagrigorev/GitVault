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

    [ObservableProperty]
    private LogLine? _selectedLine;

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
    public override string SubtitleKey => Keys.Logs_Subtitle;

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

    partial void OnSelectedLineChanged(LogLine? value)
    {
        _ = value;
        RebuildProperties();
    }

    /// <inheritdoc/>
    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        RebuildProperties();
    }

    /// <summary>
    /// Fills the properties pane for the selected log entry.
    /// </summary>
    /// <remarks>
    /// Whatever is here has already been through the redactor on its way into the sink, and the
    /// pane says so: a log the user is about to paste into an issue should carry a visible claim
    /// about what was removed from it.
    /// </remarks>
    private void RebuildProperties()
    {
        if (SelectedLine is not { } line)
        {
            SetProperties([]);
            return;
        }

        var entries = new List<PropertyEntry>
        {
            Property(Keys.Logs_Column_Time, line.Timestamp.ToLocalTime().ToString("T", L.Service.CurrentCulture)),
            Property(Keys.Logs_Column_Level, line.Level, PropertyStyle.Badge),
            Property(Keys.Logs_Column_Message, line.Message),
            Property(Keys.Logs_Detail_Secrets, L[Keys.Logs_Redacted], PropertyStyle.BadgeOk),
        };

        if (!string.IsNullOrEmpty(line.Exception))
        {
            entries.Add(Property(Keys.Logs_Detail_Exception, line.Exception, PropertyStyle.Mono));
        }

        SetProperties(entries);
    }
}
