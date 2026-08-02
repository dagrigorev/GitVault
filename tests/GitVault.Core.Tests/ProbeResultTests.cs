using FluentAssertions;
using GitVault.Core.Diagnostics;
using GitVault.Core.Models;
using Xunit;

namespace GitVault.Core.Tests;

public sealed class ProbeResultTests
{
    [Fact]
    public void Ok_carries_the_payload()
    {
        var result = ProbeResult<int>.Ok("probe.test", 42, TimeSpan.FromMilliseconds(7));

        result.IsSuccess.Should().BeTrue();
        result.Status.Should().Be(ProbeStatus.Ok);
        result.Value.Should().Be(42);
        result.Elapsed.Should().Be(TimeSpan.FromMilliseconds(7));
    }

    [Fact]
    public void Fail_carries_status_and_diagnostics()
    {
        var result = ProbeResult<int>.Fail("probe.test", ProbeStatus.AccessDenied, "keychain locked");

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ProbeStatus.AccessDenied);
        result.Diagnostics.Should().Be("keychain locked");
        result.Value.Should().Be(0);
    }

    [Fact]
    public void Map_projects_a_successful_payload()
    {
        var mapped = ProbeResult<int>.Ok("probe.test", 21).Map(v => v * 2);

        mapped.IsSuccess.Should().BeTrue();
        mapped.Value.Should().Be(42);
        mapped.ProbeId.Should().Be("probe.test");
    }

    [Fact]
    public void Map_preserves_a_failure_without_invoking_the_projection()
    {
        var invoked = false;

        var mapped = ProbeResult<int>
            .Fail("probe.test", ProbeStatus.Timeout, "took too long")
            .Map(v => { invoked = true; return v; });

        invoked.Should().BeFalse();
        mapped.Status.Should().Be(ProbeStatus.Timeout);
        mapped.Diagnostics.Should().Be("took too long");
    }
}

public sealed class ModelTests
{
    [Fact]
    public void Identity_display_name_combines_name_and_email()
    {
        var identity = GitIdentity.Create("Ada Lovelace", "ada@example.com", IdentitySource.GitGlobalConfig, "/x");

        identity.DisplayName.Should().Be("Ada Lovelace <ada@example.com>");
        identity.Occurrences.Should().ContainSingle();
    }

    [Fact]
    public void Identity_display_name_falls_back_when_a_part_is_missing()
    {
        GitIdentity.Create(string.Empty, "ada@example.com", IdentitySource.RepoLocal, "/x")
            .DisplayName.Should().Be("ada@example.com");

        GitIdentity.Create("Ada", string.Empty, IdentitySource.RepoLocal, "/x")
            .DisplayName.Should().Be("Ada");
    }

    [Fact]
    public void Identity_key_ignores_case()
    {
        var a = new IdentityKey("Ada", "ADA@example.com");
        var b = new IdentityKey("ada", "ada@EXAMPLE.com");

        a.Should().Be(b);
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Theory]
    [InlineData(0x180, "0600")]
    [InlineData(0x1A4, "0644")]
    [InlineData(0x1FF, "0777")]
    public void File_mode_renders_as_octal(int mode, string expected) =>
        new FilePermissionInfo("/k", mode, "user", false, false).ToOctal().Should().Be(expected);

    [Fact]
    public void File_mode_is_absent_on_windows() =>
        new FilePermissionInfo("C:\\k", null, "user", false, false).ToOctal().Should().BeNull();

    [Fact]
    public void Plaintext_stores_are_flagged()
    {
        Entry(VaultKind.GitCredentialsFile).IsPlaintextStore.Should().BeTrue();
        Entry(VaultKind.GcmPlaintext).IsPlaintextStore.Should().BeTrue();
        Entry(VaultKind.WindowsCredentialManager).IsPlaintextStore.Should().BeFalse();

        static CredentialEntry Entry(VaultKind kind) =>
            new(kind, "git:https://github.com", "github.com", "octocat", true, "https", null, null, false);
    }

    [Fact]
    public void Profile_markers_name_the_profile()
    {
        var profile = new IdentityProfile(
            Guid.NewGuid(),
            "work",
            GitIdentity.Create("Ada", "ada@example.com", IdentitySource.GitGlobalConfig, "/x"),
            null,
            null,
            "manager",
            ActivationScope.Global,
            null);

        profile.BeginMarker().Should().Be("# >>> GitVault managed: work >>>");
        profile.EndMarker().Should().Be("# <<< GitVault managed: work <<<");
    }

    [Fact]
    public void Activation_result_succeeds_only_when_no_step_failed()
    {
        var ok = new ActivationResult(Guid.NewGuid(), ActivationScope.Global, false, null, DateTimeOffset.UtcNow)
        {
            Steps = [new ActivationStepResult("a", StepOutcome.Applied, "user.name")],
        };
        var bad = ok with
        {
            Steps = [.. ok.Steps, new ActivationStepResult("b", StepOutcome.Failed, "user.email", "denied")],
        };

        ok.Succeeded.Should().BeTrue();
        bad.Succeeded.Should().BeFalse();
    }
}
