using System.Text;
using FluentAssertions;
using GitVault.Core.Git;
using Xunit;

namespace GitVault.Core.Tests;

public sealed class GitConfigWriterTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "gitvault-write", Guid.NewGuid().ToString("N"));

    private readonly GitConfigWriter _writer = new();

    public GitConfigWriterTests() => Directory.CreateDirectory(_root);

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    [Fact]
    public void Replaces_a_value_and_leaves_everything_else_byte_identical()
    {
        var path = WriteFile(".gitconfig", """
            # my carefully curated config
            [user]
            	name = Ada
            	email = ada@old.example
            [core]
            	# keep this comment exactly here
            	autocrlf = input

            """);

        _writer.Set(path, "user", null, "email", "ada@new.example");

        File.ReadAllText(path).Should().Be("""
            # my carefully curated config
            [user]
            	name = Ada
            	email = ada@new.example
            [core]
            	# keep this comment exactly here
            	autocrlf = input

            """);
    }

    [Fact]
    public void Adds_a_variable_to_an_existing_section()
    {
        var path = WriteFile(".gitconfig", "[user]\n\tname = Ada\n[core]\n\tautocrlf = input\n");

        _writer.Set(path, "user", null, "email", "ada@example.com");

        File.ReadAllText(path).Should().Be("[user]\n\tname = Ada\n\temail = ada@example.com\n[core]\n\tautocrlf = input\n");
    }

    [Fact]
    public void Creates_a_missing_section_at_the_end()
    {
        var path = WriteFile(".gitconfig", "[user]\n\tname = Ada\n");

        _writer.Set(path, "credential", null, "helper", "manager");

        File.ReadAllText(path).Should().Be("[user]\n\tname = Ada\n\n[credential]\n\thelper = manager\n");
    }

    [Fact]
    public void Creates_the_file_when_it_does_not_exist()
    {
        var path = Path.Combine(_root, "fresh", ".gitconfig");

        _writer.Set(path, "user", null, "name", "Ada");

        File.ReadAllText(path).Should().Be("[user]\n\tname = Ada\n");
    }

    [Fact]
    public void Preserves_crlf_line_endings()
    {
        var path = WriteFile(".gitconfig", "[user]\r\n\tname = Ada\r\n");

        _writer.Set(path, "user", null, "name", "Grace");

        File.ReadAllText(path).Should().Be("[user]\r\n\tname = Grace\r\n");
    }

    [Fact]
    public void Preserves_a_byte_order_mark()
    {
        var path = Path.Combine(_root, "bom.gitconfig");
        File.WriteAllText(path, "[user]\n\tname = Ada\n", new UTF8Encoding(true));

        _writer.Set(path, "user", null, "name", "Grace");

        var bytes = File.ReadAllBytes(path);
        bytes[0].Should().Be(0xEF);
        bytes[1].Should().Be(0xBB);
        bytes[2].Should().Be(0xBF);
    }

    [Fact]
    public void Targets_the_right_subsection()
    {
        var path = WriteFile(".gitconfig", """
            [credential "https://github.com"]
            	username = octocat
            [credential "https://gitlab.com"]
            	username = tanuki

            """);

        _writer.Set(path, "credential", "https://gitlab.com", "username", "changed");

        var text = File.ReadAllText(path);
        text.Should().Contain("username = octocat");
        text.Should().Contain("username = changed");
        text.Should().NotContain("tanuki");
    }

    [Fact]
    public void Unset_removes_only_the_matching_lines()
    {
        var path = WriteFile(".gitconfig", "[user]\n\tname = Ada\n\temail = ada@example.com\n");

        _writer.Unset(path, "user", null, "email").Should().BeTrue();

        File.ReadAllText(path).Should().Be("[user]\n\tname = Ada\n");
    }

    [Fact]
    public void Unset_removes_every_value_of_a_multi_valued_key()
    {
        var path = WriteFile(".gitconfig", "[credential]\n\thelper = a\n\thelper = b\n\tusername = x\n");

        _writer.Unset(path, "credential", null, "helper").Should().BeTrue();

        File.ReadAllText(path).Should().Be("[credential]\n\tusername = x\n");
    }

    [Fact]
    public void Unset_on_a_missing_key_reports_no_change()
    {
        var path = WriteFile(".gitconfig", "[user]\n\tname = Ada\n");

        _writer.Unset(path, "user", null, "email").Should().BeFalse();
        File.ReadAllText(path).Should().Be("[user]\n\tname = Ada\n");
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("with space", "with space")]
    [InlineData(" leading", "\" leading\"")]
    [InlineData("trailing ", "\"trailing \"")]
    [InlineData("has # hash", "\"has # hash\"")]
    [InlineData("has \"quote\"", "\"has \\\"quote\\\"\"")]
    [InlineData("C:\\keys", "\"C:\\\\keys\"")]
    [InlineData("", "\"\"")]
    public void Encodes_values_only_when_the_grammar_requires_it(string input, string expected) =>
        GitConfigWriter.Encode(input).Should().Be(expected);

    [Fact]
    public void A_written_value_round_trips_through_the_parser()
    {
        var path = WriteFile(".gitconfig", "[user]\n\tname = Ada\n");
        const string Awkward = "  Ada \"The Countess\" # 1815  ";

        _writer.Set(path, "user", null, "name", Awkward);

        var parser = new GitConfigParser(new StubPaths(_root));
        parser.ParseFile(path, Core.Models.GitConfigScope.Global)
            .Single(e => e.Key == "user.name").Value.Should().Be(Awkward);
    }

    private sealed class StubPaths(string home) : Core.Platform.PlatformPathsBase
    {
        public override string AppDataDirectory => Path.Combine(home, ".gitvault");

        public override IReadOnlyList<string> SystemGitConfigCandidates => [];

        public override IReadOnlyList<string> AdditionalKeyDirectories => [];
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
