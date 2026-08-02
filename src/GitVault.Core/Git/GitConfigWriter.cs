using System.Text;

namespace GitVault.Core.Git;

/// <summary>
/// Edits a git configuration file in place, touching only the lines that must change.
/// Comments, ordering, indentation, the byte-order mark and the file's line endings all survive.
/// </summary>
/// <remarks>
/// Used only when no <c>git</c> binary is available. Rewriting the whole file would be far
/// simpler and is exactly what we refuse to do: people keep hand-written comments in these files.
/// </remarks>
public sealed class GitConfigWriter
{
    /// <summary>Sets a variable, creating the section or the file when necessary.</summary>
    /// <param name="filePath">Configuration file to edit.</param>
    /// <param name="section">Lower-case section name.</param>
    /// <param name="subsection">Subsection, or null.</param>
    /// <param name="name">Lower-case variable name.</param>
    /// <param name="value">Value to store.</param>
    public void Set(string filePath, string section, string? subsection, string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(section);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);

        var document = GitConfigDocument.Load(filePath);
        document.Set(section, subsection, name, value);
        document.Save(filePath);
    }

    /// <summary>Removes every occurrence of a variable. The section header is left in place.</summary>
    /// <param name="filePath">Configuration file to edit.</param>
    /// <param name="section">Lower-case section name.</param>
    /// <param name="subsection">Subsection, or null.</param>
    /// <param name="name">Lower-case variable name.</param>
    /// <returns><see langword="true"/> when at least one line was removed.</returns>
    public bool Unset(string filePath, string section, string? subsection, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!File.Exists(filePath))
        {
            return false;
        }

        var document = GitConfigDocument.Load(filePath);
        var removed = document.Unset(section, subsection, name);
        if (removed)
        {
            document.Save(filePath);
        }

        return removed;
    }

    /// <summary>
    /// Removes a section header whose body no longer holds any variable.
    /// </summary>
    /// <remarks>
    /// <c>git config --unset</c> removes the variable but leaves the <c>[section]</c> header
    /// behind. That is harmless to git, but it means a file cannot be restored byte-for-byte
    /// after GitVault added and then removed a key in a section that did not exist before.
    /// Cleaning up an emptied section is what makes deactivation exact.
    ///
    /// A section is only removed when nothing but blank lines remains, so a comment the user
    /// wrote inside it keeps the header alive.
    /// </remarks>
    /// <param name="filePath">Configuration file to edit.</param>
    /// <param name="section">Lower-case section name.</param>
    /// <param name="subsection">Subsection, or null.</param>
    /// <returns><see langword="true"/> when a header was removed.</returns>
    public bool RemoveSectionIfEmpty(string filePath, string section, string? subsection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!File.Exists(filePath))
        {
            return false;
        }

        var document = GitConfigDocument.Load(filePath);
        if (!document.RemoveSectionIfEmpty(section, subsection))
        {
            return false;
        }

        document.Save(filePath);
        return true;
    }

    /// <summary>Quotes a value only when git's grammar requires it.</summary>
    /// <param name="value">Raw value.</param>
    /// <returns>The value as it should appear on the right of the <c>=</c>.</returns>
    internal static string Encode(string value)
    {
        var needsQuotes = value.Length == 0
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1])
            || value.Contains('#', StringComparison.Ordinal)
            || value.Contains(';', StringComparison.Ordinal)
            || value.Contains('"', StringComparison.Ordinal)
            || value.Contains('\\', StringComparison.Ordinal)
            || value.Contains('\n', StringComparison.Ordinal)
            || value.Contains('\t', StringComparison.Ordinal);

        var escaped = value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);

        return needsQuotes ? "\"" + escaped + "\"" : escaped;
    }
}

/// <summary>A configuration file split into lines, remembering how it was formatted.</summary>
internal sealed class GitConfigDocument
{
    private readonly List<string> _lines;
    private readonly string _newLine;
    private readonly bool _hasBom;
    private readonly bool _endedWithNewLine;

    private GitConfigDocument(List<string> lines, string newLine, bool hasBom, bool endedWithNewLine)
    {
        _lines = lines;
        _newLine = newLine;
        _hasBom = hasBom;
        _endedWithNewLine = endedWithNewLine;
    }

    internal static GitConfigDocument Load(string filePath)
    {
        if (!File.Exists(filePath))
        {
            // A file we create ourselves gets LF, which is what git writes on every platform.
            return new GitConfigDocument([], "\n", hasBom: false, endedWithNewLine: true);
        }

        var bytes = File.ReadAllBytes(filePath);
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = new UTF8Encoding(false).GetString(hasBom ? bytes.AsSpan(3) : bytes);

        // CRLF wins only if it is the dominant ending, so a stray CR cannot flip the whole file.
        var crlf = CountOccurrences(text, "\r\n");
        var lf = text.Count(c => c == '\n') - crlf;
        var newLine = crlf > lf ? "\r\n" : "\n";

        var endedWithNewLine = text.Length == 0 || text.EndsWith('\n');
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        // Split leaves a trailing empty element for a file that ends with a newline.
        if (endedWithNewLine && lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return new GitConfigDocument(lines, newLine, hasBom, endedWithNewLine);
    }

    internal void Save(string filePath)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < _lines.Count; i++)
        {
            builder.Append(_lines[i]);
            if (i < _lines.Count - 1 || _endedWithNewLine)
            {
                builder.Append(_newLine);
            }
        }

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var encoding = new UTF8Encoding(_hasBom);
        var temp = filePath + ".gitvault.tmp";
        File.WriteAllText(temp, builder.ToString(), encoding);
        File.Move(temp, filePath, overwrite: true);
    }

    internal void Set(string section, string? subsection, string name, string value)
    {
        var range = FindSection(section, subsection);
        var encoded = GitConfigWriter.Encode(value);

        if (range is null)
        {
            if (_lines.Count > 0 && _lines[^1].Trim().Length > 0)
            {
                _lines.Add(string.Empty);
            }

            _lines.Add(FormatHeader(section, subsection));
            _lines.Add($"\t{name} = {encoded}");
            return;
        }

        var (start, end) = range.Value;

        for (var i = start + 1; i < end; i++)
        {
            if (!TryReadVariableName(_lines[i], out var existing, out var indent))
            {
                continue;
            }

            if (string.Equals(existing, name, StringComparison.OrdinalIgnoreCase))
            {
                _lines[i] = $"{indent}{existing} = {encoded}";
                return;
            }
        }

        // The section exists but not the variable: insert after the last non-blank line of it.
        var insertAt = end;
        while (insertAt > start + 1 && _lines[insertAt - 1].Trim().Length == 0)
        {
            insertAt--;
        }

        _lines.Insert(insertAt, $"\t{name} = {encoded}");
    }

    internal bool Unset(string section, string? subsection, string name)
    {
        var range = FindSection(section, subsection);
        if (range is null)
        {
            return false;
        }

        var (start, end) = range.Value;
        var removed = false;

        for (var i = end - 1; i > start; i--)
        {
            if (TryReadVariableName(_lines[i], out var existing, out _)
                && string.Equals(existing, name, StringComparison.OrdinalIgnoreCase))
            {
                _lines.RemoveAt(i);
                removed = true;
            }
        }

        return removed;
    }

    /// <summary>Removes a section whose body contains nothing but blank lines.</summary>
    /// <param name="section">Lower-case section name.</param>
    /// <param name="subsection">Subsection, or null.</param>
    /// <returns><see langword="true"/> when the header was removed.</returns>
    internal bool RemoveSectionIfEmpty(string section, string? subsection)
    {
        var range = FindSection(section, subsection);
        if (range is null)
        {
            return false;
        }

        var (start, end) = range.Value;

        for (var i = start + 1; i < end; i++)
        {
            // A comment counts as content: the user put it there on purpose.
            if (_lines[i].Trim().Length > 0)
            {
                return false;
            }
        }

        _lines.RemoveRange(start, end - start);
        return true;
    }

    /// <summary>Finds the header line and the exclusive end index of a section.</summary>
    private (int Start, int End)? FindSection(string section, string? subsection)
    {
        for (var i = 0; i < _lines.Count; i++)
        {
            if (!TryReadHeader(_lines[i], out var foundSection, out var foundSubsection))
            {
                continue;
            }

            if (!string.Equals(foundSection, section, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(foundSubsection, subsection, StringComparison.Ordinal))
            {
                continue;
            }

            var end = i + 1;
            while (end < _lines.Count && !TryReadHeader(_lines[end], out _, out _))
            {
                end++;
            }

            return (i, end);
        }

        return null;
    }

    private static string FormatHeader(string section, string? subsection) =>
        subsection is null
            ? $"[{section}]"
            : $"[{section} \"{subsection.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"]";

    private static bool TryReadHeader(string line, out string section, out string? subsection)
    {
        section = string.Empty;
        subsection = null;

        var trimmed = line.Trim();
        if (trimmed.Length < 3 || trimmed[0] != '[')
        {
            return false;
        }

        var close = trimmed.LastIndexOf(']');
        if (close <= 1)
        {
            return false;
        }

        var inner = trimmed[1..close].Trim();
        var quote = inner.IndexOf('"', StringComparison.Ordinal);

        if (quote < 0)
        {
            var dot = inner.IndexOf('.', StringComparison.Ordinal);
            if (dot >= 0)
            {
                section = inner[..dot].ToLowerInvariant();
                subsection = inner[(dot + 1)..].ToLowerInvariant();
            }
            else
            {
                section = inner.ToLowerInvariant();
            }

            return section.Length > 0;
        }

        section = inner[..quote].Trim().ToLowerInvariant();
        var closingQuote = inner.LastIndexOf('"');
        if (closingQuote > quote)
        {
            subsection = Unescape(inner[(quote + 1)..closingQuote]);
        }

        return section.Length > 0;
    }

    private static string Unescape(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                builder.Append(value[i + 1]);
                i++;
                continue;
            }

            builder.Append(value[i]);
        }

        return builder.ToString();
    }

    private static bool TryReadVariableName(string line, out string name, out string indent)
    {
        name = string.Empty;
        indent = "\t";

        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] is '#' or ';' or '[')
        {
            return false;
        }

        indent = line[..(line.Length - trimmed.Length)];
        if (indent.Length == 0)
        {
            indent = "\t";
        }

        var end = 0;
        while (end < trimmed.Length && (char.IsLetterOrDigit(trimmed[end]) || trimmed[end] == '-'))
        {
            end++;
        }

        if (end == 0)
        {
            return false;
        }

        name = trimmed[..end].ToLowerInvariant();
        return true;
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = text.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
