using FluentAssertions;
using GitVault.Core.Abstractions;
using GitVault.Core.Git;
using GitVault.Core.Platform;
using Xunit;
using Xunit.Abstractions;

namespace GitVault.Core.Tests;

/// <summary>
/// Exercises the real <c>git</c> binary. These run on every CI platform, which is the point:
/// the <c>--show-scope --show-origin -z</c> record layout is an assumption about another
/// program's output, and it deserves to be checked against the real thing.
/// </summary>
public sealed class GitBinaryIntegrationTests(ITestOutputHelper output)
{
    private sealed class RealPaths : PlatformPathsBase
    {
        public override string AppDataDirectory => Path.Combine(HomeDirectory, ".gitvault-tests");

        public override IReadOnlyList<string> SystemGitConfigCandidates => [];

        public override IReadOnlyList<string> AdditionalKeyDirectories => [];
    }

    private sealed class NameOnlyHints : IGitInstallHints
    {
        public string GitExecutableName => OperatingSystem.IsWindows() ? "git.exe" : "git";

        public IReadOnlyList<string> CandidateGitPaths => [];
    }

    [Fact]
    public async Task Locates_git_on_the_path_and_reads_its_version()
    {
        var locator = new GitBinaryLocator(new ProcessRunner(), new NameOnlyHints());

        var binary = await locator.LocateAsync(CancellationToken.None);

        if (binary is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        binary.Version.Should().MatchRegex(@"^\d+\.\d+");
    }

    [Fact]
    public async Task Reads_a_real_repository_configuration_through_git()
    {
        var runner = new ProcessRunner();
        var locator = new GitBinaryLocator(runner, new NameOnlyHints());
        if (await locator.LocateAsync(CancellationToken.None) is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var repository = Path.Combine(Path.GetTempPath(), "gitvault-repo", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repository);

        try
        {
            (await runner.RunAsync(OperatingSystem.IsWindows() ? "git.exe" : "git",
                ["init", "--quiet"], repository, TimeSpan.FromSeconds(20), CancellationToken.None))
                .IsSuccess.Should().BeTrue();

            var service = new GitConfigService(runner, locator, new RealPaths());

            await service.SetAsync("user.email", "ada@example.com", Models.GitConfigScope.Local,
                repository, CancellationToken.None);
            await service.SetAsync("user.name", "Ada Lovelace", Models.GitConfigScope.Local,
                repository, CancellationToken.None);

            var values = await service.ListAsync(repository, CancellationToken.None);

            values.Should().Contain(v =>
                v.Key == "user.email"
                && v.Value == "ada@example.com"
                && v.Scope == Models.GitConfigScope.Local);

            var effective = await new EffectiveIdentityResolver(service)
                .ResolveAsync(repository, CancellationToken.None);

            effective.IsComplete.Should().BeTrue();
            effective.Email.Value.Should().Be("ada@example.com");
            effective.Email.Scope.Should().Be(Models.GitConfigScope.Local);

            await service.UnsetAsync("user.email", Models.GitConfigScope.Local, repository, CancellationToken.None);

            var after = await service.ListAsync(repository, CancellationToken.None);
            after.Should().NotContain(v => v.Key == "user.email" && v.Scope == Models.GitConfigScope.Local);
        }
        finally
        {
            TryDelete(repository);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            // git marks objects read-only on Windows, so clear the attribute before deleting.
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp repository is not worth failing the run over.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }
}
