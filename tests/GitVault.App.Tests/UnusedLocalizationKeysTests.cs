using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using GitVault.Localization;
using Xunit;

namespace GitVault.App.Tests;

/// <summary>
/// Every localized string is one somebody can actually see.
/// </summary>
/// <remarks>
/// A string nobody reaches is worse than no string. It costs three translations, it survives every
/// review because it looks like working text, and the next person to need that wording wires up
/// the stale one instead of writing the right one — which is exactly how an interface ends up
/// saying something its author never meant.
///
/// The check has to allow for keys nothing names in full, because several families are composed at
/// run time: a plural key from a prefix and a category, a warning's title and body from a code
/// defined in the engine, a status from an enumeration member. A key counts as reached when any
/// prefix of its name appears as a literal anywhere in the sources, which is what those call sites
/// look like.
/// </remarks>
public sealed class UnusedLocalizationKeysTests
{
    [Fact]
    public void No_localized_string_is_left_with_nothing_to_show_it()
    {
        var sources = ReadSources();
        var unreachable = new List<string>();

        foreach (var key in Keys.All)
        {
            if (Regex.IsMatch(sources, @"\b" + Regex.Escape(key) + @"\b"))
            {
                continue;
            }

            if (IsComposedAtRunTime(key, sources))
            {
                continue;
            }

            unreachable.Add(key);
        }

        unreachable.Should().BeEmpty(
            "a string nothing can reach is a translation nobody sees and a wording the next "
            + "person will wire up by mistake");
    }

    [Fact]
    public void Every_key_the_interface_names_exists_in_the_resource_file()
    {
        // The other direction, and the one that fails loudly at run time rather than quietly: a
        // {loc:Tr Something} naming a key that was renamed shows the key itself to the user.
        var declared = Keys.All.ToHashSet(StringComparer.Ordinal);
        var missing = new List<string>();

        foreach (var file in EnumerateSources("*.axaml"))
        {
            foreach (Match match in Regex.Matches(File.ReadAllText(file), @"\{loc:Tr\s+(?<key>\w+)"))
            {
                var key = match.Groups["key"].Value;

                if (!declared.Contains(key))
                {
                    missing.Add(Path.GetFileName(file) + ": " + key);
                }
            }
        }

        missing.Should().BeEmpty("a name with no string behind it renders as the name");
    }

    /// <summary>True when some call site builds this key from a prefix rather than naming it.</summary>
    private static bool IsComposedAtRunTime(string key, string sources)
    {
        var parts = key.Split('_');

        for (var i = 1; i < parts.Length; i++)
        {
            var prefix = string.Join('_', parts[..i]);

            if (sources.Contains('"' + prefix + '"', StringComparison.Ordinal))
            {
                return true;
            }
        }

        // A warning's title and body are built from a code the engine publishes as a constant.
        return key.StartsWith("Warning_", StringComparison.Ordinal)
            && (key.EndsWith("_Title", StringComparison.Ordinal) || key.EndsWith("_Body", StringComparison.Ordinal))
            && sources.Contains(
                '"' + key["Warning_".Length..key.LastIndexOf('_')] + '"',
                StringComparison.Ordinal);
    }

    /// <summary>Reads every source file the interface and the engine are written in.</summary>
    private static string ReadSources()
    {
        var root = FindRepositoryRoot();
        var text = new List<string>();

        foreach (var project in (string[])["GitVault.App", "GitVault.Core", "GitVault.Clients", "GitVault.Localization"])
        {
            var directory = Path.Combine(root, "src", project);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories))
            {
                if (!file.EndsWith(".cs", StringComparison.Ordinal) && !file.EndsWith(".axaml", StringComparison.Ordinal))
                {
                    continue;
                }

                // The generated key list names every key by definition, so it proves nothing.
                if (file.EndsWith("Keys.g.cs", StringComparison.Ordinal) || IsBuildOutput(file))
                {
                    continue;
                }

                text.Add(File.ReadAllText(file));
            }
        }

        return string.Join('\n', text);
    }

    private static IEnumerable<string> EnumerateSources(string pattern) =>
        Directory
            .EnumerateFiles(Path.Combine(FindRepositoryRoot(), "src"), pattern, SearchOption.AllDirectories)
            .Where(f => !IsBuildOutput(f));

    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GitVault.sln")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the tests must run from inside the repository");
        return directory!.FullName;
    }

    /// <summary>Reads the source of truth, so the test fails on the file people edit.</summary>
    private static IReadOnlyList<string> ReadDeclaredKeys()
    {
        var path = Path.Combine(FindRepositoryRoot(), "build", "loc", "strings.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        return [.. document.RootElement.EnumerateArray().Select(e => e.GetProperty("key").GetString()!)];
    }

    [Fact]
    public void The_generated_key_list_matches_the_file_it_was_generated_from()
    {
        // Cheap, and it catches the one mistake that makes every other localization test lie:
        // editing strings.json and forgetting to regenerate.
        ReadDeclaredKeys().Should().BeEquivalentTo(Keys.All);
    }
}
