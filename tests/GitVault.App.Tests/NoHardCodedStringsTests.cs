using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace GitVault.App.Tests;

/// <summary>
/// Guards the "no hard-coded user-facing string" rule by scanning the sources rather than the
/// compiled output, because that is where an untranslated caption is introduced.
/// </summary>
public sealed class NoHardCodedStringsTests
{
    /// <summary>XAML attributes whose value is shown to the user.</summary>
    private static readonly Regex UserFacingAttribute = new(
        "\\b(Text|Content|Header|Watermark|ToolTip\\.Tip|AutomationProperties\\.Name|Title)\\s*=\\s*\"([^\"]*)\"",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// String literals passed to a caption-like assignment in a view model. Resource keys and
    /// key prefixes are excluded by the allow-list below.
    /// </summary>
    private static readonly Regex ViewModelLiteral = new(
        "(?<!\\w)\"(?<value>[^\"\\\\]{2,})\"",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    private static readonly string[] AllowedXamlValues =
    [
        // Purely structural or numeric values that happen to sit on a user-facing attribute.
        string.Empty,
    ];

    [Fact]
    public void No_xaml_file_contains_a_literal_user_facing_string()
    {
        var offenders = new List<string>();

        foreach (var file in EnumerateSources("*.axaml"))
        {
            var text = File.ReadAllText(file);
            foreach (Match match in UserFacingAttribute.Matches(text))
            {
                var value = match.Groups[2].Value;

                // Bindings and markup extensions start with '{'; those are the localized path.
                if (value.StartsWith('{') || AllowedXamlValues.Contains(value))
                {
                    continue;
                }

                offenders.Add($"{Path.GetFileName(file)}: {match.Value}");
            }
        }

        offenders.Should().BeEmpty(
            "every user-visible caption must come from {loc:Tr} or a localized view-model property");
    }

    [Fact]
    public void No_view_model_builds_a_caption_from_a_literal()
    {
        var offenders = new List<string>();

        foreach (var file in EnumerateSources("*.cs").Where(f => f.Contains("ViewModels", StringComparison.Ordinal)))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                // Serilog message templates are diagnostics, not UI. They stay English on
                // purpose: a log a user pastes into an issue must be readable by the maintainer.
                if (IsPartOfLogStatement(lines, i))
                {
                    continue;
                }

                foreach (Match match in ViewModelLiteral.Matches(line))
                {
                    var value = match.Groups["value"].Value;
                    if (IsAllowedCodeLiteral(value))
                    {
                        continue;
                    }

                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}: \"{value}\"");
                }
            }
        }

        offenders.Should().BeEmpty("view models must reference resource keys, not literal text");
    }

    /// <summary>
    /// True when the line belongs to a logging call. Looks back a few lines so that a wrapped
    /// call is covered too, and stops at a statement terminator.
    /// </summary>
    private static bool IsPartOfLogStatement(IReadOnlyList<string> lines, int index)
    {
        for (var i = index; i >= 0 && index - i < 6; i--)
        {
            var line = lines[i];
            if (Regex.IsMatch(line, @"\bLog\.(Verbose|Debug|Information|Warning|Error|Fatal)\b",
                    RegexOptions.None, TimeSpan.FromSeconds(1)))
            {
                return true;
            }

            // A completed statement above us means we are not inside a wrapped call.
            if (i < index && line.TrimEnd().EndsWith(';'))
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Literals that are not user-facing text: resource keys, key prefixes, culture names and
    /// language endonyms, which are deliberately identical in every language.
    /// </summary>
    private static bool IsAllowedCodeLiteral(string value)
    {
        // Proper nouns: language endonyms, which are written the same in every language, and the
        // names of shells, which are commands the user types rather than words to translate.
        string[] properNouns =
        [
            "English", "Русский", "简体中文",
            "bash", "zsh", "fish", "PowerShell", "cmd",
        ];

        if (properNouns.Contains(value, StringComparer.Ordinal))
        {
            return true;
        }

        // BCP-47 culture names such as en-US, ru-RU, zh-Hans.
        if (Regex.IsMatch(value, "^[a-z]{2}(-[A-Za-z]{2,4})?$", RegexOptions.None, TimeSpan.FromSeconds(1)))
        {
            return true;
        }

        // Icon resource keys, e.g. IconAgents. A view model names the icon it wants; the lookup
        // converter turns that into a geometry, so this is a resource key and not display text.
        if (Regex.IsMatch(value, "^Icon[A-Z][A-Za-z0-9]*$", RegexOptions.None, TimeSpan.FromSeconds(1)))
        {
            return true;
        }

        // Resource keys and key prefixes: Page_Section_Element, or Plural_Thing.
        return Regex.IsMatch(value, "^[A-Z][A-Za-z0-9]*(_[A-Za-z0-9]+)+$", RegexOptions.None, TimeSpan.FromSeconds(1));
    }

    private static IEnumerable<string> EnumerateSources(string pattern)
    {
        var root = FindRepositoryRoot();
        var appDirectory = Path.Combine(root, "src", "GitVault.App");

        return Directory.EnumerateFiles(appDirectory, pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

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
}
