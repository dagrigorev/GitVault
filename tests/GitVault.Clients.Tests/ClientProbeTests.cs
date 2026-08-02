using FluentAssertions;
using GitVault.Core.Abstractions;
using GitVault.Core.Models;
using Xunit;

namespace GitVault.Clients.Tests;

/// <summary>
/// Every probe here runs against a committed fixture tree, so the suite passes on a machine with
/// none of these applications installed — which is the point.
/// </summary>
public sealed class GitKrakenProbeTests
{
    private static async Task<(DetectedClient Client, ProbePayload Payload)> ProbeAsync(
        FixtureClientEnvironment environment)
    {
        var result = await new GitKrakenProbe(environment).ProbeAsync(CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        return (result.Value!.Clients.Single(), result.Value);
    }

    [Fact]
    public async Task Reads_every_profile()
    {
        var (client, payload) = await ProbeAsync(FixtureClientEnvironment.For("gitkraken", "windows"));

        client.Kind.Should().Be(GitClientKind.GitKraken);
        client.Version.Should().Be("10.2.0");
        payload.Identities.Should().HaveCount(2);
        payload.Identities.Should().Contain(i => i.Email == "ada@example.com");
        payload.Identities.Should().Contain(i => i.Email == "grace@example.com", "the nested user shape is supported");
    }

    [Fact]
    public async Task Provider_accounts_become_credential_records()
    {
        var (_, payload) = await ProbeAsync(FixtureClientEnvironment.For("gitkraken", "windows"));

        payload.Credentials.Should().Contain(c => c.Host == "github.com" && c.UserName == "ada");
        payload.Credentials.Should().Contain(c => c.Host == "gitlab.example.com" && c.UserName == "ada.l");
    }

    [Fact]
    public async Task The_secure_box_is_reported_as_present_and_never_parsed()
    {
        var (_, payload) = await ProbeAsync(FixtureClientEnvironment.For("gitkraken", "windows"));

        var box = payload.Credentials.Single(c => c.Vault == VaultKind.GitKrakenBox && c.Host.Length == 0);
        box.SecretPresent.Should().BeTrue();
        box.IsReadOnly.Should().BeTrue("GitVault never writes into another application's store");
    }

    [Fact]
    public async Task Identities_from_a_third_party_store_are_only_probable()
    {
        var (_, payload) = await ProbeAsync(FixtureClientEnvironment.For("gitkraken", "windows"));

        payload.Identities.Should().OnlyContain(i => i.Confidence == DetectionConfidence.Probable);
    }

    [Fact]
    public async Task An_absent_client_reports_not_installed()
    {
        var result = await new GitKrakenProbe(FixtureClientEnvironment.Empty())
            .ProbeAsync(CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ProbeStatus.NotInstalled);
    }
}

public sealed class SourcetreeProbeTests
{
    [Fact]
    public async Task Reads_accounts_in_both_shapes()
    {
        var result = await new SourcetreeProbe(FixtureClientEnvironment.For("sourcetree", "windows"))
            .ProbeAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        var payload = result.Value!;
        payload.Credentials.Should().Contain(c => c.Host == "github.com" && c.UserName == "ada");
        payload.Credentials.Should().Contain(c => c.Host == "bitbucket.org" && c.UserName == "grace");
    }

    [Fact]
    public async Task Reads_the_display_name_and_email()
    {
        var result = await new SourcetreeProbe(FixtureClientEnvironment.For("sourcetree", "windows"))
            .ProbeAsync(CancellationToken.None);

        result.Value!.Identities.Should().Contain(i =>
            i.UserName == "Ada Lovelace" && i.Email == "ada@example.com");
    }

    [Fact]
    public async Task The_encrypted_password_file_is_reported_but_not_decrypted()
    {
        var result = await new SourcetreeProbe(FixtureClientEnvironment.For("sourcetree", "windows"))
            .ProbeAsync(CancellationToken.None);

        result.Value!.Credentials.Should().Contain(c =>
            c.SourcePath != null && c.SourcePath.EndsWith("passwd", StringComparison.Ordinal) && c.IsReadOnly);
    }

    [Fact]
    public async Task An_absent_client_reports_not_installed()
    {
        var result = await new SourcetreeProbe(FixtureClientEnvironment.Empty())
            .ProbeAsync(CancellationToken.None);

        result.Status.Should().Be(ProbeStatus.NotInstalled);
    }
}

public sealed class GitHubDesktopProbeTests
{
    [Fact]
    public async Task Reads_the_account_logins()
    {
        var result = await new GitHubDesktopProbe(FixtureClientEnvironment.For("githubdesktop", "windows"))
            .ProbeAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Credentials.Should().Contain(c => c.UserName == "ada" && c.Host == "github.com");
    }

    [Fact]
    public async Task An_enterprise_endpoint_is_shown_as_the_forge_the_user_recognises()
    {
        var result = await new GitHubDesktopProbe(FixtureClientEnvironment.For("githubdesktop", "windows"))
            .ProbeAsync(CancellationToken.None);

        result.Value!.Credentials.Should().Contain(c =>
            c.UserName == "ada-work" && c.Host == "github.example.com");
    }

    [Fact]
    public async Task The_token_is_reported_as_present_without_being_read()
    {
        var result = await new GitHubDesktopProbe(FixtureClientEnvironment.For("githubdesktop", "windows"))
            .ProbeAsync(CancellationToken.None);

        result.Value!.Credentials.Should().OnlyContain(c => c.SecretPresent && c.IsReadOnly);
    }
}

public sealed class GhCliProbeTests
{
    [Fact]
    public async Task Reads_each_host_block()
    {
        var result = await new GhCliProbe(FixtureClientEnvironment.For("ghcli", "linux"))
            .ProbeAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Credentials.Should().HaveCount(2);
        result.Value.Credentials.Should().Contain(c => c.Host == "github.com" && c.UserName == "ada");
        result.Value.Credentials.Should().Contain(c => c.Host == "github.example.com" && c.UserName == "ada-work");
    }

    [Fact]
    public async Task A_token_written_into_the_file_is_flagged_as_plaintext()
    {
        var result = await new GhCliProbe(FixtureClientEnvironment.For("ghcli", "linux"))
            .ProbeAsync(CancellationToken.None);

        result.Value!.Credentials.Single(c => c.Host == "github.com")
            .IsPlaintextStore.Should().BeTrue("the token sits in hosts.yml in the clear");

        result.Value.Credentials.Single(c => c.Host == "github.example.com")
            .IsPlaintextStore.Should().BeFalse("that host has no token in the file");
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("# only a comment\n", 0)]
    [InlineData("github.com:\n    user: ada\n", 1)]
    public void Host_parsing_tolerates_sparse_files(string text, int expected) =>
        GhCliProbe.ParseHosts(text).Should().HaveCount(expected);
}

public sealed class ManifestClientProbeTests
{
    [Fact]
    public void Every_embedded_manifest_parses()
    {
        var manifests = ManifestClientProbe.LoadEmbeddedManifests();

        manifests.Should().NotBeEmpty();
        manifests.Should().OnlyContain(m => m.Id.Length > 0 && m.DisplayName.Length > 0);
        manifests.Select(m => m.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task A_manifest_described_client_is_detected_and_read()
    {
        var manifest = ManifestClientProbe.LoadEmbeddedManifests().Single(m => m.Id == "fork");
        var probe = new ManifestClientProbe(FixtureClientEnvironment.For("fork", "windows"), manifest);

        var result = await probe.ProbeAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Clients.Single().DisplayName.Should().Be("Fork");
        result.Value.Identities.Should().ContainSingle(i =>
            i.UserName == "Ada Lovelace" && i.Email == "ada@example.com");
    }

    [Fact]
    public async Task A_manifest_for_another_platform_is_not_applicable()
    {
        var manifest = ManifestClientProbe.LoadEmbeddedManifests().Single(m => m.Id == "git-extensions");
        var environment = FixtureClientEnvironment.For("ghcli", "linux");

        var probe = new ManifestClientProbe(environment, manifest);

        probe.IsSupportedOnThisPlatform.Should().BeFalse();
        (await probe.ProbeAsync(CancellationToken.None)).Status.Should().Be(ProbeStatus.NotApplicable);
    }

    [Fact]
    public void Path_tokens_expand_to_the_environment_directories()
    {
        var environment = FixtureClientEnvironment.For("fork", "windows");

        ManifestClientProbe.ExpandTokens("{appdata}/Fork", environment)
            .Should().Be(Path.Combine(environment.AppData, "Fork"));
    }

    [Fact]
    public void A_dotted_property_path_reads_a_nested_value()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""{"user":{"email":"ada@example.com"}}""");

        ManifestClientProbe.ReadNestedProperty(document.RootElement, "user.email")
            .Should().Be("ada@example.com");
        ManifestClientProbe.ReadNestedProperty(document.RootElement, "user.missing").Should().BeNull();
        ManifestClientProbe.ReadNestedProperty(document.RootElement, "nope.email").Should().BeNull();
    }
}

public sealed class TortoiseGitProbeTests
{
    [Fact]
    public void Remote_key_bindings_are_extracted_from_the_flat_registry_map()
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [@"Remote\origin\puttykeyfile"] = @"C:\keys\work.ppk",
            [@"Remote\upstream\puttykeyfile"] = @"C:\keys\oss.ppk",
            [@"Remote\origin\pushurl"] = "https://github.com/ada/p.git",
            ["SSH"] = @"C:\Program Files\TortoiseGit\bin\TortoiseGitPlink.exe",
        };

        var bindings = TortoiseGitProbe.ExtractRemoteKeyBindings(settings);

        bindings.Should().HaveCount(2);
        bindings["origin"].Should().Be(@"C:\keys\work.ppk");
        bindings["upstream"].Should().Be(@"C:\keys\oss.ppk");
    }

    [Fact]
    public void An_empty_binding_value_is_ignored()
    {
        var bindings = TortoiseGitProbe.ExtractRemoteKeyBindings(
            new Dictionary<string, string> { [@"Remote\origin\puttykeyfile"] = string.Empty });

        bindings.Should().BeEmpty();
    }

    [Fact]
    public void The_probe_only_applies_to_windows() =>
        new TortoiseGitProbe(FixtureClientEnvironment.Empty())
            .IsSupportedOnThisPlatform.Should().Be(OperatingSystem.IsWindows());
}

public sealed class WslProbeTests
{
    [Theory]
    [InlineData("[user]\n\tname = Ada\n\temail = ada@example.com\n", "Ada", "ada@example.com")]
    [InlineData("[core]\n\tname = notthis\n[user]\n\temail = ada@example.com\n", null, "ada@example.com")]
    [InlineData("# comment\n[user]\n\tname = \"Ada L\"\n", "Ada L", null)]
    [InlineData("", null, null)]
    public void Identity_is_read_from_a_distribution_gitconfig(string text, string? name, string? email)
    {
        var (readName, readEmail) = WslProbe.ReadIdentityFromConfig(text);

        readName.Should().Be(name);
        readEmail.Should().Be(email);
    }

    [Fact]
    public void The_probe_only_applies_to_windows() =>
        new WslProbe(FixtureClientEnvironment.Empty())
            .IsSupportedOnThisPlatform.Should().Be(OperatingSystem.IsWindows());
}

public sealed class ClientProbeContractTests
{
    private static IReadOnlyList<IClientProbe> AllProbes()
    {
        var environment = FixtureClientEnvironment.Empty();

        return
        [
            new GitKrakenProbe(environment),
            new SourcetreeProbe(environment),
            new GitHubDesktopProbe(environment),
            new GhCliProbe(environment),
            new GlabCliProbe(environment),
            new TortoiseGitProbe(environment),
            new WslProbe(environment),
        ];
    }

    [Fact]
    public void Probe_identifiers_are_unique() =>
        AllProbes().Select(p => p.ProbeId).Should().OnlyHaveUniqueItems();

    [Fact]
    public void Every_probe_names_itself() =>
        AllProbes().Should().OnlyContain(p => p.DisplayName.Length > 0 && p.ProbeId.Length > 0);

    [Fact]
    public async Task No_probe_throws_when_nothing_is_installed()
    {
        foreach (var probe in AllProbes())
        {
            var act = async () => await probe.ProbeAsync(CancellationToken.None);
            await act.Should().NotThrowAsync($"{probe.ProbeId} must report absence, not fail");
        }
    }

    [Fact]
    public async Task An_absent_client_never_produces_a_payload()
    {
        foreach (var probe in AllProbes().Where(p => p.IsSupportedOnThisPlatform))
        {
            var result = await probe.ProbeAsync(CancellationToken.None);

            result.Status.Should().BeOneOf(ProbeStatus.NotInstalled, ProbeStatus.NotApplicable);
        }
    }
}
