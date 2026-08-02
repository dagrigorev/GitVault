using FluentAssertions;
using GitVault.Core.Models;
using GitVault.Core.Platform;
using GitVault.Core.Ssh;
using Xunit;

namespace GitVault.Core.Tests;

file sealed class FixedPaths(string home) : PlatformPathsBase(home)
{
    public override string AppDataDirectory => Path.Combine(HomeDirectory, ".gitvault");

    public override IReadOnlyList<string> SystemGitConfigCandidates => [];

    public override IReadOnlyList<string> AdditionalKeyDirectories => [];
}

public sealed class SshConfigParserTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "gitvault-sshcfg", Guid.NewGuid().ToString("N"));

    private readonly SshConfigParser _parser;

    public SshConfigParserTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".ssh"));
        _parser = new SshConfigParser(new FixedPaths(_root));
    }

    private string WriteConfig(string name, string content)
    {
        var path = Path.Combine(_root, ".ssh", name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Directives_carry_the_host_block_they_appeared_in()
    {
        var directives = _parser.ParseText("""
            Host github.com
                User git
                IdentityFile ~/.ssh/id_ed25519

            Host gitlab.com work-*
                IdentityFile ~/.ssh/id_work
            """, "config");

        directives.Should().HaveCount(3);
        directives[0].Keyword.Should().Be("user");
        directives[0].HostPatterns.Should().BeEquivalentTo(["github.com"]);
        directives[2].HostPatterns.Should().BeEquivalentTo(["gitlab.com", "work-*"]);
    }

    [Fact]
    public void Keyword_and_value_may_be_separated_by_an_equals_sign()
    {
        var directives = _parser.ParseText("Host=example.com\n    IdentityFile=~/.ssh/id_rsa\n", "config");

        directives.Should().ContainSingle();
        directives[0].Value.Should().Be("~/.ssh/id_rsa");
        directives[0].HostPatterns.Should().BeEquivalentTo(["example.com"]);
    }

    [Fact]
    public void Comments_and_blank_lines_are_skipped()
    {
        var directives = _parser.ParseText("# comment\n\n   # indented\nHost x\n  User git\n", "config");

        directives.Should().ContainSingle();
    }

    [Fact]
    public void Quoted_values_are_unquoted()
    {
        var directives = _parser.ParseText("Host x\n  IdentityFile \"~/.ssh/my key\"\n", "config");

        directives[0].Value.Should().Be("~/.ssh/my key");
    }

    [Theory]
    [InlineData("%d/.ssh/id_ed25519", ".ssh/id_ed25519")]
    [InlineData("~/keys/%h.key", "github.com.key")]
    [InlineData("100%%done", "100%done")]
    public void Percent_tokens_are_expanded(string input, string expectedFragment)
    {
        var expanded = _parser.ExpandTokens(input, host: "github.com", userName: "ada");

        expanded.Should().Contain(expectedFragment);
    }

    [Fact]
    public void The_user_token_expands_to_the_supplied_name() =>
        _parser.ExpandTokens("~/keys/%u", userName: "ada").Should().EndWith("ada");

    [Fact]
    public void Identity_files_are_collected_and_deduplicated()
    {
        var directives = _parser.ParseText("""
            Host a
                IdentityFile ~/.ssh/id_ed25519
            Host b
                IdentityFile ~/.ssh/id_ed25519
                IdentityFile %d/.ssh/id_rsa
            """, "config");

        var files = _parser.CollectIdentityFiles(directives, "ada");

        files.Should().HaveCount(2);
        files[0].Should().Be(Path.GetFullPath(Path.Combine(_root, ".ssh", "id_ed25519")));
        files[1].Should().Be(Path.GetFullPath(Path.Combine(_root, ".ssh", "id_rsa")));
    }

    [Fact]
    public void Include_pulls_in_another_file()
    {
        WriteConfig("work", "Host work\n  IdentityFile ~/.ssh/id_work\n");
        var main = WriteConfig("config", "Include work\nHost github.com\n  User git\n");

        var directives = _parser.ParseFile(main);

        directives.Should().Contain(d => d.Keyword == "identityfile" && d.Value == "~/.ssh/id_work");
        directives.Should().Contain(d => d.Keyword == "user");
    }

    [Fact]
    public void Include_supports_globs()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".ssh", "conf.d"));
        File.WriteAllText(Path.Combine(_root, ".ssh", "conf.d", "10-a"), "Host a\n  IdentityFile ~/.ssh/a\n");
        File.WriteAllText(Path.Combine(_root, ".ssh", "conf.d", "20-b"), "Host b\n  IdentityFile ~/.ssh/b\n");

        var main = WriteConfig("config", "Include conf.d/*\n");

        var files = _parser.CollectIdentityFiles(_parser.ParseFile(main));

        files.Should().HaveCount(2);
    }

    [Fact]
    public void An_include_cycle_terminates()
    {
        WriteConfig("a", "Include b\nHost a\n  User git\n");
        WriteConfig("b", "Include a\nHost b\n  User git\n");

        var act = () => _parser.ParseFile(Path.Combine(_root, ".ssh", "a"));

        act.Should().NotThrow();
        _parser.ParseFile(Path.Combine(_root, ".ssh", "a")).Should().HaveCountGreaterThan(1);
    }

    [Fact]
    public void A_missing_include_is_ignored()
    {
        var main = WriteConfig("config", "Include nope\nHost x\n  User git\n");

        _parser.ParseFile(main).Should().Contain(d => d.Keyword == "user");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Leftover temp files are not worth failing the run over.
        }
    }
}

public sealed class KeyHealthAnalyzerTests
{
    private static SshKey Key(
        SshKeyAlgorithm algorithm = SshKeyAlgorithm.Ed25519,
        int? bits = 256,
        bool encrypted = true,
        string? privatePath = "/home/ada/.ssh/id_ed25519",
        string? publicPath = "/home/ada/.ssh/id_ed25519.pub",
        FilePermissionInfo? permissions = null,
        bool hardware = false) =>
        new(Guid.NewGuid(), privatePath, publicPath, algorithm, bits, "SHA256:x", "MD5:x", null,
            SshKeyFormat.OpenSsh, encrypted, null, hardware)
        {
            Permissions = permissions,
        };

    [Fact]
    public void A_world_readable_private_key_is_the_highest_severity_finding()
    {
        var permissions = new FilePermissionInfo("/k", 0x1A4, "ada", IsWorldReadable: true, IsGroupReadable: true);

        var warnings = KeyHealthAnalyzer.Analyze(Key(permissions: permissions));

        warnings.Should().Contain(w => w.Code == KeyHealthAnalyzer.WorldReadableCode);
        warnings[0].Severity.Should().Be(WarningSeverity.High);
        warnings.Single(w => w.Code == KeyHealthAnalyzer.WorldReadableCode).IsAutoFixable.Should().BeTrue();
    }

    [Fact]
    public void A_correctly_locked_down_key_raises_nothing()
    {
        var permissions = new FilePermissionInfo("/k", 0x180, "ada", IsWorldReadable: false, IsGroupReadable: false);

        KeyHealthAnalyzer.Analyze(Key(permissions: permissions)).Should().BeEmpty();
    }

    [Fact]
    public void Dsa_is_flagged() =>
        KeyHealthAnalyzer.Analyze(Key(SshKeyAlgorithm.Dsa, 1024))
            .Should().Contain(w => w.Code == KeyHealthAnalyzer.DsaDeprecatedCode);

    [Theory]
    [InlineData(1024, true)]
    [InlineData(2048, true)]
    [InlineData(3072, false)]
    [InlineData(4096, false)]
    public void Short_rsa_keys_are_flagged(int bits, bool expected) =>
        KeyHealthAnalyzer.Analyze(Key(SshKeyAlgorithm.Rsa, bits))
            .Any(w => w.Code == KeyHealthAnalyzer.RsaTooShortCode).Should().Be(expected);

    [Fact]
    public void A_key_without_a_passphrase_is_flagged() =>
        KeyHealthAnalyzer.Analyze(Key(encrypted: false))
            .Should().Contain(w => w.Code == KeyHealthAnalyzer.NoPassphraseCode);

    [Fact]
    public void A_hardware_backed_key_is_not_asked_for_a_passphrase() =>
        KeyHealthAnalyzer.Analyze(Key(SshKeyAlgorithm.Ed25519Sk, encrypted: false, hardware: true))
            .Should().NotContain(w => w.Code == KeyHealthAnalyzer.NoPassphraseCode);

    [Fact]
    public void A_public_key_with_no_private_half_is_flagged() =>
        KeyHealthAnalyzer.Analyze(Key(privatePath: null))
            .Should().Contain(w => w.Code == KeyHealthAnalyzer.OrphanedPublicKeyCode);

    [Fact]
    public void A_private_key_with_no_public_file_is_flagged_and_fixable()
    {
        var warnings = KeyHealthAnalyzer.Analyze(Key(publicPath: null, encrypted: false));

        warnings.Should().Contain(w => w.Code == KeyHealthAnalyzer.MissingPublicKeyCode);
        warnings.Single(w => w.Code == KeyHealthAnalyzer.MissingPublicKeyCode)
            .IsAutoFixable.Should().BeTrue("an unencrypted key's public half can be derived without asking");
    }

    [Fact]
    public void A_failed_container_integrity_check_is_flagged() =>
        KeyHealthAnalyzer.Analyze(Key(), integrityIsValid: false)
            .Should().Contain(w => w.Code == KeyHealthAnalyzer.IntegrityCheckFailedCode);
}
