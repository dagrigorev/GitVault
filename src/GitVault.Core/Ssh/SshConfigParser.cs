using System.Text;
using GitVault.Core.Abstractions;

namespace GitVault.Core.Ssh;

/// <summary>One directive read from an <c>ssh_config</c> file.</summary>
/// <param name="Keyword">Lower-cased keyword, e.g. <c>identityfile</c>.</param>
/// <param name="Value">Raw value, with tokens still unexpanded.</param>
/// <param name="HostPatterns">The <c>Host</c> patterns in force where the directive appeared.</param>
/// <param name="FilePath">File the directive was read from.</param>
/// <param name="LineNumber">One-based line number.</param>
public sealed record SshConfigDirective(
    string Keyword,
    string Value,
    IReadOnlyList<string> HostPatterns,
    string FilePath,
    int LineNumber);

/// <summary>
/// Reader for <c>~/.ssh/config</c>. Understands <c>Host</c> and <c>Match</c> blocks, <c>Include</c>
/// with globbing, quoted values, and the <c>%d</c> / <c>%u</c> / <c>%h</c> / <c>%r</c> / <c>%p</c>
/// / <c>%%</c> tokens that appear in <c>IdentityFile</c>.
/// </summary>
public sealed class SshConfigParser
{
    /// <summary>Include nesting limit, to bound a pathological configuration.</summary>
    public const int MaxIncludeDepth = 16;

    private readonly IPlatformPaths _paths;

    /// <summary>Creates the parser.</summary>
    /// <param name="paths">Used to expand <c>~</c> and to resolve relative includes.</param>
    public SshConfigParser(IPlatformPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    /// <summary>Reads a configuration file and everything it includes.</summary>
    /// <param name="filePath">File to read.</param>
    /// <returns>Directives in file order, includes expanded in place.</returns>
    public IReadOnlyList<SshConfigDirective> ParseFile(string filePath)
    {
        var result = new List<SshConfigDirective>();
        ParseFileInto(filePath, 0, result, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>Reads configuration text, without following includes.</summary>
    /// <param name="text">File contents.</param>
    /// <param name="originPath">Path to report in the directives.</param>
    /// <returns>Directives in file order.</returns>
    public IReadOnlyList<SshConfigDirective> ParseText(string text, string originPath)
    {
        var result = new List<SshConfigDirective>();
        ParseTextInto(text, originPath, 0, result, null);
        return result;
    }

    /// <summary>
    /// Collects every <c>IdentityFile</c> path a configuration references, with tokens expanded.
    /// </summary>
    /// <param name="directives">Directives from <see cref="ParseFile"/>.</param>
    /// <param name="userName">Value substituted for <c>%u</c>.</param>
    /// <returns>Distinct absolute paths, in the order they were first seen.</returns>
    public IReadOnlyList<string> CollectIdentityFiles(
        IEnumerable<SshConfigDirective> directives,
        string? userName = null)
    {
        ArgumentNullException.ThrowIfNull(directives);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (var directive in directives)
        {
            if (!string.Equals(directive.Keyword, "identityfile", StringComparison.Ordinal))
            {
                continue;
            }

            // %h and %r depend on the connection, not on the file, so a scan expands them to
            // the first Host pattern that is a literal name and leaves the rest alone.
            var host = directive.HostPatterns.FirstOrDefault(IsLiteralHost) ?? string.Empty;
            var expanded = _paths.Expand(ExpandTokens(directive.Value, host, userName));

            if (seen.Add(expanded))
            {
                result.Add(expanded);
            }
        }

        return result;
    }

    /// <summary>Expands the percent tokens OpenSSH defines for path options.</summary>
    /// <param name="value">Raw value.</param>
    /// <param name="host">Value substituted for <c>%h</c> and <c>%n</c>.</param>
    /// <param name="userName">Value substituted for <c>%u</c> and <c>%r</c>.</param>
    /// <returns>The expanded value.</returns>
    public string ExpandTokens(string value, string? host = null, string? userName = null)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains('%', StringComparison.Ordinal))
        {
            return value ?? string.Empty;
        }

        var user = userName ?? Environment.UserName;
        var builder = new StringBuilder(value.Length);

        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '%' || i + 1 >= value.Length)
            {
                builder.Append(value[i]);
                continue;
            }

            i++;
            switch (value[i])
            {
                case 'd': builder.Append(_paths.HomeDirectory); break;
                case 'u': builder.Append(user); break;
                case 'r': builder.Append(user); break;
                case 'h': builder.Append(host ?? string.Empty); break;
                case 'n': builder.Append(host ?? string.Empty); break;
                case 'l': builder.Append(Environment.MachineName); break;
                case 'p': builder.Append("22"); break;
                case '%': builder.Append('%'); break;
                default:
                    builder.Append('%').Append(value[i]);
                    break;
            }
        }

        return builder.ToString();
    }

    private static bool IsLiteralHost(string pattern) =>
        !pattern.Contains('*', StringComparison.Ordinal)
        && !pattern.Contains('?', StringComparison.Ordinal)
        && !pattern.StartsWith('!');

    private void ParseFileInto(
        string filePath,
        int depth,
        List<SshConfigDirective> result,
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

        if (!visited.Add(full))
        {
            return;
        }

        string text;
        try
        {
            text = File.ReadAllText(full);
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        ParseTextInto(text, full, depth, result, visited);
    }

    private void ParseTextInto(
        string text,
        string originPath,
        int depth,
        List<SshConfigDirective> result,
        HashSet<string>? visited)
    {
        IReadOnlyList<string> hostPatterns = ["*"];
        var lineNumber = 0;

        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            lineNumber++;

            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#')
            {
                continue;
            }

            // Keyword and value may be separated by whitespace or by '='.
            var separator = line.IndexOfAny([' ', '\t', '=']);
            if (separator <= 0)
            {
                continue;
            }

            var keyword = line[..separator].Trim().ToLowerInvariant();
            var value = line[(separator + 1)..].TrimStart(' ', '\t', '=').Trim();

            switch (keyword)
            {
                case "host":
                    hostPatterns = SplitPatterns(value);
                    continue;

                case "match":
                    // A Match block's conditions are evaluated at connect time. Treat it as a
                    // new scope whose patterns we do not pretend to know.
                    hostPatterns = [value];
                    continue;

                case "include" when visited is not null:
                    result.Add(new SshConfigDirective(keyword, value, hostPatterns, originPath, lineNumber));
                    foreach (var included in ResolveIncludes(value, originPath))
                    {
                        ParseFileInto(included, depth + 1, result, visited);
                    }

                    continue;
            }

            result.Add(new SshConfigDirective(keyword, Unquote(value), hostPatterns, originPath, lineNumber));
        }
    }

    private static IReadOnlyList<string> SplitPatterns(string value) =>
        [.. value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(Unquote)];

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;

    /// <summary>Resolves an <c>Include</c> value, which may be relative and may contain globs.</summary>
    private IReadOnlyList<string> ResolveIncludes(string value, string includingFile)
    {
        var results = new List<string>();

        foreach (var token in value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var pattern = Unquote(token);

            // A relative Include is resolved against ~/.ssh for a user config.
            var expanded = pattern.StartsWith('~')
                ? _paths.Expand(pattern)
                : Path.IsPathRooted(pattern)
                    ? pattern
                    : Path.Combine(Path.GetDirectoryName(includingFile) ?? _paths.DefaultSshDirectory, pattern);

            var directory = Path.GetDirectoryName(expanded);
            var fileName = Path.GetFileName(expanded);

            if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName))
            {
                continue;
            }

            try
            {
                if (fileName.Contains('*', StringComparison.Ordinal) || fileName.Contains('?', StringComparison.Ordinal))
                {
                    if (Directory.Exists(directory))
                    {
                        results.AddRange(Directory.EnumerateFiles(directory, fileName).OrderBy(f => f, StringComparer.Ordinal));
                    }
                }
                else if (File.Exists(expanded))
                {
                    results.Add(expanded);
                }
            }
            catch (IOException)
            {
                // An unreadable include is simply not included.
            }
            catch (UnauthorizedAccessException)
            {
                // Same.
            }
        }

        return results;
    }
}
