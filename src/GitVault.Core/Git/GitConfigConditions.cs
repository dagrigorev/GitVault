using System.Text;
using System.Text.RegularExpressions;
using GitVault.Core.Abstractions;

namespace GitVault.Core.Git;

/// <summary>Repository facts an <c>includeIf</c> condition can be evaluated against.</summary>
/// <param name="GitDirectory">Absolute path of the repository's <c>.git</c> directory.</param>
/// <param name="CurrentBranch">Checked-out branch name, when known.</param>
public sealed record GitConfigIncludeContext(string? GitDirectory, string? CurrentBranch)
{
    /// <summary>Builds a context from a working-tree path.</summary>
    /// <param name="repositoryPath">Working tree, or the <c>.git</c> directory itself.</param>
    /// <param name="currentBranch">Checked-out branch, when known.</param>
    /// <returns>A context, or null when no path was supplied.</returns>
    public static GitConfigIncludeContext? ForRepository(string? repositoryPath, string? currentBranch = null)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath))
        {
            return null;
        }

        var gitDirectory = Path.GetFileName(repositoryPath.TrimEnd('/', '\\')) == ".git"
            ? repositoryPath
            : Path.Combine(repositoryPath, ".git");

        return new GitConfigIncludeContext(gitDirectory, currentBranch);
    }
}

/// <summary>
/// Evaluates <c>includeIf</c> conditions.
/// </summary>
/// <remarks>
/// Supports <c>gitdir:</c>, <c>gitdir/i:</c> and <c>onbranch:</c>. <c>hasconfig:remote.*.url:</c>
/// is recognised but never matches, because evaluating it would require the fully resolved
/// configuration we are still in the middle of building.
/// VERIFY: git's exact pattern semantics against a real installation, in particular how a
/// pattern with no leading <c>~/</c>, <c>./</c> or <c>/</c> is anchored.
/// </remarks>
public static class GitConfigConditions
{
    /// <summary>Evaluates a condition.</summary>
    /// <param name="condition">Condition text, i.e. the <c>includeIf</c> subsection.</param>
    /// <param name="includingFile">File the directive appeared in, for <c>./</c> patterns.</param>
    /// <param name="context">Repository facts, or null when there is no repository.</param>
    /// <param name="paths">Used to expand <c>~</c>.</param>
    /// <returns><see langword="true"/> when the include should be followed.</returns>
    public static bool Matches(
        string condition,
        string includingFile,
        GitConfigIncludeContext? context,
        IPlatformPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (string.IsNullOrWhiteSpace(condition) || context is null)
        {
            return false;
        }

        if (TryStrip(condition, "gitdir:", out var pattern))
        {
            return MatchesGitDir(pattern, includingFile, context.GitDirectory, paths, ignoreCase: false);
        }

        if (TryStrip(condition, "gitdir/i:", out pattern))
        {
            return MatchesGitDir(pattern, includingFile, context.GitDirectory, paths, ignoreCase: true);
        }

        if (TryStrip(condition, "onbranch:", out pattern))
        {
            return context.CurrentBranch is not null
                && GlobMatches(NormalizeBranchPattern(pattern), context.CurrentBranch, ignoreCase: false);
        }

        return false;
    }

    /// <summary>Translates a git path pattern into a regular expression.</summary>
    /// <param name="pattern">Pattern using <c>*</c>, <c>**</c> and <c>?</c>.</param>
    /// <param name="ignoreCase">Whether matching is case-insensitive.</param>
    /// <returns>An anchored regular expression.</returns>
    internal static Regex BuildGlobRegex(string pattern, bool ignoreCase)
    {
        var builder = new StringBuilder("^");

        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            switch (c)
            {
                case '*' when i + 1 < pattern.Length && pattern[i + 1] == '*':
                    // '**' crosses directory separators; '**/' also matches zero directories.
                    if (i + 2 < pattern.Length && pattern[i + 2] == '/')
                    {
                        builder.Append("(?:.*/)?");
                        i += 2;
                    }
                    else
                    {
                        builder.Append(".*");
                        i++;
                    }

                    break;

                case '*':
                    builder.Append("[^/]*");
                    break;

                case '?':
                    builder.Append("[^/]");
                    break;

                default:
                    builder.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        builder.Append('$');

        var options = RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
        return new Regex(builder.ToString(), options, TimeSpan.FromSeconds(1));
    }

    private static bool TryStrip(string condition, string prefix, out string rest)
    {
        if (condition.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            rest = condition[prefix.Length..];
            return true;
        }

        rest = string.Empty;
        return false;
    }

    private static bool MatchesGitDir(
        string pattern,
        string includingFile,
        string? gitDirectory,
        IPlatformPaths paths,
        bool ignoreCase)
    {
        if (string.IsNullOrWhiteSpace(gitDirectory))
        {
            return false;
        }

        var expanded = ExpandPattern(pattern, includingFile, paths);
        var target = Normalize(gitDirectory);

        // git compares against the gitdir with a trailing slash, so a pattern ending in '/'
        // (expanded to '/**') matches the directory itself as well as everything below it.
        return GlobMatches(expanded, target, ignoreCase)
            || GlobMatches(expanded, target + "/", ignoreCase);
    }

    private static string ExpandPattern(string pattern, string includingFile, IPlatformPaths paths)
    {
        var result = pattern;

        if (result.StartsWith("~/", StringComparison.Ordinal))
        {
            result = Normalize(paths.Expand(result));
        }
        else if (result.StartsWith("./", StringComparison.Ordinal))
        {
            var directory = Path.GetDirectoryName(includingFile) ?? string.Empty;
            result = Normalize(Path.Combine(directory, result[2..]));
        }
        else if (!IsRootedPattern(result))
        {
            // A bare pattern matches anywhere in the path.
            result = "**/" + result;
        }
        else
        {
            result = Normalize(result);
        }

        if (result.EndsWith('/'))
        {
            result += "**";
        }

        return result;
    }

    private static bool IsRootedPattern(string pattern) =>
        pattern.StartsWith('/')
        || (pattern.Length > 2 && char.IsLetter(pattern[0]) && pattern[1] == ':');

    private static string NormalizeBranchPattern(string pattern) =>
        pattern.EndsWith('/') ? pattern + "**" : pattern;

    private static bool GlobMatches(string pattern, string value, bool ignoreCase) =>
        BuildGlobRegex(pattern, ignoreCase).IsMatch(value);

    private static string Normalize(string path) => path.Replace('\\', '/');
}
