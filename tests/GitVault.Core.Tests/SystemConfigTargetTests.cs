using FluentAssertions;
using GitVault.Core.Models;
using Xunit;
using Xunit.Abstractions;

namespace GitVault.Core.Tests;

/// <summary>
/// Which file the system scope actually is.
/// </summary>
/// <remarks>
/// The same class of mismatch that made the global scope dangerous: the file GitVault copies into
/// a snapshot and the file git writes have to be the same one, or a rollback restores something
/// that was never changed. The platform's candidate list is a good guess, and a guess is not good
/// enough, so git is asked.
///
/// The test redirects git's system configuration with <c>GIT_CONFIG_SYSTEM</c> to somewhere no
/// candidate list would ever name. If the answer still comes back as that file, the value was
/// obtained rather than assumed.
/// </remarks>
public sealed class SystemConfigTargetTests(ITestOutputHelper output)
{
    [Fact]
    public async Task The_system_scope_is_the_file_git_names_rather_than_a_candidate()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        var elsewhere = Path.Combine(environment.Home, "unusual-install", "gitconfig");
        Directory.CreateDirectory(Path.GetDirectoryName(elsewhere)!);
        await File.WriteAllTextAsync(elsewhere, "[core]\n\tpager = less\n");

        using var redirect = new EnvironmentVariable("GIT_CONFIG_SYSTEM", elsewhere);
        using var allowSystem = new EnvironmentVariable("GIT_CONFIG_NOSYSTEM", null);

        var config = await environment.BuildConfigServiceAsync();
        var resolved = config.ResolveConfigFilePath(GitConfigScope.System, null);

        if (resolved is null)
        {
            output.WriteLine("This git does not honour GIT_CONFIG_SYSTEM; skipping.");
            return;
        }

        Path.GetFullPath(resolved).Should().Be(
            Path.GetFullPath(elsewhere),
            "no candidate list names this path, so the value can only have come from git");

        environment.Paths.SystemGitConfigCandidates.Should().NotContain(
            c => string.Equals(Path.GetFullPath(c), Path.GetFullPath(elsewhere), StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_system_scope_nobody_can_name_is_refused_rather_than_written_blind()
    {
        using var environment = TempGitEnvironment.TryCreate();
        if (environment is null)
        {
            output.WriteLine("git is not on PATH; skipping.");
            return;
        }

        // An origin is reported per entry, so an empty system configuration tells git nothing to
        // pass on. With no candidate to fall back to either, the file cannot be named — and a
        // write GitVault cannot snapshot is a write it cannot undo, so the plan is blocked instead.
        var empty = Path.Combine(environment.Home, "empty-system-config");
        await File.WriteAllTextAsync(empty, string.Empty);

        using var redirect = new EnvironmentVariable("GIT_CONFIG_SYSTEM", empty);
        using var allowSystem = new EnvironmentVariable("GIT_CONFIG_NOSYSTEM", null);

        var config = await environment.BuildConfigServiceAsync();

        config.ResolveConfigFilePath(GitConfigScope.System, null).Should().BeNull(
            "the harness has no candidate list, so there is nothing left to guess with");

        var editor = new GitVault.Core.Repository.ConfigEditor(
            config, new GitVault.Core.Profiles.SnapshotService(environment.Paths));

        var plan = await editor.PlanSetAsync(
            "core.pager", "less", GitConfigScope.System, null, CancellationToken.None);

        plan.Blockers.Should().Contain(GitVault.Core.Repository.BlockerMessages.NoConfigurationFile);
        plan.CanApply.Should().BeFalse("nothing is written that could not be put back");
    }
}

/// <summary>Sets an environment variable for the length of a test, and puts it back.</summary>
internal sealed class EnvironmentVariable : IDisposable
{
    private readonly string _name;
    private readonly string? _previous;

    /// <summary>Sets the variable.</summary>
    /// <param name="name">Variable to set.</param>
    /// <param name="value">Value, or null to remove it.</param>
    internal EnvironmentVariable(string name, string? value)
    {
        _name = name;
        _previous = Environment.GetEnvironmentVariable(name);

        Environment.SetEnvironmentVariable(name, value);
    }

    /// <inheritdoc/>
    public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
}
