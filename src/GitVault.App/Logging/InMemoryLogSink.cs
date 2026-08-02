using System.Collections.Concurrent;
using System.Globalization;
using Serilog.Core;
using Serilog.Events;

namespace GitVault.App.Logging;

/// <summary>One rendered log line, as shown by the in-app log viewer.</summary>
/// <param name="Timestamp">When the event was raised.</param>
/// <param name="Level">Serilog level name.</param>
/// <param name="Message">Rendered message; already redacted by the enricher.</param>
/// <param name="Exception">Exception text, when present.</param>
internal sealed record LogLine(DateTimeOffset Timestamp, string Level, string Message, string? Exception);

/// <summary>
/// Bounded ring buffer of recent log events, feeding the in-app log viewer. Holds at most
/// <see cref="Capacity"/> lines so a long session cannot grow without bound.
/// </summary>
internal sealed class InMemoryLogSink : ILogEventSink
{
    /// <summary>Maximum number of lines retained.</summary>
    internal const int Capacity = 2000;

    private readonly ConcurrentQueue<LogLine> _lines = new();

    /// <summary>Raised on the logging thread whenever a line is appended.</summary>
    internal event EventHandler<LogLine>? LineAppended;

    /// <summary>Returns a snapshot of the retained lines, oldest first.</summary>
    /// <returns>The retained lines.</returns>
    internal IReadOnlyList<LogLine> Snapshot() => [.. _lines];

    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        var line = new LogLine(
            logEvent.Timestamp,
            logEvent.Level.ToString(),
            logEvent.RenderMessage(CultureInfo.InvariantCulture),
            logEvent.Exception?.ToString());

        _lines.Enqueue(line);
        while (_lines.Count > Capacity && _lines.TryDequeue(out _))
        {
            // Drop the oldest lines until we are back within capacity.
        }

        LineAppended?.Invoke(this, line);
    }
}
