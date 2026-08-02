using FluentAssertions;
using GitVault.Core.Abstractions;
using GitVault.Core.Models;
using GitVault.Core.Platform;
using GitVault.Core.Ssh;
using Xunit;
using Xunit.Abstractions;

namespace GitVault.Core.Tests;

file sealed class ScanPaths(string home) : PlatformPathsBase(home)
{
    public override string AppDataDirectory => Path.Combine(HomeDirectory, ".gitvault");

    public override IReadOnlyList<string> SystemGitConfigCandidates => [];

    public override IReadOnlyList<string> AdditionalKeyDirectories => [];
}

/// <summary>A permission service that reports whatever the test wants, on any platform.</summary>
file sealed class StubPermissions(bool worldReadable) : IFilePermissionService
{
    public bool CanRestrictPermissions => true;

    public FilePermissionInfo? Read(string path) =>
        new(path, worldReadable ? 0x1A4 : 0x180, "tester", worldReadable, worldReadable);

    public Task<bool> HardenAsync(string path, CancellationToken cancellationToken) => Task.FromResult(true);
}

public sealed class SshKeyScannerTests : IDisposable
{
    private readonly string _home =
        Path.Combine(Path.GetTempPath(), "gitvault-scan", Guid.NewGuid().ToString("N"));

    private readonly string _sshDirectory;

    public SshKeyScannerTests()
    {
        _sshDirectory = Path.Combine(_home, ".ssh");
        Directory.CreateDirectory(_sshDirectory);
    }

    private void CopyFixture(string name, string? targetName = null) =>
        File.Copy(SshFixtures.Path(name), Path.Combine(_sshDirectory, targetName ?? name), overwrite: true);

    private Task<IReadOnlyList<SshKey>> ScanAsync(bool worldReadable = false) =>
        new SshKeyScanner(new ScanPaths(_home), new StubPermissions(worldReadable))
            .ScanAsync([], CancellationToken.None);

    [Fact]
    public async Task Pairs_a_private_key_with_its_public_file()
    {
        CopyFixture("ed25519_plain");
        CopyFixture("ed25519_plain.pub");

        var keys = await ScanAsync();

        keys.Should().ContainSingle();
        keys[0].PrivatePath.Should().EndWith("ed25519_plain");
        keys[0].PublicPath.Should().EndWith("ed25519_plain.pub");
        keys[0].FingerprintSha256.Should().Be(SshFixtures.Expected["ed25519_plain"].Sha256);
        keys[0].Comment.Should().Be("ada@example.com");
    }

    [Fact]
    public async Task A_public_key_with_no_private_half_is_reported_as_an_orphan()
    {
        CopyFixture("orphan.pub");

        var keys = await ScanAsync();

        keys.Should().ContainSingle();
        keys[0].PrivatePath.Should().BeNull();
        keys[0].Format.Should().Be(SshKeyFormat.PublicOnly);
        keys[0].Warnings.Should().Contain(w => w.Code == KeyHealthAnalyzer.OrphanedPublicKeyCode);
    }

    [Fact]
    public async Task A_private_key_with_no_public_file_still_fingerprints()
    {
        CopyFixture("rsa2048_plain");

        var keys = await ScanAsync();

        keys.Should().ContainSingle();
        keys[0].PublicPath.Should().BeNull();
        keys[0].FingerprintSha256.Should().Be(SshFixtures.Expected["rsa2048_plain"].Sha256);
        keys[0].Warnings.Should().Contain(w => w.Code == KeyHealthAnalyzer.MissingPublicKeyCode);
    }

    [Fact]
    public async Task Loose_permissions_produce_a_high_severity_finding()
    {
        CopyFixture("ed25519_plain");

        var keys = await ScanAsync(worldReadable: true);

        keys[0].Warnings.Should().Contain(w =>
            w.Code == KeyHealthAnalyzer.WorldReadableCode && w.Severity == WarningSeverity.High);
    }

    [Fact]
    public async Task Known_ssh_directory_files_are_not_mistaken_for_keys()
    {
        CopyFixture("ed25519_plain");
        await File.WriteAllTextAsync(Path.Combine(_sshDirectory, "known_hosts"), "github.com ssh-ed25519 AAAA\n");
        await File.WriteAllTextAsync(Path.Combine(_sshDirectory, "config"), "Host x\n  User git\n");

        var keys = await ScanAsync();

        keys.Should().ContainSingle();
    }

    [Fact]
    public async Task Identity_files_referenced_from_the_config_are_scanned()
    {
        var elsewhere = Path.Combine(_home, "keys");
        Directory.CreateDirectory(elsewhere);
        File.Copy(SshFixtures.Path("ecdsa256_plain"), Path.Combine(elsewhere, "ecdsa256_plain"));

        await File.WriteAllTextAsync(
            Path.Combine(_sshDirectory, "config"),
            "Host example.com\n    IdentityFile ~/keys/ecdsa256_plain\n");

        var keys = await ScanAsync();

        keys.Should().ContainSingle();
        keys[0].FingerprintSha256.Should().Be(SshFixtures.Expected["ecdsa256_plain"].Sha256);
    }

    [Fact]
    public async Task Every_committed_fixture_is_classified()
    {
        foreach (var file in Directory.EnumerateFiles(SshFixtures.Root)
                     .Where(f => !f.EndsWith(".tsv", StringComparison.Ordinal)
                                 && !f.EndsWith(".md", StringComparison.Ordinal)
                                 && !Path.GetFileName(f).StartsWith("malformed", StringComparison.Ordinal)))
        {
            File.Copy(file, Path.Combine(_sshDirectory, Path.GetFileName(file)), overwrite: true);
        }

        var keys = await ScanAsync();

        keys.Should().NotBeEmpty();
        keys.Should().OnlyContain(k => k.FingerprintSha256.StartsWith("SHA256:", StringComparison.Ordinal));
        keys.Select(k => k.Algorithm).Distinct().Should().Contain(
            [SshKeyAlgorithm.Ed25519, SshKeyAlgorithm.Rsa, SshKeyAlgorithm.Ecdsa, SshKeyAlgorithm.Dsa]);
    }

    [Fact]
    public async Task Malformed_files_are_skipped_rather_than_failing_the_scan()
    {
        CopyFixture("malformed_truncated.key");
        CopyFixture("malformed_not_a_key.key");
        CopyFixture("ed25519_plain");

        var keys = await ScanAsync();

        keys.Should().ContainSingle();
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

/// <summary>
/// Exercises the real <c>ssh-keygen</c>. Skipped when it is not installed, so the suite still
/// runs on a bare container.
/// </summary>
public sealed class SshKeyGeneratorIntegrationTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "gitvault-keygen", Guid.NewGuid().ToString("N"));

    private sealed class Hints : ISshToolHints
    {
        public IReadOnlyList<string> SshKeygenCandidates => [];

        public IReadOnlyList<string> SshAddCandidates => [];
    }

    private sealed class NoOpPermissions : IFilePermissionService
    {
        public bool CanRestrictPermissions => true;

        public FilePermissionInfo? Read(string path) => null;

        public Task<bool> HardenAsync(string path, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private async Task<SshKeyGenerator?> CreateAsync()
    {
        Directory.CreateDirectory(_directory);

        var runner = new ProcessRunner();
        var generator = new SshKeyGenerator(runner, new NoOpPermissions(), new SshToolLocator(runner, new Hints()));
        await generator.InitializeAsync(CancellationToken.None);

        if (!generator.HasSshKeygen)
        {
            output.WriteLine("ssh-keygen is not installed; skipping.");
            return null;
        }

        return generator;
    }

    [Fact]
    public async Task Generates_an_ed25519_key_that_we_then_parse_identically()
    {
        var generator = await CreateAsync();
        if (generator is null)
        {
            return;
        }

        var path = Path.Combine(_directory, "id_ed25519");
        var result = await generator.GenerateAsync(
            path,
            new SshKeyGenerationRequest(SshKeyAlgorithm.Ed25519, null, "generated@example.com"),
            ReadOnlyMemory<byte>.Empty,
            CancellationToken.None);

        result.Succeeded.Should().BeTrue(result.Diagnostics);
        File.Exists(path).Should().BeTrue();

        SshKeyReader.TryReadPrivateKeyFile(path, out var info).Should().BeTrue();
        info!.IsEncrypted.Should().BeFalse();
        info.Comment.Should().Be("generated@example.com");
        info.PublicKey!.FingerprintSha256.Should().Be(result.Fingerprint);
    }

    [Fact]
    public async Task Refuses_to_overwrite_an_existing_key()
    {
        var generator = await CreateAsync();
        if (generator is null)
        {
            return;
        }

        var path = Path.Combine(_directory, "existing");
        await File.WriteAllTextAsync(path, "not a key");

        var result = await generator.GenerateAsync(
            path,
            new SshKeyGenerationRequest(SshKeyAlgorithm.Ed25519, null, "x"),
            ReadOnlyMemory<byte>.Empty,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        (await File.ReadAllTextAsync(path)).Should().Be("not a key");
    }

    [Fact]
    public async Task Generates_a_passphrase_protected_key_with_the_requested_work_factor()
    {
        var generator = await CreateAsync();
        if (generator is null)
        {
            return;
        }

        var path = Path.Combine(_directory, "id_locked");
        var passphrase = System.Text.Encoding.UTF8.GetBytes("correct horse battery staple");

        var result = await generator.GenerateAsync(
            path,
            new SshKeyGenerationRequest(SshKeyAlgorithm.Ed25519, null, "locked@example.com", KdfRounds: 20),
            passphrase,
            CancellationToken.None);

        result.Succeeded.Should().BeTrue(result.Diagnostics);

        SshKeyReader.TryReadPrivateKeyFile(path, out var info).Should().BeTrue();
        info!.IsEncrypted.Should().BeTrue();
        info.KdfRounds.Should().Be(20);
    }

    [Fact]
    public async Task Derives_a_missing_public_key_from_an_unencrypted_private_key()
    {
        var generator = await CreateAsync();
        if (generator is null)
        {
            return;
        }

        var path = Path.Combine(_directory, "id_rsa");
        await generator.GenerateAsync(
            path,
            new SshKeyGenerationRequest(SshKeyAlgorithm.Rsa, 3072, "derive@example.com"),
            ReadOnlyMemory<byte>.Empty,
            CancellationToken.None);

        File.Delete(path + ".pub");

        var result = await generator.DerivePublicKeyAsync(path, ReadOnlyMemory<byte>.Empty, CancellationToken.None);

        result.Succeeded.Should().BeTrue(result.Diagnostics);
        File.Exists(path + ".pub").Should().BeTrue();

        SshKeyReader.TryReadPublicKeyFile(path + ".pub", out var recovered).Should().BeTrue();
        recovered!.FingerprintSha256.Should().Be(result.Fingerprint);
    }

    [Fact]
    public async Task Adds_and_then_removes_a_passphrase()
    {
        var generator = await CreateAsync();
        if (generator is null)
        {
            return;
        }

        var path = Path.Combine(_directory, "id_change");
        await generator.GenerateAsync(
            path,
            new SshKeyGenerationRequest(SshKeyAlgorithm.Ed25519, null, "change@example.com"),
            ReadOnlyMemory<byte>.Empty,
            CancellationToken.None);

        var passphrase = System.Text.Encoding.UTF8.GetBytes("s3cret");

        (await generator.ChangePassphraseAsync(path, ReadOnlyMemory<byte>.Empty, passphrase, CancellationToken.None))
            .Succeeded.Should().BeTrue();
        SshKeyReader.TryReadPrivateKeyFile(path, out var locked).Should().BeTrue();
        locked!.IsEncrypted.Should().BeTrue();

        (await generator.ChangePassphraseAsync(path, passphrase, ReadOnlyMemory<byte>.Empty, CancellationToken.None))
            .Succeeded.Should().BeTrue();
        SshKeyReader.TryReadPrivateKeyFile(path, out var unlocked).Should().BeTrue();
        unlocked!.IsEncrypted.Should().BeFalse();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Leftover temp files are not worth failing the run over.
        }
    }
}
