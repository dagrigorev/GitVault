using FluentAssertions;
using GitVault.Core.Platform;
using Xunit;

namespace GitVault.Core.Tests;

/// <summary>
/// Exercises the OS-independent half of <see cref="PlatformPathsBase"/> through a test double,
/// so the expansion rules are verified on every CI platform rather than only on Windows.
/// </summary>
/// <summary>
/// Resolving one directory's several names to the one the system stores it under.
/// </summary>
/// <remarks>
/// git resolves symbolic links before it records a path; a folder picker does not. Everything
/// that compares a path GitVault was given against a path git printed depends on the two being
/// brought to the same form first.
/// </remarks>
public sealed class RealPathTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "gitvault-realpath", Guid.NewGuid().ToString("N")[..12]);

    public RealPathTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void A_path_with_no_links_in_it_is_its_own_resolution()
    {
        var directory = Path.Combine(_root, "plain");
        Directory.CreateDirectory(directory);

        RealPath.Resolve(directory).Should().Be(RealPath.Resolve(Path.GetFullPath(directory)));
        RealPath.AreSame(directory, directory + Path.DirectorySeparatorChar).Should().BeTrue();
    }

    [Fact]
    public void A_link_and_its_target_are_the_same_directory()
    {
        var target = Path.Combine(_root, "target");
        var link = Path.Combine(_root, "link");
        Directory.CreateDirectory(target);

        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (IOException)
        {
            // Creating a link is a privileged operation on Windows unless developer mode is on.
            // The behaviour is still worth asserting wherever it can be.
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        RealPath.Resolve(Path.Combine(link, "inside"))
            .Should().Be(Path.Combine(RealPath.Resolve(target), "inside"));

        RealPath.AreSame(link, target).Should().BeTrue();
    }

    [Fact]
    public void A_path_that_does_not_exist_still_comes_back_absolute()
    {
        var missing = Path.Combine(_root, "nowhere", "at", "all");

        // The parts that exist are resolved and the parts that do not are kept as written. Being
        // resolvable is not a precondition for naming a path.
        RealPath.Resolve(missing)
            .Should().Be(Path.Combine(RealPath.Resolve(_root), "nowhere", "at", "all"));
    }

    [Fact]
    public void Nothing_in_gives_nothing_out() =>
        RealPath.Resolve(null).Should().BeEmpty();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temporary directory that outlives the test is not a failure.
        }
    }
}

public sealed class PlatformPathsTests
{
    private sealed class TestPaths : PlatformPathsBase
    {
        public override string AppDataDirectory => Path.Combine(HomeDirectory, ".gitvault");

        public override IReadOnlyList<string> SystemGitConfigCandidates => [];

        public override IReadOnlyList<string> AdditionalKeyDirectories => [];
    }

    private readonly TestPaths _paths = new();

    [Fact]
    public void Tilde_expands_to_the_home_directory() =>
        _paths.Expand("~").Should().Be(Path.GetFullPath(_paths.HomeDirectory));

    [Fact]
    public void Tilde_slash_expands_to_a_child_of_home() =>
        _paths.Expand("~/.ssh/id_ed25519")
            .Should().Be(Path.GetFullPath(Path.Combine(_paths.HomeDirectory, ".ssh", "id_ed25519")));

    [Fact]
    public void A_leading_tilde_that_is_part_of_a_name_is_left_alone()
    {
        var expanded = _paths.Expand("~work/keys");

        // What matters is that "~work" was not read as the home directory. The earlier form of
        // this assertion said the result must not begin with home plus "work", which on a machine
        // whose home is /Users/runner and whose checkout is under /Users/runner/work was true of
        // every relative path the test could produce — it failed for where the build happened to
        // run rather than for anything about expansion.
        expanded.Should().NotBe(Path.GetFullPath(Path.Combine(_paths.HomeDirectory, "work", "keys")));
        expanded.Should().Contain("~work");
    }

    [Fact]
    public void Separators_are_normalised_for_the_current_platform()
    {
        var expanded = _paths.Expand("~/a/b\\c");

        expanded.Should().NotContain(Path.DirectorySeparatorChar == '/' ? "\\" : "/");
    }

    [Fact]
    public void Empty_input_is_returned_unchanged()
    {
        _paths.Expand(string.Empty).Should().BeEmpty();
        _paths.Expand("   ").Should().Be("   ");
    }

    [Fact]
    public void Log_and_snapshot_directories_live_under_the_app_data_directory()
    {
        _paths.LogDirectory.Should().StartWith(_paths.AppDataDirectory);
        _paths.SnapshotDirectory.Should().StartWith(_paths.AppDataDirectory);
    }

    [Fact]
    public void Default_ssh_directory_is_dot_ssh_under_home() =>
        _paths.DefaultSshDirectory.Should().Be(Path.Combine(_paths.HomeDirectory, ".ssh"));
}
