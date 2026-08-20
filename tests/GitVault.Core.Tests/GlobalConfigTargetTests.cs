using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace GitVault.Core.Tests;

/// <summary>
/// Where <c>git config --global</c> actually writes.
/// </summary>
/// <remarks>
/// This matters more than it looks. GitVault snapshots the file it believes a plan will change,
/// and restores that file on deactivation or rollback. If the path it snapshots is not the path
/// git writes to, the change is real and the undo is a no-op — the one failure the whole snapshot
/// design exists to prevent.
///
/// Git's documented rule for the per-user file is: <c>$GIT_CONFIG_GLOBAL</c> if set; otherwise
/// <c>$XDG_CONFIG_HOME/git/config</c> when <c>~/.gitconfig</c> does not exist and the XDG file
/// does; otherwise <c>~/.gitconfig</c>. These tests pin that behaviour against the real binary so
/// the rule GitVault implements is checked rather than assumed.
/// </remarks>
public sealed class GlobalConfigTargetTests(ITestOutputHelper output)
{
    [Fact]
    public void Git_writes_the_xdg_file_when_it_exists_and_the_home_file_does_not()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        // Start from a home with neither file, then create only the XDG one.
        File.Delete(environment.GlobalConfigPath);

        var xdgDirectory = Path.Combine(environment.Home, ".config", "git");
        Directory.CreateDirectory(xdgDirectory);

        var xdgFile = Path.Combine(xdgDirectory, "config");
        File.WriteAllText(xdgFile, "[user]\n\tname = From XDG\n");

        // GIT_CONFIG_GLOBAL would short-circuit the rule under test, so this run drops it.
        environment.GitWithoutGlobalOverride(
            environment.Home, "config", "--global", "user.email", "xdg@example.invalid");

        File.ReadAllText(xdgFile).Should().Contain("xdg@example.invalid",
            "git writes the XDG file when ~/.gitconfig is absent");

        File.Exists(Path.Combine(environment.Home, ".gitconfig")).Should().BeFalse(
            "git must not have created ~/.gitconfig");
    }

    [Fact]
    public void Git_prefers_the_home_file_when_both_exist()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var xdgDirectory = Path.Combine(environment.Home, ".config", "git");
        Directory.CreateDirectory(xdgDirectory);

        var xdgFile = Path.Combine(xdgDirectory, "config");
        File.WriteAllText(xdgFile, "[user]\n\tname = From XDG\n");

        environment.GitWithoutGlobalOverride(
            environment.Home, "config", "--global", "user.email", "home@example.invalid");

        File.ReadAllText(environment.GlobalConfigPath).Should().Contain("home@example.invalid");
        File.ReadAllText(xdgFile).Should().NotContain("home@example.invalid");
    }

    [Fact]
    public void Git_config_global_honours_the_environment_override()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        // The harness sets GIT_CONFIG_GLOBAL, and this is the assertion that it is honoured —
        // which is also what makes every other test in the suite safe to run.
        environment.Git(environment.Home, "config", "--global", "user.email", "override@example.invalid");

        File.ReadAllText(environment.GlobalConfigPath).Should().Contain("override@example.invalid");
    }

    [Fact]
    public void GitVault_resolves_the_same_per_user_file_that_git_would_write()
    {
        // The regression this pins: PlatformPathsBase used to hard-code ~/.gitconfig, so on a
        // machine keeping its configuration in the XDG location GitVault snapshotted a file git
        // never touched — and rollback restored nothing.
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var home = Path.Combine(environment.Home, ".gitconfig");
        var xdgFile = Path.Combine(environment.Home, ".config", "git", "config");
        Directory.CreateDirectory(Path.GetDirectoryName(xdgFile)!);

        // Only the XDG file exists.
        File.Delete(home);
        File.WriteAllText(xdgFile, "[user]\n\tname = From XDG\n");

        var paths = new TempPaths(environment.Home);
        paths.GlobalGitConfigPath.Should().Be(xdgFile, "git would write the XDG file here");

        // Once ~/.gitconfig exists, git prefers it and so must GitVault.
        File.WriteAllText(home, "[core]\n\tbare = false\n");
        paths.GlobalGitConfigPath.Should().Be(home);
    }

    [Fact]
    public void With_neither_file_present_the_home_file_is_the_target()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        File.Delete(environment.GlobalConfigPath);

        var paths = new TempPaths(environment.Home);
        paths.GlobalGitConfigPath.Should().Be(Path.Combine(environment.Home, ".gitconfig"),
            "git creates ~/.gitconfig when neither file exists");
    }

    [Theory]
    [InlineData(true, true, "home")]
    [InlineData(false, true, "xdg")]
    [InlineData(false, false, "home")]
    [InlineData(true, false, "home")]
    public void The_rule_GitVault_implements_matches_gits_own_choice(bool homeExists, bool xdgExists, string expected)
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        File.Delete(environment.GlobalConfigPath);

        var xdgFile = Path.Combine(environment.Home, ".config", "git", "config");
        Directory.CreateDirectory(Path.GetDirectoryName(xdgFile)!);

        if (homeExists)
        {
            File.WriteAllText(environment.GlobalConfigPath, "[core]\n\tbare = false\n");
        }

        if (xdgExists)
        {
            File.WriteAllText(xdgFile, "[core]\n\tbare = false\n");
        }

        // What GitVault predicts, before git runs.
        var homeFile = Path.Combine(environment.Home, ".gitconfig");
        var predicted = new TempPaths(environment.Home).GlobalGitConfigPath == homeFile ? "home" : "xdg";

        environment.GitWithoutGlobalOverride(
            environment.Home, "config", "--global", "gitvault.probe", "value");

        var wroteHome = File.Exists(homeFile)
            && File.ReadAllText(homeFile).Contains("probe", StringComparison.Ordinal);

        var actual = wroteHome ? "home" : "xdg";

        actual.Should().Be(expected, "this is git's documented rule");
        predicted.Should().Be(actual, "GitVault must snapshot the file git actually writes");
    }
}
