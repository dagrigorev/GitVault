using Avalonia.Headless.XUnit;
using FluentAssertions;
using GitVault.App.Services;
using GitVault.App.ViewModels;
using GitVault.Core.Repository;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GitVault.App.Tests;

/// <summary>
/// The page that removes, moves and re-attributes across a whole history.
/// </summary>
/// <remarks>
/// The engine is covered in <c>GitVault.Core.Tests</c> against real repositories. What is checked
/// here is that these three take the same route as every other write: a plan, a preview, and a
/// rewrite that happens only when the user types the branch name. Three operations sharing one
/// page is exactly the arrangement where one of them can quietly acquire a shortcut.
/// </remarks>
public sealed class HistoryToolsPageTests
{
    [AvaloniaFact]
    public async Task Removing_a_path_is_previewed_and_closing_the_preview_rewrites_nothing()
    {
        using var provider = Build(out var tools, out var rewriter);
        var page = Open(provider);
        var dialogs = provider.GetRequiredService<FakeDialogService>();

        page.RemovePath = "secret.key";
        dialogs.Answer = false;

        await page.RemoveCommand.ExecuteAsync(CancellationToken.None);

        tools.RemovedPaths.Should().ContainSingle().Which.Should().Be("secret.key");
        dialogs.ShownOfType<RewriteReviewViewModel>().Should().ContainSingle();
        rewriter.Applied.Should().BeEmpty("closing the preview must rewrite nothing");
        page.RemovePath.Should().Be("secret.key", "the typed path survives an abandoned attempt");
    }

    [AvaloniaFact]
    public async Task Typing_the_branch_name_is_what_removes_a_path()
    {
        using var provider = Build(out _, out var rewriter);
        var page = Open(provider);
        var dialogs = provider.GetRequiredService<FakeDialogService>();

        page.RemovePath = "secret.key";
        dialogs.Handler = dialog => dialog is RewriteReviewViewModel review && Confirm(review);

        await page.RemoveCommand.ExecuteAsync(CancellationToken.None);

        rewriter.Applied.Should().ContainSingle();
        page.RemovePath.Should().BeEmpty("the field clears once the work is done");
    }

    [AvaloniaFact]
    public async Task Confirming_without_typing_the_branch_name_removes_nothing()
    {
        using var provider = Build(out _, out var rewriter);
        var page = Open(provider);
        var dialogs = provider.GetRequiredService<FakeDialogService>();

        page.RemovePath = "secret.key";

        // The user presses the confirming button without naming the branch.
        dialogs.Answer = true;
        await page.RemoveCommand.ExecuteAsync(CancellationToken.None);

        var review = dialogs.ShownOfType<RewriteReviewViewModel>().Should().ContainSingle().Subject;
        review.CanConfirm.Should().BeFalse();
        rewriter.Applied.Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task A_blocked_path_operation_cannot_be_confirmed_at_all()
    {
        using var provider = Build(out var tools, out var rewriter);
        var page = Open(provider);
        var dialogs = provider.GetRequiredService<FakeDialogService>();

        tools.BlockNextPlan = RewriteBlockers.PathNotInHistory;
        page.RemovePath = "nothing/here.txt";

        dialogs.Handler = dialog => dialog is RewriteReviewViewModel review && Confirm(review);
        await page.RemoveCommand.ExecuteAsync(CancellationToken.None);

        var review = dialogs.ShownOfType<RewriteReviewViewModel>().Should().ContainSingle().Subject;
        review.HasBlockers.Should().BeTrue();
        review.CanConfirm.Should().BeFalse("a blocker outranks the confirmation");
        rewriter.Applied.Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task Moving_a_path_goes_through_the_same_preview()
    {
        using var provider = Build(out var tools, out var rewriter);
        var page = Open(provider);
        var dialogs = provider.GetRequiredService<FakeDialogService>();

        page.RenamePath = "notes.txt";
        page.RenameNewPath = "docs/notes.txt";

        dialogs.Handler = dialog => dialog is RewriteReviewViewModel review && Confirm(review);
        await page.RenameCommand.ExecuteAsync(CancellationToken.None);

        tools.Renamed.Should().ContainSingle().Which.Should().Be("notes.txt -> docs/notes.txt");
        rewriter.Applied.Should().ContainSingle();
    }

    [AvaloniaFact]
    public async Task Replacing_an_identity_goes_through_the_same_preview()
    {
        using var provider = Build(out var tools, out var rewriter);
        var page = Open(provider);
        var dialogs = provider.GetRequiredService<FakeDialogService>();

        page.OldEmail = "wrong@example.invalid";
        page.NewName = "Right Name";
        page.NewEmail = "right@example.invalid";

        dialogs.Handler = dialog => dialog is RewriteReviewViewModel review && Confirm(review);
        await page.ReplaceIdentityCommand.ExecuteAsync(CancellationToken.None);

        tools.Identities.Should().ContainSingle()
            .Which.Should().Be("wrong@example.invalid -> Right Name <right@example.invalid>");

        rewriter.Applied.Should().ContainSingle();
        page.OldEmail.Should().BeEmpty();
    }

    [AvaloniaFact]
    public void An_operation_with_an_empty_field_cannot_be_started()
    {
        using var provider = Build(out _, out _);
        var page = Open(provider);

        page.CanRemove.Should().BeFalse();
        page.CanRename.Should().BeFalse();
        page.CanReplaceIdentity.Should().BeFalse();

        page.RemovePath = "   ";
        page.CanRemove.Should().BeFalse("whitespace is not a path");

        page.RemovePath = "secret.key";
        page.CanRemove.Should().BeTrue();

        page.RenamePath = "notes.txt";
        page.CanRename.Should().BeFalse("a move needs both ends");

        page.RenameNewPath = "docs/notes.txt";
        page.CanRename.Should().BeTrue();
    }

    [AvaloniaFact]
    public void Nothing_can_be_started_without_a_repository()
    {
        using var provider = Build(out _, out _);
        var page = provider.GetRequiredService<HistoryToolsViewModel>();

        page.HasRepository.Should().BeFalse();

        page.RemovePath = "secret.key";
        page.CanRemove.Should().BeFalse("there is nothing to remove it from");
    }

    /// <summary>Confirms a rewrite the way a user would, by typing the branch name.</summary>
    private static bool Confirm(RewriteReviewViewModel dialog)
    {
        dialog.TypedConfirmation = dialog.BranchName;
        return true;
    }

    private static HistoryToolsViewModel Open(ServiceProvider provider)
    {
        provider.GetRequiredService<RepositoryContext>().Select("/src/alpha", "alpha");
        return provider.GetRequiredService<HistoryToolsViewModel>();
    }

    private static ServiceProvider Build(out StubHistoryTools tools, out StubRewriter rewriter)
    {
        var stubTools = new StubHistoryTools();
        var stubRewriter = new StubRewriter();

        tools = stubTools;
        rewriter = stubRewriter;

        return TestServices.Build(services =>
        {
            services.AddSingleton<IRepositoryInspector>(new StubInspector());
            services.AddSingleton<ICommitReader>(new StubCommitReader());
            services.AddSingleton<IHistoryRewriter>(stubRewriter);
            services.AddSingleton<IHistoryTools>(stubTools);
            services.AddSingleton<StubFileReader>();
            services.AddSingleton<IFileContentReader>(sp => sp.GetRequiredService<StubFileReader>());
        });
    }
}

/// <summary>Tools that record what they were asked to plan, and change nothing.</summary>
internal sealed class StubHistoryTools : IHistoryTools
{
    /// <summary>Paths a removal was planned for.</summary>
    public List<string> RemovedPaths { get; } = [];

    /// <summary>Moves that were planned, written as "from -&gt; to".</summary>
    public List<string> Renamed { get; } = [];

    /// <summary>Identity replacements that were planned.</summary>
    public List<string> Identities { get; } = [];

    /// <summary>Blocker the next plan carries, when set.</summary>
    public string? BlockNextPlan { get; set; }

    public Task<RewritePlan> PlanRemovePathAsync(
        string repositoryPath, string path, CancellationToken cancellationToken)
    {
        RemovedPaths.Add(path);
        return Task.FromResult(Plan(repositoryPath));
    }

    public Task<RewritePlan> PlanRenamePathAsync(
        string repositoryPath, string path, string newPath, CancellationToken cancellationToken)
    {
        Renamed.Add(path + " -> " + newPath);
        return Task.FromResult(Plan(repositoryPath));
    }

    public Task<RewritePlan> PlanReplaceIdentityAsync(
        string repositoryPath, string oldEmail, string name, string email, CancellationToken cancellationToken)
    {
        Identities.Add($"{oldEmail} -> {name} <{email}>");
        return Task.FromResult(Plan(repositoryPath));
    }

    private RewritePlan Plan(string repositoryPath)
    {
        var blockers = BlockNextPlan is { Length: > 0 } blocker ? new[] { blocker } : [];
        BlockNextPlan = null;

        return new RewritePlan(repositoryPath, "main", "refs/heads/main")
        {
            Steps = [new RewriteStep(StubRewriter.Head, new CommitEdit(StubRewriter.Head.Sha), true)],
            RefsToBackUp = ["refs/heads/main"],
            Blockers = blockers,
            OriginalTip = StubRewriter.Head.Sha,
        };
    }
}
