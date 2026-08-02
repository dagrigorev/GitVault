using System.Text;
using GitVault.Core.Abstractions;
using GitVault.Core.Models;

namespace GitVault.Core.Git;

/// <summary>One key/value pair as read from a configuration file.</summary>
/// <param name="Section">Lower-cased section name.</param>
/// <param name="Subsection">Subsection name, case-sensitive, or null.</param>
/// <param name="Name">Lower-cased variable name.</param>
/// <param name="Value">Value after unquoting and escape processing.</param>
/// <param name="FilePath">File the entry was read from.</param>
/// <param name="LineNumber">One-based line number of the entry.</param>
/// <param name="Scope">Scope the originating file belongs to.</param>
public sealed record GitConfigEntry(
    string Section,
    string? Subsection,
    string Name,
    string Value,
    string FilePath,
    int LineNumber,
    GitConfigScope Scope)
{
    /// <summary>Fully qualified key, e.g. <c>credential.https://github.com.helper</c>.</summary>
    public string Key => Subsection is null ? $"{Section}.{Name}" : $"{Section}.{Subsection}.{Name}";
}

/// <summary>
/// Native reader for git's configuration format, used when no <c>git</c> binary is available.
/// Implements section subnames, quoting and escapes, valueless booleans, line continuation,
/// multi-valued keys, and <c>include</c> / <c>includeIf</c> resolution.
/// </summary>
/// <remarks>
/// This is a fallback. When git is present, <see cref="GitConfigService"/> shells out to it
/// instead, because git is the authority on its own format.
/// </remarks>
public sealed class GitConfigParser
{
    /// <summary>Maximum include nesting depth, matching git's own limit.</summary>
    public const int MaxIncludeDepth = 10;

    private readonly IPlatformPaths _paths;

    /// <summary>Creates the parser.</summary>
    /// <param name="paths">Used to expand <c>~</c> in include paths.</param>
    public GitConfigParser(IPlatformPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    /// <summary>Reads a configuration file and every file it includes.</summary>
    /// <param name="filePath">File to read.</param>
    /// <param name="scope">Scope the file belongs to.</param>
    /// <param name="context">Repository context used to evaluate <c>includeIf</c> conditions.</param>
    /// <returns>Entries in file order, includes expanded in place.</returns>
    public IReadOnlyList<GitConfigEntry> ParseFile(
        string filePath,
        GitConfigScope scope,
        GitConfigIncludeContext? context = null)
    {
        var entries = new List<GitConfigEntry>();
        ParseFileInto(filePath, scope, context, depth: 0, entries, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return entries;
    }

    /// <summary>Parses configuration text without touching the filesystem for includes.</summary>
    /// <param name="text">File contents.</param>
    /// <param name="originPath">Path reported in the resulting entries.</param>
    /// <param name="scope">Scope the text belongs to.</param>
    /// <returns>Entries in file order. <c>include.path</c> entries are returned, not followed.</returns>
    public IReadOnlyList<GitConfigEntry> ParseText(string text, string originPath, GitConfigScope scope)
    {
        var entries = new List<GitConfigEntry>();
        ParseTextInto(text, originPath, scope, null, MaxIncludeDepth, entries, null);
        return entries;
    }

    private void ParseFileInto(
        string filePath,
        GitConfigScope scope,
        GitConfigIncludeContext? context,
        int depth,
        List<GitConfigEntry> entries,
        HashSet<string> visited)
    {
        if (depth > MaxIncludeDepth)
        {
            return;
        }

        string full;
        try
        {
            full = Path.GetFullPath(filePath);
        }
        catch (ArgumentException)
        {
            return;
        }
        catch (NotSupportedException)
        {
            return;
        }

        // A configuration file that includes itself, directly or in a cycle, must not hang us.
        if (!visited.Add(full))
        {
            return;
        }

        string text;
        try
        {
            text = File.ReadAllText(full, Encoding.UTF8);
        }
        catch (FileNotFoundException)
        {
            return;
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        ParseTextInto(text, full, scope, context, depth, entries, visited);
    }

    private void ParseTextInto(
        string text,
        string originPath,
        GitConfigScope scope,
        GitConfigIncludeContext? context,
        int depth,
        List<GitConfigEntry> entries,
        HashSet<string>? visited)
    {
        var reader = new ConfigReader(text);
        var section = string.Empty;
        string? subsection = null;

        while (reader.TryReadItem(ref section, ref subsection, out var name, out var value, out var line))
        {
            var entry = new GitConfigEntry(section, subsection, name, value, originPath, line, scope);

            // include.path and includeIf.<condition>.path are directives, not data. git still
            // reports them from `git config --list`, so we keep them and also follow them.
            entries.Add(entry);

            if (visited is null || !string.Equals(name, "path", StringComparison.Ordinal))
            {
                continue;
            }

            var shouldInclude = section switch
            {
                "include" => true,
                "includeif" => subsection is not null
                              && GitConfigConditions.Matches(subsection, originPath, context, _paths),
                _ => false,
            };

            if (shouldInclude)
            {
                var resolved = ResolveIncludePath(value, originPath);
                if (resolved is not null)
                {
                    ParseFileInto(resolved, scope, context, depth + 1, entries, visited);
                }
            }
        }
    }

    /// <summary>Resolves an include path relative to the including file, expanding <c>~</c>.</summary>
    /// <param name="value">Raw <c>path</c> value.</param>
    /// <param name="includingFile">File the directive appeared in.</param>
    /// <returns>An absolute path, or null when it cannot be resolved.</returns>
    private string? ResolveIncludePath(string value, string includingFile)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var expanded = value.StartsWith('~') ? _paths.Expand(value) : value;

        if (Path.IsPathRooted(expanded))
        {
            return expanded;
        }

        var directory = Path.GetDirectoryName(includingFile);
        return string.IsNullOrEmpty(directory) ? null : Path.GetFullPath(Path.Combine(directory, expanded));
    }

    /// <summary>
    /// Character-level scanner over a configuration file. Kept as a struct-like private class so
    /// that the grammar rules sit in one readable place.
    /// </summary>
    private sealed class ConfigReader(string text)
    {
        private readonly string _text = StripBom(text);
        private int _index;
        private int _line = 1;

        /// <summary>Reads the next variable, updating the current section as it goes.</summary>
        /// <param name="section">Current section; updated when a header is crossed.</param>
        /// <param name="subsection">Current subsection; updated when a header is crossed.</param>
        /// <param name="name">Variable name that was read.</param>
        /// <param name="value">Variable value that was read.</param>
        /// <param name="lineNumber">Line the variable started on.</param>
        /// <returns><see langword="true"/> when a variable was produced.</returns>
        internal bool TryReadItem(
            ref string section,
            ref string? subsection,
            out string name,
            out string value,
            out int lineNumber)
        {
            while (_index < _text.Length)
            {
                SkipBlanks();
                if (_index >= _text.Length)
                {
                    break;
                }

                var c = _text[_index];

                if (c is '\n')
                {
                    Advance();
                    continue;
                }

                if (c is '#' or ';')
                {
                    SkipToEndOfLine();
                    continue;
                }

                if (c == '[')
                {
                    ReadSectionHeader(ref section, ref subsection);
                    continue;
                }

                lineNumber = _line;
                if (TryReadVariable(out name, out value))
                {
                    return true;
                }

                continue;
            }

            name = string.Empty;
            value = string.Empty;
            lineNumber = 0;
            return false;
        }

        private static string StripBom(string input) =>
            input.Length > 0 && input[0] == '\uFEFF' ? input[1..] : input;

        private void Advance()
        {
            if (_text[_index] == '\n')
            {
                _line++;
            }

            _index++;
        }

        private void SkipBlanks()
        {
            while (_index < _text.Length && (_text[_index] == ' ' || _text[_index] == '\t' || _text[_index] == '\r'))
            {
                _index++;
            }
        }

        private void SkipToEndOfLine()
        {
            while (_index < _text.Length && _text[_index] != '\n')
            {
                _index++;
            }
        }

        private void ReadSectionHeader(ref string section, ref string? subsection)
        {
            Advance(); // consume '['

            var name = new StringBuilder();
            string? sub = null;

            while (_index < _text.Length && _text[_index] != ']' && _text[_index] != '\n')
            {
                var c = _text[_index];

                if (c == '"')
                {
                    sub = ReadQuotedSubsection();
                    continue;
                }

                if (c is ' ' or '\t')
                {
                    _index++;
                    continue;
                }

                name.Append(c);
                _index++;
            }

            if (_index < _text.Length && _text[_index] == ']')
            {
                _index++;
            }

            SkipToEndOfLine();

            var raw = name.ToString();

            // [section.sub] is the legacy spelling of [section "sub"]. The dotted form is
            // lower-cased whole; only the quoted form keeps its case.
            if (sub is null)
            {
                var dot = raw.IndexOf('.', StringComparison.Ordinal);
                if (dot >= 0)
                {
                    section = raw[..dot].ToLowerInvariant();
                    subsection = raw[(dot + 1)..].ToLowerInvariant();
                    return;
                }
            }

            section = raw.ToLowerInvariant();
            subsection = sub;
        }

        private string ReadQuotedSubsection()
        {
            _index++; // consume opening quote
            var builder = new StringBuilder();

            while (_index < _text.Length && _text[_index] != '"' && _text[_index] != '\n')
            {
                if (_text[_index] == '\\' && _index + 1 < _text.Length)
                {
                    // Inside a subsection only \" and \\ are meaningful; anything else is literal.
                    _index++;
                    builder.Append(_text[_index]);
                    _index++;
                    continue;
                }

                builder.Append(_text[_index]);
                _index++;
            }

            if (_index < _text.Length && _text[_index] == '"')
            {
                _index++;
            }

            return builder.ToString();
        }

        private bool TryReadVariable(out string name, out string value)
        {
            var builder = new StringBuilder();
            while (_index < _text.Length)
            {
                var c = _text[_index];
                if (char.IsLetterOrDigit(c) || c == '-')
                {
                    builder.Append(c);
                    _index++;
                    continue;
                }

                break;
            }

            name = builder.ToString().ToLowerInvariant();
            if (name.Length == 0)
            {
                // Not a variable start: skip the line so a malformed file cannot stall the scan.
                SkipToEndOfLine();
                value = string.Empty;
                return false;
            }

            SkipBlanks();

            if (_index >= _text.Length || _text[_index] == '\n' || _text[_index] == '#' || _text[_index] == ';')
            {
                // A name with no '=' is a boolean true, per git's rules.
                SkipToEndOfLine();
                value = "true";
                return true;
            }

            if (_text[_index] != '=')
            {
                SkipToEndOfLine();
                value = "true";
                return true;
            }

            _index++; // consume '='
            value = ReadValue();
            return true;
        }

        private string ReadValue()
        {
            var builder = new StringBuilder();
            var inQuotes = false;
            var trailingWhitespace = 0;
            var started = false;

            while (_index < _text.Length)
            {
                var c = _text[_index];

                if (c == '\n' && !inQuotes)
                {
                    break;
                }

                if (!started && !inQuotes && (c == ' ' || c == '\t' || c == '\r'))
                {
                    _index++;
                    continue;
                }

                if (c == '"')
                {
                    inQuotes = !inQuotes;
                    started = true;
                    _index++;
                    trailingWhitespace = 0;
                    continue;
                }

                if (c == '\\' && _index + 1 < _text.Length)
                {
                    var escaped = _text[_index + 1];
                    if (escaped == '\n')
                    {
                        // Line continuation: consume both characters and keep reading.
                        _index++;
                        Advance();
                        continue;
                    }

                    if (escaped == '\r' && _index + 2 < _text.Length && _text[_index + 2] == '\n')
                    {
                        _index += 2;
                        Advance();
                        continue;
                    }

                    builder.Append(escaped switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        'b' => '\b',
                        '\\' => '\\',
                        '"' => '"',
                        _ => escaped,
                    });

                    started = true;
                    trailingWhitespace = 0;
                    _index += 2;
                    continue;
                }

                if (!inQuotes && (c == '#' || c == ';'))
                {
                    break;
                }

                if (!inQuotes && (c == ' ' || c == '\t' || c == '\r'))
                {
                    // Trailing whitespace outside quotes is dropped, inner whitespace is kept, so
                    // buffer it until we know whether more content follows.
                    trailingWhitespace++;
                    builder.Append(c);
                    _index++;
                    continue;
                }

                started = true;
                trailingWhitespace = 0;
                builder.Append(c);
                _index++;
            }

            SkipToEndOfLine();

            if (trailingWhitespace > 0)
            {
                builder.Length -= trailingWhitespace;
            }

            return builder.ToString();
        }
    }
}
