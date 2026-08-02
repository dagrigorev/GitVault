using FluentAssertions;
using GitVault.Core.Security;
using Xunit;

namespace GitVault.Core.Tests;

public sealed class SecretRedactorTests
{
    private readonly SecretRedactor _redactor = new();

    [Theory]
    [InlineData("password=hunter2")]
    [InlineData("Password: hunter2")]
    [InlineData("passphrase = \"correct horse\"")]
    [InlineData("token=ghp_abcdefghijklmnopqrstuvwxyz012345")]
    [InlineData("api_key: 8f14e45fceea167a5a36dedd4bea2543")]
    [InlineData("secret='s3cr3t'")]
    public void Redacts_key_value_secrets(string input)
    {
        var result = _redactor.Redact(input);

        result.Should().Contain(SecretRedactor.Placeholder);
        result.Should().NotContain("hunter2");
        result.Should().NotContain("s3cr3t");
        result.Should().NotContain("correct horse");
        result.Should().NotContain("ghp_abcdefghijklmnopqrstuvwxyz012345");
        result.Should().NotContain("8f14e45fceea167a5a36dedd4bea2543");
    }

    [Fact]
    public void Redacts_openssh_private_key_block()
    {
        const string Input = """
            reading key file:
            -----BEGIN OPENSSH PRIVATE KEY-----
            b3BlbnNzaC1rZXktdjEAAAAABG5vbmUAAAAEbm9uZQAAAAAAAAABAAAAMwAAAAtzc2gt
            ZWQyNTUxOQAAACAX9dQjZ0yTQ7Xy0T1oPzGqYcFf5o2Wk0hzZKQ0P1s5xQ==
            -----END OPENSSH PRIVATE KEY-----
            done
            """;

        var result = _redactor.Redact(Input);

        result.Should().NotContain("b3BlbnNzaC1rZXktdjEA");
        result.Should().Contain("reading key file:");
        result.Should().Contain("done");
    }

    [Fact]
    public void Redacts_putty_private_lines()
    {
        const string Input = """
            PuTTY-User-Key-File-3: ssh-ed25519
            Encryption: none
            Comment: work key
            Private-Lines: 1
            AAAAIHqQ1s5xQ0P1sZKQ0hzWk2o5fFcYqzPo1T0yX7QdX9AX
            Private-MAC: 0123456789abcdef
            """;

        var result = _redactor.Redact(Input);

        result.Should().NotContain("AAAAIHqQ1s5xQ0P1sZKQ0hzWk2o5fFcYqzPo1T0yX7QdX9AX");
        result.Should().NotContain("0123456789abcdef");
        result.Should().Contain("Comment: work key");
    }

    [Fact]
    public void Redacts_password_inside_a_git_credentials_url()
    {
        var result = _redactor.Redact("https://octocat:ghs_verySecretValue123456@github.com");

        result.Should().NotContain("ghs_verySecretValue123456");
        result.Should().Contain("octocat");
        result.Should().Contain("github.com");
    }

    [Fact]
    public void Keeps_ordinary_text_untouched()
    {
        const string Input = "Scanned /home/user/.ssh, found 3 keys, SHA256:abc123DEF456 loaded in agent.";

        _redactor.Redact(Input).Should().Be(Input);
        _redactor.ContainsSecret(Input).Should().BeFalse();
    }

    [Fact]
    public void Keeps_ssh_fingerprints_readable()
    {
        // A canonical OpenSSH fingerprint is 43 base64 characters, deliberately below the
        // long-base64 threshold, because operators need to read it in the logs.
        const string Fingerprint = "SHA256:47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU";

        _redactor.Redact("key " + Fingerprint).Should().Contain(Fingerprint);
    }

    [Fact]
    public void Redacts_long_base64_blobs()
    {
        var blob = new string('A', 80);

        _redactor.Redact("blob " + blob).Should().NotContain(blob);
    }

    [Fact]
    public void Null_and_empty_input_produce_empty_output()
    {
        _redactor.Redact(null).Should().BeEmpty();
        _redactor.Redact(string.Empty).Should().BeEmpty();
        _redactor.ContainsSecret(null).Should().BeFalse();
    }
}
