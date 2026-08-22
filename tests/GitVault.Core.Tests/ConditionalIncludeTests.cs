using FluentAssertions;
using GitVault.Core.Git;
using Xunit;

namespace GitVault.Core.Tests;

/// <summary>
/// The conditions that decide which configuration file applies to a repository.
/// </summary>
/// <remarks>
/// This is where a wrong answer becomes a wrong identity, which is the one thing this application
/// exists to get right. Someone with an <c>includeIf "gitdir:~/work/"</c> section expects their
/// work address in work repositories and their own address everywhere else; an evaluator that is
/// slightly too eager or slightly too shy tells them the opposite, and the interface repeats it
/// with confidence.
///
/// The cases below are the ones git's own documentation calls out, plus the ones a naive
/// implementation gets wrong: a trailing slash matching everything beneath, a pattern with no
/// slash at all being anchored differently, case folding applying only to the <c>/i</c> form, and
/// a branch condition on a repository that is not on a branch.
/// </remarks>
public sealed class ConditionalIncludeTests
{
    private static readonly TempPaths Paths = new("/home/qa");

    [Theory]
    // A trailing slash means "this directory and everything under it".
    [InlineData("gitdir:/home/qa/work/", "/home/qa/work/project/.git", true)]
    [InlineData("gitdir:/home/qa/work/", "/home/qa/work/deep/nested/project/.git", true)]
    [InlineData("gitdir:/home/qa/work/", "/home/qa/personal/project/.git", false)]

    // Without a trailing slash the pattern has to match the directory itself.
    [InlineData("gitdir:/home/qa/work/project/.git", "/home/qa/work/project/.git", true)]
    [InlineData("gitdir:/home/qa/work/project", "/home/qa/work/project/.git", false)]

    // A double star crosses separators; a single one does not.
    [InlineData("gitdir:/home/qa/**/deep/.git", "/home/qa/a/b/deep/.git", true)]
    [InlineData("gitdir:/home/qa/*/deep/.git", "/home/qa/a/b/deep/.git", false)]
    [InlineData("gitdir:/home/qa/*/deep/.git", "/home/qa/a/deep/.git", true)]
    public void A_gitdir_condition_matches_what_git_says_it_matches(
        string condition,
        string gitDirectory,
        bool expected)
    {
        var context = new GitConfigIncludeContext(gitDirectory, null);

        GitConfigConditions.Matches(condition, "/home/qa/.gitconfig", context, Paths)
            .Should().Be(expected);
    }

    [Fact]
    public void Case_folding_applies_only_to_the_slash_i_form()
    {
        var context = new GitConfigIncludeContext("/home/qa/Work/project/.git", null);

        GitConfigConditions.Matches("gitdir:/home/qa/work/", "/home/qa/.gitconfig", context, Paths)
            .Should().BeFalse("the plain form is case-sensitive, whatever the filesystem thinks");

        GitConfigConditions.Matches("gitdir/i:/home/qa/work/", "/home/qa/.gitconfig", context, Paths)
            .Should().BeTrue();
    }

    [Fact]
    public void A_tilde_is_the_users_home_rather_than_a_directory_called_tilde()
    {
        // Rooted the way this platform roots things, because expanding "~" ends in a full-path
        // comparison and a POSIX-looking path on Windows would be normalised onto the current
        // drive — a difference in the test, not in the thing being tested.
        var home = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "gitvault-tilde"));
        var paths = new TempPaths(home);

        var context = new GitConfigIncludeContext(
            Path.Combine(home, "work", "project", ".git").Replace(Path.DirectorySeparatorChar, '/'),
            null);

        GitConfigConditions.Matches("gitdir:~/work/", Path.Combine(home, ".gitconfig"), context, paths)
            .Should().BeTrue();
    }

    [Fact]
    public void A_relative_pattern_is_relative_to_the_file_it_appeared_in()
    {
        // "./" is how a shared configuration refers to its own neighbourhood without knowing where
        // the user put it.
        var context = new GitConfigIncludeContext("/opt/shared/repos/project/.git", null);

        GitConfigConditions.Matches("gitdir:./repos/", "/opt/shared/gitconfig", context, Paths)
            .Should().BeTrue();

        GitConfigConditions.Matches("gitdir:./repos/", "/opt/elsewhere/gitconfig", context, Paths)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("onbranch:main", "main", true)]
    [InlineData("onbranch:main", "feature", false)]
    [InlineData("onbranch:feature/*", "feature/login", true)]
    [InlineData("onbranch:feature/*", "feature/login/step-two", false)]
    [InlineData("onbranch:feature/**", "feature/login/step-two", true)]

    // A pattern ending in a slash means "and everything beneath", the same as for directories.
    [InlineData("onbranch:release/", "release/2024", true)]
    public void An_onbranch_condition_matches_the_checked_out_branch(
        string condition,
        string branch,
        bool expected)
    {
        var context = new GitConfigIncludeContext("/home/qa/project/.git", branch);

        GitConfigConditions.Matches(condition, "/home/qa/.gitconfig", context, Paths)
            .Should().Be(expected);
    }

    [Fact]
    public void A_detached_head_matches_no_branch_condition()
    {
        // Not "matches everything" and not an exception: a repository on no branch is simply not
        // on the branch the condition names.
        var context = new GitConfigIncludeContext("/home/qa/project/.git", null);

        GitConfigConditions.Matches("onbranch:main", "/home/qa/.gitconfig", context, Paths)
            .Should().BeFalse();

        GitConfigConditions.Matches("onbranch:**", "/home/qa/.gitconfig", context, Paths)
            .Should().BeFalse();
    }

    [Fact]
    public void Without_a_repository_nothing_conditional_applies()
    {
        // Reading the global configuration on its own has no repository to test a condition
        // against, and guessing one would attribute settings to the wrong scope.
        GitConfigConditions.Matches("gitdir:/home/qa/work/", "/home/qa/.gitconfig", null, Paths)
            .Should().BeFalse();

        GitConfigConditions.Matches("onbranch:main", "/home/qa/.gitconfig", null, Paths)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hasconfig:remote.*.url:https://example.invalid/**")]
    [InlineData("something git has not invented yet:value")]
    public void A_condition_this_does_not_understand_is_declined_rather_than_assumed(string condition)
    {
        // Declining is the safe direction: an include that should have applied means a setting is
        // reported as absent, while an include that should not have means the wrong identity is
        // reported as active.
        var context = new GitConfigIncludeContext("/home/qa/work/project/.git", "main");

        GitConfigConditions.Matches(condition, "/home/qa/.gitconfig", context, Paths)
            .Should().BeFalse();
    }
}
