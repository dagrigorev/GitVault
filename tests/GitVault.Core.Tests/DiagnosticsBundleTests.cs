using System.IO.Compression;
using System.Text;
using FluentAssertions;
using GitVault.Core.Abstractions;
using GitVault.Core.Diagnostics;
using GitVault.Core.Models;
using GitVault.Core.Platform;
using GitVault.Core.Security;
using NSubstitute;
using Xunit;

namespace GitVault.Core.Tests;

file sealed class BundlePaths(string home) : PlatformPathsBase(home)
{
    public override string AppDataDirectory => Path.Combine(HomeDirectory, ".gitvault");

    public override IReadOnlyList<string> SystemGitConfigCandidates => [];

    public override IReadOnlyList<string> AdditionalKeyDirectories => [];
}

public sealed class DiagnosticsBundleTests : IDisposable
{
    private readonly string _home =
        Path.Combine(Path.GetTempPath(), "gitvault-diag", Guid.NewGuid().ToString("N"));

    private readonly PlatformPathsBase _paths;
    private readonly IGitConfigService _config = Substitute.For<IGitConfigService>();
    private readonly DiagnosticsBundleBuilder _builder;

    public DiagnosticsBundleTests()
    {
        _paths = new BundlePaths(_home);
        Directory.CreateDirectory(_paths.LogDirectory);

        var platformInfo = Substitute.For<IPlatformInfo>();
        platformInfo.OsDescription.Returns("Test OS 1.0");
        platformInfo.PlatformId.Returns("linux");
        platformInfo.Architecture.Returns("X64");

        _config.HasGitBinary.Returns(true);
        _config.GitVersion.Returns("2.45.0");
        _config.GitBinaryPath.Returns("/usr/bin/git");
        _config.ListAsync(null, Arg.Any<CancellationToken>()).Returns([]);

        _builder = new DiagnosticsBundleBuilder(_paths, platformInfo, _config, new SecretRedactor());
    }

    private static DiscoveryReport Report() =>
        new(DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(50))
        {
            Identities = [GitIdentity.Create("Ada", "ada@example.com", IdentitySource.GitGlobalConfig, "/x")],
            ProbeStatuses =
            [
                new ProbeStatusEntry("git.identities", "Git", ProbeStatus.Ok, null, TimeSpan.FromMilliseconds(12)),
                new ProbeStatusEntry("credentials", "Vaults", ProbeStatus.AccessDenied, "locked", TimeSpan.Zero),
            ],
            Warnings = [new KeyWarning("KeyNoPassphrase", WarningSeverity.Medium, "/home/ada/.ssh/id_rsa")],
        };

    [Fact]
    public async Task The_preview_lists_everything_the_bundle_will_contain()
    {
        var items = await _builder.PreviewAsync(Report(), CancellationToken.None);

        items.Select(i => i.Name).Should().Contain(
            ["environment.txt", "probe-status.tsv", "summary.tsv", "git-config-inventory.tsv"]);

        items.Should().OnlyContain(i => i.Description.Length > 0, "the user must be told what each file is");
    }

    [Fact]
    public async Task The_preview_writes_nothing()
    {
        var before = Directory.GetFiles(_home, "*", SearchOption.AllDirectories).Length;

        await _builder.PreviewAsync(Report(), CancellationToken.None);

        Directory.GetFiles(_home, "*", SearchOption.AllDirectories).Should().HaveCount(before);
    }

    [Fact]
    public void The_environment_file_carries_no_user_data()
    {
        var content = _builder.BuildEnvironment();

        content.Should().Contain("Test OS 1.0");
        content.Should().Contain("2.45.0");
        content.Should().NotContain(_home, "the user's home directory is not diagnostic");
    }

    [Fact]
    public void The_probe_matrix_reports_every_probe()
    {
        var content = _builder.BuildProbeMatrix(Report());

        content.Should().Contain("git.identities\tOk");
        content.Should().Contain("credentials\tAccessDenied");
        content.Should().Contain("locked");
    }

    [Fact]
    public void The_summary_holds_counts_and_no_names()
    {
        var content = DiagnosticsBundleBuilder.BuildSummary(Report());

        content.Should().Contain("identities\t1");
        content.Should().Contain("warning.KeyNoPassphrase\t1");
        content.Should().NotContain("ada@example.com", "a summary is counts, not contents");
        content.Should().NotContain("id_rsa");
    }

    [Fact]
    public async Task The_config_inventory_lists_keys_but_never_values()
    {
        _config.ListAsync(null, Arg.Any<CancellationToken>()).Returns(
        [
            new GitConfigValue("user.email", "ada@example.com", GitConfigScope.Global, "file:/home/ada/.gitconfig"),
            new GitConfigValue("http.proxy", "http://user:hunter2@proxy.example", GitConfigScope.Global, "file:/home/ada/.gitconfig"),
        ]);

        var content = await _builder.BuildConfigInventoryAsync(CancellationToken.None);

        content.Should().Contain("user.email");
        content.Should().Contain("http.proxy");
        content.Should().NotContain("ada@example.com", "values are deliberately excluded");
        content.Should().NotContain("hunter2", "a proxy password must never leave the machine");
    }

    [Fact]
    public async Task Logs_are_redacted_again_on_the_way_out()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_paths.LogDirectory, "gitvault-20260101.log"),
            "[INF] read line password=hunter2 from config\n");

        var items = await _builder.PreviewAsync(Report(), CancellationToken.None);
        var log = items.Single(i => i.Name.StartsWith("logs/", StringComparison.Ordinal));

        log.Content.Should().NotContain("hunter2");
        log.Content.Should().Contain(SecretRedactor.Placeholder);
    }

    [Fact]
    public async Task The_archive_contains_exactly_the_previewed_entries()
    {
        await File.WriteAllTextAsync(Path.Combine(_paths.LogDirectory, "gitvault-20260101.log"), "[INF] hello\n");

        var items = await _builder.PreviewAsync(Report(), CancellationToken.None);
        var destination = Path.Combine(_home, "diagnostics.zip");

        await _builder.WriteAsync(items, destination, CancellationToken.None);

        using var archive = ZipFile.OpenRead(destination);

        archive.Entries.Select(e => e.FullName).Should().BeEquivalentTo(items.Select(i => i.Name));

        var environment = archive.GetEntry("environment.txt");
        environment.Should().NotBeNull();

        using var reader = new StreamReader(environment!.Open(), Encoding.UTF8);
        (await reader.ReadToEndAsync()).Should().Be(items.Single(i => i.Name == "environment.txt").Content,
            "what the user approved is exactly what gets written");
    }

    [Fact]
    public async Task A_git_that_cannot_be_read_does_not_stop_the_bundle()
    {
        _config.ListAsync(null, Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<GitConfigValue>>(_ => throw new Git.GitConfigException("boom"));

        var content = await _builder.BuildConfigInventoryAsync(CancellationToken.None);

        content.Should().Contain("could not be read");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
            // Leftover temp files are not worth failing the run over.
        }
    }
}
