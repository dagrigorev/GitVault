using FluentAssertions;
using GitVault.Core.Git;
using GitVault.Core.Models;
using GitVault.Core.Platform;
using Xunit;

namespace GitVault.Core.Tests;

public sealed class GitConfigParserTests : IDisposable
{
    private sealed class TestPaths(string home) : PlatformPathsBase
    {
        private readonly string _home = home;

        public override string AppDataDirectory => Path.Combine(_home, ".gitvault");

        public override IReadOnlyList<string> SystemGitConfigCandidates => [];

        public override IReadOnlyList<string> AdditionalKeyDirectories => [];
    }

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "gitvault-cfg", Guid.NewGuid().ToString("N"));

    private readonly GitConfigParser _parser;

    public GitConfigParserTests()
    {
        Directory.CreateDirectory(_root);
        _parser = new GitConfigParser(new TestPaths(_root));
    }

    private IReadOnlyList<GitConfigEntry> Parse(string text) =>
        _parser.ParseText(text, Path.Combine(_root, ".gitconfig"), GitConfigScope.Global);

    [Fact]
    public void Reads_simple_sections()
    {
        var entries = Parse("""
            [user]
                name = Ada Lovelace
                email = ada@example.com
            """);

        entries.Should().HaveCount(2);
        entries[0].Key.Should().Be("user.name");
        entries[0].Value.Should().Be("Ada Lovelace");
        entries[1].Key.Should().Be("user.email");
        entries[1].LineNumber.Should().Be(3);
    }

    [Fact]
    public void Lower_cases_section_and_name_but_not_the_subsection()
    {
        var entries = Parse("""
            [Credential "https://GitHub.com"]
                HELPER = manager
            """);

        entries.Should().ContainSingle();
        entries[0].Section.Should().Be("credential");
        entries[0].Subsection.Should().Be("https://GitHub.com");
        entries[0].Name.Should().Be("helper");
        entries[0].Key.Should().Be("credential.https://GitHub.com.helper");
    }

    [Fact]
    public void Supports_the_dotted_subsection_spelling()
    {
        var entries = Parse("""
            [branch.Main]
                remote = origin
            """);

        entries[0].Section.Should().Be("branch");
        entries[0].Subsection.Should().Be("main", "the dotted form is lower-cased whole");
    }

    [Fact]
    public void Subsection_names_may_contain_dots()
    {
        var entries = Parse("""
            [credential "https://git.example.com:8443"]
                username = octocat
            """);

        entries[0].Key.Should().Be("credential.https://git.example.com:8443.username");
    }

    [Fact]
    public void Keeps_every_value_of_a_multi_valued_key()
    {
        var entries = Parse("""
            [credential]
                helper =
                helper = manager
            """);

        entries.Should().HaveCount(2);
        entries[0].Value.Should().BeEmpty();
        entries[1].Value.Should().Be("manager");
    }

    [Fact]
    public void A_name_without_a_value_is_boolean_true()
    {
        var entries = Parse("""
            [core]
                bare
                filemode = false
            """);

        entries[0].Key.Should().Be("core.bare");
        entries[0].Value.Should().Be("true");
        entries[1].Value.Should().Be("false");
    }

    [Theory]
    [InlineData("[user]\n\tname = \"  Ada  \"\n", "  Ada  ")]
    [InlineData("[user]\n\tname = Ada   \n", "Ada")]
    [InlineData("[user]\n\tname = Ada Lovelace # not a comment marker inside\n", "Ada Lovelace")]
    [InlineData("[user]\n\tname = \"Ada # Lovelace\"\n", "Ada # Lovelace")]
    [InlineData("[user]\n\tname = a\\tb\n", "a\tb")]
    [InlineData("[user]\n\tname = a\\nb\n", "a\nb")]
    [InlineData("[user]\n\tname = C:\\\\keys\n", "C:\\keys")]
    [InlineData("[user]\n\tname = say \\\"hi\\\"\n", "say \"hi\"")]
    public void Handles_quoting_and_escapes(string text, string expected) =>
        Parse(text)[0].Value.Should().Be(expected);

    [Fact]
    public void Supports_line_continuation()
    {
        var entries = Parse("[alias]\n\tlg = log --graph \\\n--oneline\n");

        entries.Should().ContainSingle();
        entries[0].Value.Should().Be("log --graph --oneline");
    }

    [Fact]
    public void Ignores_comments_and_blank_lines()
    {
        var entries = Parse("""
            # a hash comment
            ; a semicolon comment

            [user]
            ; another
                name = Ada
            """);

        entries.Should().ContainSingle();
        entries[0].Value.Should().Be("Ada");
    }

    [Fact]
    public void Handles_crlf_line_endings()
    {
        var entries = Parse("[user]\r\n\tname = Ada\r\n\temail = ada@example.com\r\n");

        entries.Should().HaveCount(2);
        entries[0].Value.Should().Be("Ada");
        entries[1].Value.Should().Be("ada@example.com");
    }

    [Fact]
    public void Handles_a_byte_order_mark()
    {
        var entries = Parse("\uFEFF[user]\n\tname = Ada\n");

        entries.Should().ContainSingle();
        entries[0].Section.Should().Be("user");
    }

    [Fact]
    public void A_malformed_line_does_not_stop_the_parse()
    {
        var entries = Parse("""
            [user]
                = nonsense
                name = Ada
            """);

        entries.Should().ContainSingle();
        entries[0].Value.Should().Be("Ada");
    }

    [Fact]
    public void Follows_an_unconditional_include()
    {
        var included = Path.Combine(_root, "work.inc");
        File.WriteAllText(included, "[user]\n\temail = ada@work.example\n");

        var main = Path.Combine(_root, ".gitconfig");
        File.WriteAllText(main, $"[user]\n\tname = Ada\n[include]\n\tpath = {included.Replace("\\", "/")}\n");

        var entries = _parser.ParseFile(main, GitConfigScope.Global);

        entries.Select(e => e.Key).Should().Contain("user.email");
        entries.Single(e => e.Key == "user.email").Value.Should().Be("ada@work.example");
    }

    [Fact]
    public void Resolves_a_relative_include_against_the_including_file()
    {
        Directory.CreateDirectory(Path.Combine(_root, "conf"));
        File.WriteAllText(Path.Combine(_root, "conf", "extra.inc"), "[core]\n\tautocrlf = input\n");

        var main = Path.Combine(_root, ".gitconfig");
        File.WriteAllText(main, "[include]\n\tpath = conf/extra.inc\n");

        _parser.ParseFile(main, GitConfigScope.Global)
            .Should().Contain(e => e.Key == "core.autocrlf" && e.Value == "input");
    }

    [Fact]
    public void Conditional_include_fires_only_for_a_matching_gitdir()
    {
        var included = Path.Combine(_root, "work.inc");
        File.WriteAllText(included, "[user]\n\temail = ada@work.example\n");

        var main = Path.Combine(_root, ".gitconfig");
        var path = included.Replace("\\", "/");
        File.WriteAllText(main, $"[includeIf \"gitdir:**/work/\"]\n\tpath = {path}\n");

        var matching = GitConfigIncludeContext.ForRepository("/home/ada/work/project");
        var other = GitConfigIncludeContext.ForRepository("/home/ada/personal/project");

        _parser.ParseFile(main, GitConfigScope.Global, matching)
            .Should().Contain(e => e.Key == "user.email");
        _parser.ParseFile(main, GitConfigScope.Global, other)
            .Should().NotContain(e => e.Key == "user.email");
    }

    [Fact]
    public void An_include_cycle_terminates()
    {
        var a = Path.Combine(_root, "a.inc");
        var b = Path.Combine(_root, "b.inc");
        File.WriteAllText(a, $"[include]\n\tpath = {b.Replace("\\", "/")}\n[user]\n\tname = A\n");
        File.WriteAllText(b, $"[include]\n\tpath = {a.Replace("\\", "/")}\n[user]\n\temail = b@example.com\n");

        var entries = _parser.ParseFile(a, GitConfigScope.Global);

        entries.Should().Contain(e => e.Key == "user.name");
        entries.Should().Contain(e => e.Key == "user.email");
    }

    [Fact]
    public void A_missing_include_is_ignored()
    {
        var main = Path.Combine(_root, ".gitconfig");
        File.WriteAllText(main, "[include]\n\tpath = /definitely/not/here.inc\n[user]\n\tname = Ada\n");

        _parser.ParseFile(main, GitConfigScope.Global)
            .Should().Contain(e => e.Key == "user.name");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Leftover temp files are not worth failing a run over.
        }
    }
}

public sealed class GitConfigConditionTests
{
    [Theory]
    [InlineData("**/work/**", "/home/ada/work/p/.git", true)]
    [InlineData("**/work/**", "/home/ada/play/p/.git", false)]
    [InlineData("/home/ada/**", "/home/ada/work/p/.git", true)]
    [InlineData("/home/bob/**", "/home/ada/work/p/.git", false)]
    [InlineData("*.git", "/home/ada/p.git", false)]
    [InlineData("**/*.git", "/home/ada/p.git", true)]
    [InlineData("a?c/**", "/abc/p/.git", false)]
    public void Glob_patterns_behave(string pattern, string value, bool expected) =>
        GitConfigConditions.BuildGlobRegex(pattern, ignoreCase: false)
            .IsMatch(value).Should().Be(expected);

    [Fact]
    public void Double_star_slash_also_matches_zero_directories() =>
        GitConfigConditions.BuildGlobRegex("**/work", ignoreCase: false)
            .IsMatch("work").Should().BeTrue();

    [Fact]
    public void Case_insensitive_matching_is_opt_in()
    {
        GitConfigConditions.BuildGlobRegex("**/Work/**", ignoreCase: false)
            .IsMatch("/home/ada/work/p/.git").Should().BeFalse();
        GitConfigConditions.BuildGlobRegex("**/Work/**", ignoreCase: true)
            .IsMatch("/home/ada/work/p/.git").Should().BeTrue();
    }

    [Fact]
    public void Repository_context_derives_the_git_directory()
    {
        GitConfigIncludeContext.ForRepository("/home/ada/p")!.GitDirectory
            .Should().Be(Path.Combine("/home/ada/p", ".git"));

        GitConfigIncludeContext.ForRepository("/home/ada/p/.git")!.GitDirectory
            .Should().Be("/home/ada/p/.git");

        GitConfigIncludeContext.ForRepository(null).Should().BeNull();
    }
}
