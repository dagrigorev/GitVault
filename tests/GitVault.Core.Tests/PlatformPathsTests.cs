using FluentAssertions;
using GitVault.Core.Platform;
using Xunit;

namespace GitVault.Core.Tests;

/// <summary>
/// Exercises the OS-independent half of <see cref="PlatformPathsBase"/> through a test double,
/// so the expansion rules are verified on every CI platform rather than only on Windows.
/// </summary>
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

        expanded.Should().NotStartWith(_paths.HomeDirectory + Path.DirectorySeparatorChar + "work");
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
