using System.Text;

namespace GitVault.Core.Profiles;

/// <summary>
/// Renders the difference between two versions of a file's text.
/// </summary>
/// <remarks>
/// The preview is the thing standing between a user and a write they did not intend, so it has to
/// be readable. Printing a whole file twice — every line as a removal, every line as an addition —
/// is technically honest and practically useless: nobody reads four hundred lines to find the one
/// that changed, which means in practice nobody reads the preview at all.
///
/// The method is deliberately the simple one. The common prefix and suffix are trimmed, and what
/// is left is shown as removals followed by additions, with a little context on each side. For the
/// ordinary edit — a line changed, a rule added — that is exactly the right output. For a
/// wholesale rewrite it shows everything, which is also right, because that is what is happening.
///
/// No inner alignment is attempted. A cleverer diff would sometimes produce a shorter hunk, and
/// would sometimes produce a misleading one by pairing lines that have nothing to do with each
/// other; a hunk that is honest about its bounds is worth more here than one that is short.
/// </remarks>
public static class TextDiff
{
    /// <summary>Lines of unchanged text shown either side of a change.</summary>
    private const int ContextLines = 2;

    /// <summary>Renders a line-level difference, or a note that nothing changed.</summary>
    /// <param name="before">Text as it stands, or null when the file does not exist.</param>
    /// <param name="after">Text as it would be, or null when the file would be removed.</param>
    /// <returns>The difference, one line per entry.</returns>
    public static IReadOnlyList<DiffLine> Render(string? before, string? after)
    {
        var oldLines = Split(before);
        var newLines = Split(after);

        var prefix = 0;
        while (prefix < oldLines.Count
               && prefix < newLines.Count
               && string.Equals(oldLines[prefix], newLines[prefix], StringComparison.Ordinal))
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < oldLines.Count - prefix
               && suffix < newLines.Count - prefix
               && string.Equals(
                   oldLines[^(suffix + 1)],
                   newLines[^(suffix + 1)],
                   StringComparison.Ordinal))
        {
            suffix++;
        }

        var removed = oldLines.Skip(prefix).Take(oldLines.Count - prefix - suffix).ToList();
        var added = newLines.Skip(prefix).Take(newLines.Count - prefix - suffix).ToList();

        if (removed.Count == 0 && added.Count == 0)
        {
            return [];
        }

        var lines = new List<DiffLine>();

        // Context before, and a marker when there is more of the file above it.
        var contextStart = Math.Max(0, prefix - ContextLines);
        if (contextStart > 0)
        {
            lines.Add(new DiffLine(DiffLineKind.Elision, string.Empty, contextStart));
        }

        for (var i = contextStart; i < prefix; i++)
        {
            lines.Add(new DiffLine(DiffLineKind.Context, oldLines[i], i + 1));
        }

        for (var i = 0; i < removed.Count; i++)
        {
            lines.Add(new DiffLine(DiffLineKind.Removal, removed[i], prefix + i + 1));
        }

        for (var i = 0; i < added.Count; i++)
        {
            lines.Add(new DiffLine(DiffLineKind.Addition, added[i], prefix + i + 1));
        }

        var trailing = oldLines.Count - suffix;
        for (var i = trailing; i < Math.Min(oldLines.Count, trailing + ContextLines); i++)
        {
            lines.Add(new DiffLine(DiffLineKind.Context, oldLines[i], i + 1));
        }

        var remaining = oldLines.Count - (trailing + ContextLines);
        if (remaining > 0)
        {
            lines.Add(new DiffLine(DiffLineKind.Elision, string.Empty, remaining));
        }

        return lines;
    }

    /// <summary>Renders a line-level difference as plain text, for a plan's preview string.</summary>
    /// <param name="before">Text as it stands.</param>
    /// <param name="after">Text as it would be.</param>
    /// <returns>The difference as text.</returns>
    public static string RenderText(string? before, string? after)
    {
        var builder = new StringBuilder();

        foreach (var line in Render(before, after))
        {
            builder.Append(line.Kind switch
            {
                DiffLineKind.Addition => "  + ",
                DiffLineKind.Removal => "  - ",
                DiffLineKind.Elision => "  ",
                _ => "    ",
            });

            builder.Append(line.Kind == DiffLineKind.Elision ? "…" : line.Text).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Splits text into lines without losing or inventing a trailing one.
    /// </summary>
    /// <remarks>
    /// A file ending in a newline has that newline as a terminator, not as an empty last line, and
    /// a diff that showed one would report a change nobody made. Carriage returns are stripped for
    /// comparison only; what gets written is decided by the caller, which knows the file's own
    /// line ending.
    /// </remarks>
    private static IReadOnlyList<string> Split(string? text)
    {
        if (text is null)
        {
            return [];
        }

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n').ToList();

        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }
}

/// <summary>What one line of a rendered difference is.</summary>
public enum DiffLineKind
{
    /// <summary>Unchanged, shown for orientation.</summary>
    Context = 0,

    /// <summary>Present before and not after.</summary>
    Removal,

    /// <summary>Present after and not before.</summary>
    Addition,

    /// <summary>A run of unchanged lines that is not shown.</summary>
    Elision,
}

/// <summary>One line of a rendered difference.</summary>
/// <param name="Kind">What the line is.</param>
/// <param name="Text">The line's text; empty for an elision.</param>
/// <param name="Number">Line number, or for an elision the number of lines hidden.</param>
public sealed record DiffLine(DiffLineKind Kind, string Text, int Number);
