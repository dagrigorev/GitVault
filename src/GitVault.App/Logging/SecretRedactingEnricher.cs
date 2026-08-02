using GitVault.Core.Abstractions;
using Serilog.Core;
using Serilog.Events;

namespace GitVault.App.Logging;

/// <summary>
/// Rewrites every string-valued log property through <see cref="ISecretRedactor"/> before the
/// event reaches a sink.
/// </summary>
/// <remarks>
/// Message templates are compile-time literals in this codebase, so a secret can only enter a
/// log event as a property value. Redacting properties therefore covers every path, including
/// exception-derived text, which is added as a property by the sinks' output template.
/// </remarks>
internal sealed class SecretRedactingEnricher : ILogEventEnricher
{
    private readonly ISecretRedactor _redactor;

    internal SecretRedactingEnricher(ISecretRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(redactor);
        _redactor = redactor;
    }

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(propertyFactory);

        // Snapshot the keys: AddOrUpdateProperty mutates the dictionary we are iterating.
        var names = logEvent.Properties.Keys.ToArray();
        foreach (var name in names)
        {
            var redacted = RedactValue(logEvent.Properties[name]);
            if (redacted is not null)
            {
                logEvent.AddOrUpdateProperty(new LogEventProperty(name, redacted));
            }
        }
    }

    /// <summary>Returns a redacted copy of the value, or null when nothing changed.</summary>
    private LogEventPropertyValue? RedactValue(LogEventPropertyValue value)
    {
        switch (value)
        {
            case ScalarValue { Value: string text }:
            {
                var clean = _redactor.Redact(text);
                return string.Equals(clean, text, StringComparison.Ordinal) ? null : new ScalarValue(clean);
            }

            case SequenceValue sequence:
            {
                LogEventPropertyValue[]? replaced = null;
                for (var i = 0; i < sequence.Elements.Count; i++)
                {
                    var item = RedactValue(sequence.Elements[i]);
                    if (item is null)
                    {
                        continue;
                    }

                    replaced ??= [.. sequence.Elements];
                    replaced[i] = item;
                }

                return replaced is null ? null : new SequenceValue(replaced);
            }

            case StructureValue structure:
            {
                List<LogEventProperty>? replaced = null;
                for (var i = 0; i < structure.Properties.Count; i++)
                {
                    var property = structure.Properties[i];
                    var item = RedactValue(property.Value);
                    if (item is null)
                    {
                        continue;
                    }

                    replaced ??= [.. structure.Properties];
                    replaced[i] = new LogEventProperty(property.Name, item);
                }

                return replaced is null ? null : new StructureValue(replaced, structure.TypeTag);
            }

            case DictionaryValue dictionary:
            {
                List<KeyValuePair<ScalarValue, LogEventPropertyValue>>? replaced = null;
                for (var i = 0; i < dictionary.Elements.Count; i++)
                {
                    var pair = dictionary.Elements.ElementAt(i);
                    var item = RedactValue(pair.Value);
                    if (item is null)
                    {
                        continue;
                    }

                    replaced ??= [.. dictionary.Elements];
                    replaced[i] = new KeyValuePair<ScalarValue, LogEventPropertyValue>(pair.Key, item);
                }

                return replaced is null ? null : new DictionaryValue(replaced);
            }

            default:
                return null;
        }
    }
}
