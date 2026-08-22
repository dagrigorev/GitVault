using Avalonia.Headless.XUnit;
using FluentAssertions;
using GitVault.App.Services;
using GitVault.App.ViewModels;
using GitVault.Core.Repository;
using GitVault.Localization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GitVault.App.Tests;

/// <summary>
/// Editing commits, at the level the user meets it.
/// </summary>
/// <remarks>
/// The rewrite itself is covered in <c>GitVault.Core.Tests</c> against real repositories. What is
/// checked here is the gate in front of it: that an edit writes nothing on its own, that the
/// preview is always shown, and that confirming requires typing the branch name rather than
/// pressing a button. That last one is the whole safety argument for the most consequential
/// operation in the application, so it is asserted rather than assumed.
/// </remarks>
public sealed class CommitEditingTests
{
    [AvaloniaFact]
    public async Task Editing_a_commit_writes_nothing_until_the_edits_are_applied()
    {
        using var provider = Build(out var rewriter);
        var page = await OpenAsync(provider);
        var dialogs = provider.GetRequiredService<FakeDialogService>();

        dialogs.Handler = dialog =>
        {
            ((CommitEditorViewModel)dialog).Message = "A better subject";
            return true;
        };

        await page.EditCommand.ExecuteAsync(null);

        page.HasPendingEdits.Should().BeTrue("the edit is collected");
        rewriter.Planned.Should().BeEmpty("editing alone must not even plan a rewrite");
        rewriter.Applied.Should().BeEmpty("nothing is written until the user applies");
    }

    [AvaloniaFact]
    public async Task The_edited_commit_is_marked_in_the_grid()
    {
        using var provider = Build(out _);
        var page = await OpenAsync(provider);
        var dialogs = provider.GetRequiredService<FakeDialogService>();

        var edited = page.Rows[0];
        page.SelectedRow = edited;

        dialogs.Handler = dialog =>
        {
            ((CommitEditorViewModel)dialog).AuthorEmail = "someone.else@example.invalid";
            return true;
        };

        await page.EditCommand.ExecuteAsync(null);

        edited.IsPending.Should().BeTrue();
        page.Rows[1].IsPending.Should().BeFalse("only the edited commit is marked");
    }

    [AvaloniaFact]
    public async Task Applying_shows_the_preview_and_closing_it_rewrites_nothing()
    {
        using var provider = Build(out var rewriter);
        var page = await OpenAsync(provider);
        var dialogs = provider.GetRequiredService<FakeDialogService>();

        await StageAnEditAsync(page, dialogs);

        dialogs.Answer = false;
        await page.ApplyEditsCommand.ExecuteAsync(CancellationToken.None);

        dialogs.ShownOfType<RewriteReviewViewModel>().Should().ContainSingle(
            "a rewrite is previewed like every other write");

        rewriter.Applied.Should().BeEmpty("closing the preview must rewrite nothing");
        page.HasPendingEdits.Should().BeTrue("the user declined the rewrite, not their own edits");
    }

    [AvaloniaFact]
    public async Task Confirming_without_typing_the_branch_name_rewrites_nothing()
    {
        using var provider = Build(out var rewriter);
        var page = await OpenAsync(provider);
        var dialogs = provider.GetRequiredService<FakeDialogService>();

        await StageAnEditAsync(page, dialogs);

        // The user presses the confirming button without naming the branch. The dialog refuses,
        // exactly as the real window does by keeping that button disabled.
        dialogs.Answer = true;
        await page.ApplyEditsCommand.ExecuteAsync(CancellationToken.None);

        var review = dialogs.ShownOfType<RewriteReviewViewModel>().Should().ContainSingle().Subject;
        review.CanConfirm.Should().BeFalse();
        rewriter.Applied.Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task Typing_the_wrong_branch_name_rewrites_nothing()
    {
        using var provider = Build(out var rewriter);
        var page = await OpenAsync(provider);
        var dialogs = provider.GetRequiredService<FakeDialogService>();

        await StageAnEditAsync(page, dialogs);

        dialogs.Handler = dialog =>
        {
            ((RewriteReviewViewModel)dialog).TypedConfirmation = "main2";
            return true;
        };

        await page.ApplyEditsCommand.ExecuteAsync(CancellationToken.None);

        rewriter.Applied.Should().BeEmpty("a near miss is still not the branch name");
    }

    [AvaloniaFact]
    public async Task Typing_the_branch_name_is_what_rewrites_history()
    {
        using var provider = Build(out var rewriter);
        var page = await OpenAsync(provider);
        var dialogs = provider.GetRequiredService<FakeDialogService>();

        await StageAnEditAsync(page, dialogs);

        dialogs.Handler = dialog =>
        {
            var review = (RewriteReviewViewModel)dialog;
            review.TypedConfirmation = review.BranchName;
            return true;
        };

        await page.ApplyEditsCommand.ExecuteAsync(CancellationToken.None);

        rewriter.Applied.Should().ContainSingle();
        page.HasPendingEdits.Should().BeFalse("the edits have been written, so they stop being pending");
    }

    [AvaloniaFact]
    public async Task A_blocked_rewrite_cannot_be_confirmed_even_with_the_branch_name_typed()
    {
        using var provider = Build(out var rewriter);
        var page = await OpenAsync(provider);
        var dialogs = provider.GetRequiredService<FakeDialogService>();

        await StageAnEditAsync(page, dialogs);

        rewriter.BlockNextPlan = true;
        dialogs.Handler = dialog =>
        {
            var review = (RewriteReviewViewModel)dialog;
            review.TypedConfirmation = review.BranchName;
            return true;
        };

        await page.ApplyEditsCommand.ExecuteAsync(CancellationToken.None);

        var review = dialogs.ShownOfType<RewriteReviewViewModel>().Should().ContainSingle().Subject;
        review.HasBlockers.Should().BeTrue();
        review.ConfirmationMatches.Should().BeTrue("the user did type the name");
        review.CanConfirm.Should().BeFalse("a blocker outranks the confirmation");

        rewriter.Applied.Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task Discarding_the_edits_leaves_the_repository_alone()
    {
        using var provider = Build(out var rewriter);
        var page = await OpenAsync(provider);
        var dialogs = provider.GetRequiredService<FakeDialogService>();

        await StageAnEditAsync(page, dialogs);

        page.DiscardEditsCommand.Execute(null);

        page.HasPendingEdits.Should().BeFalse();
        page.Rows.Should().OnlyContain(r => !r.IsPending);
        rewriter.Applied.Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task Editing_a_file_collects_the_content_without_writing_it()
    {
        using var provider = Build(out var rewriter);
        var page = await OpenAsync(provider);
        var dialogs = provider.GetRequiredService<FakeDialogService>();

        page.SelectedFile = page.Files[0];

        dialogs.Handler = dialog =>
        {
            ((FileEditorViewModel)dialog).Text = "ALPHA\n";
            return true;
        };

        await page.EditFileCommand.ExecuteAsync(CancellationToken.None);

        page.HasPendingEdits.Should().BeTrue();
        rewriter.Planned.Should().BeEmpty("editing a file must not plan a rewrite on its own");
        rewriter.Applied.Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task A_file_that_cannot_be_edited_is_refused_before_anything_is_typed()
    {
        using var provider = Build(out _);
        var page = await OpenAsync(provider);
        var dialogs = provider.GetRequiredService<FakeDialogService>();

        provider.GetRequiredService<StubFileReader>().EditablePath = "something.else";
        page.SelectedFile = page.Files[0];

        await page.EditFileCommand.ExecuteAsync(CancellationToken.None);

        dialogs.Shown.Should().BeEmpty("a binary or oversized file must not open an editor at all");
        page.HasPendingEdits.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task A_conflict_is_asked_about_before_the_preview_appears()
    {
        using var provider = Build(out var rewriter);
        var page = await OpenAsync(provider);
        var dialogs = provider.GetRequiredService<FakeDialogService>();

        await StageAnEditAsync(page, dialogs);
        rewriter.ConflictingPaths.Add("notes.txt");

        dialogs.Handler = dialog => dialog switch
        {
            ConflictResolutionViewModel resolution => Resolve(resolution),
            RewriteReviewViewModel review => Confirm(review),
            _ => false,
        };

        await page.ApplyEditsCommand.ExecuteAsync(CancellationToken.None);

        dialogs.Shown.Should().HaveCountGreaterThan(1);
        dialogs.Shown.OfType<ConflictResolutionViewModel>().Should().ContainSingle(
            "the conflict is settled once, then the plan is rebuilt");

        rewriter.ResolutionsSeen[^1].Should().ContainSingle(
            "the resolution is carried into the plan that gets applied");

        rewriter.Applied.Should().ContainSingle();
    }

    [AvaloniaFact]
    public async Task Closing_the_conflict_dialog_rewrites_nothing()
    {
        using var provider = Build(out var rewriter);
        var page = await OpenAsync(provider);
        var dialogs = provider.GetRequiredService<FakeDialogService>();

        await StageAnEditAsync(page, dialogs);
        rewriter.ConflictingPaths.Add("notes.txt");

        dialogs.Answer = false;
        await page.ApplyEditsCommand.ExecuteAsync(CancellationToken.None);

        dialogs.ShownOfType<RewriteReviewViewModel>().Should().BeEmpty(
            "a rewrite nobody can carry out is not worth previewing");

        rewriter.Applied.Should().BeEmpty();
        page.HasPendingEdits.Should().BeTrue("the edits survive; only this attempt was abandoned");
    }

    [AvaloniaFact]
    public void A_conflict_cannot_be_confirmed_while_a_marker_remains()
    {
        using var provider = Build(out _);
        var localizer = provider.GetRequiredService<GitVault.Localization.Localizer>();

        var conflict = new ContentConflict(
            StubRewriter.Head.Sha,
            StubRewriter.Head.ShortSha,
            StubRewriter.Head.Subject,
            "notes.txt",
            "<<<<<<< notes.txt\nmine\n=======\ntheirs\n>>>>>>> edited version\n");

        var dialog = new ConflictResolutionViewModel(localizer, conflict);

        dialog.HasMarkers.Should().BeTrue();
        dialog.CanConfirm.Should().BeFalse("a marker committed into history is a broken file");

        dialog.Text = "settled\n";

        dialog.HasMarkers.Should().BeFalse();
        dialog.CanConfirm.Should().BeTrue();
        dialog.ToResolution().Content.Should().Be("settled\n");
    }

    [AvaloniaFact]
    public async Task A_file_edit_and_a_message_edit_reach_the_same_commit_together()
    {
        using var provider = Build(out var rewriter);
        var page = await OpenAsync(provider);
        var dialogs = provider.GetRequiredService<FakeDialogService>();

        await StageAnEditAsync(page, dialogs);

        page.SelectedFile = page.Files[0];
        dialogs.Handler = dialog =>
        {
            ((FileEditorViewModel)dialog).Text = "ALPHA\n";
            return true;
        };

        await page.EditFileCommand.ExecuteAsync(CancellationToken.None);

        dialogs.Handler = dialog => dialog is RewriteReviewViewModel review && Confirm(review);
        await page.ApplyEditsCommand.ExecuteAsync(CancellationToken.None);

        var applied = rewriter.Applied.Should().ContainSingle().Subject;
        var edit = applied.Steps.Should().ContainSingle().Subject.Edit!;

        edit.Message.Should().Be("A better subject", "the message edit was not lost");
        edit.Files.Should().ContainSingle().Which.Content.Should().Be("ALPHA\n");
    }

    /// <summary>Settles a conflict the way a user would, by removing the markers.</summary>
    private static bool Resolve(ConflictResolutionViewModel dialog)
    {
        dialog.Text = "settled\n";
        return true;
    }

    /// <summary>Confirms a rewrite the way a user would, by typing the branch name.</summary>
    private static bool Confirm(RewriteReviewViewModel dialog)
    {
        dialog.TypedConfirmation = dialog.BranchName;
        return true;
    }

    [AvaloniaFact]
    public async Task Changing_the_selection_quickly_lists_each_file_once()
    {
        using var provider = Build(out _);
        var page = await OpenAsync(provider);

        // A grid pushes a null selection back while it attaches and the page restores it, so
        // several file reads are in flight at once. Each clears the list before its own await; if
        // they all append afterwards the same file is listed two or three times.
        for (var i = 0; i < 4; i++)
        {
            page.SelectedRow = page.Rows[i % page.Rows.Count];
        }

        await Task.Yield();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        page.Files.Select(f => f.Change.Path).Should().OnlyHaveUniqueItems(
            "only the newest read is allowed to fill the list");
    }

    [AvaloniaFact]
    public void An_edit_that_changes_nothing_is_not_an_edit()
    {
        using var provider = Build(out _);
        var localizer = provider.GetRequiredService<GitVault.Localization.Localizer>();

        var editor = new CommitEditorViewModel(localizer, StubRewriter.Head);

        editor.ToEdit().IsEmpty.Should().BeTrue();
        editor.CanConfirm.Should().BeFalse("there is nothing to confirm");

        editor.AuthorName = "Someone Else";

        editor.ToEdit().IsEmpty.Should().BeFalse();
        editor.CanConfirm.Should().BeTrue();
    }

    [AvaloniaFact]
    public void A_date_without_an_offset_is_refused_rather_than_guessed()
    {
        using var provider = Build(out _);
        var localizer = provider.GetRequiredService<GitVault.Localization.Localizer>();

        var editor = new CommitEditorViewModel(localizer, StubRewriter.Head)
        {
            AuthorDate = "2024-03-01 09:15:00",
        };

        editor.DatesAreValid.Should().BeFalse();
        editor.CanConfirm.Should().BeFalse("writing a guessed timezone into history is not an option");

        editor.AuthorDate = "2024-03-04 09:15:00 +03:00";

        editor.DatesAreValid.Should().BeTrue();
        editor.ToEdit().AuthorDate.Should().Be(
            new DateTimeOffset(2024, 3, 4, 9, 15, 0, TimeSpan.FromHours(3)),
            "the offset the user typed is written, not the one this machine happens to be in");
    }

    [AvaloniaFact]
    public void The_preview_names_how_far_the_rewrite_reaches()
    {
        using var provider = Build(out _);
        var localizer = provider.GetRequiredService<GitVault.Localization.Localizer>();

        var plan = new RewritePlan("/src/alpha", "main", "refs/heads/main")
        {
            Steps =
            [
                new RewriteStep(StubRewriter.Older, new CommitEdit(StubRewriter.Older.Sha) { Message = "Fixed" }, true),
                new RewriteStep(StubRewriter.Head, null, false),
            ],
            RefsToBackUp = ["refs/heads/main"],
            StrandedRefs = ["refs/tags/v1.0"],
            Warnings = [RewriteWarnings.SignaturesWillBeLost],
        };

        var review = new RewriteReviewViewModel(localizer, plan);

        review.Rows.Should().HaveCount(2);
        review.Rows[0].Reason.Should().Be(localizer[Keys.Rewrite_Reason_Edited]);
        review.Rows[1].Reason.Should().Be(localizer[Keys.Rewrite_Reason_Carried]);
        review.Rows[0].Subject.Should().Be("Fixed", "the preview shows the message the commit will end up with");

        review.HasStrandedRefs.Should().BeTrue();
        review.Warnings.Single().Should().Be(localizer[RewriteWarnings.SignaturesWillBeLost],
            "an identifier must be rendered in the reader's language");

        review.ReachCaption.Should().Be(localizer.Format(Keys.Rewrite_Reach, 1, 1));
    }

    /// <summary>Opens the page against the stub repository, with a commit selected.</summary>
    private static async Task<CommitsViewModel> OpenAsync(ServiceProvider provider)
    {
        var page = provider.GetRequiredService<CommitsViewModel>();

        provider.GetRequiredService<RepositoryContext>().Select("/src/alpha", "alpha");
        await page.ReloadAsync(CancellationToken.None);

        page.Rows.Should().NotBeEmpty();
        page.SelectedRow.Should().NotBeNull();

        return page;
    }

    /// <summary>Edits the selected commit the way the user would, and collects the change.</summary>
    private static async Task StageAnEditAsync(CommitsViewModel page, FakeDialogService dialogs)
    {
        dialogs.Handler = dialog =>
        {
            ((CommitEditorViewModel)dialog).Message = "A better subject";
            return true;
        };

        await page.EditCommand.ExecuteAsync(null);

        page.HasPendingEdits.Should().BeTrue();
        dialogs.Handler = null;
    }

    private static ServiceProvider Build(out StubRewriter rewriter)
    {
        var stub = new StubRewriter();
        rewriter = stub;

        return TestServices.Build(services =>
        {
            services.AddSingleton<IRepositoryInspector>(new StubInspector());
            services.AddSingleton<ICommitReader>(new StubCommitReader());
            services.AddSingleton<IHistoryRewriter>(stub);
            services.AddSingleton<StubFileReader>();
            services.AddSingleton<IFileContentReader>(sp => sp.GetRequiredService<StubFileReader>());
        });
    }
}

/// <summary>A reader that reports two commits, without a repository on disk.</summary>
internal sealed class StubCommitReader : ICommitReader
{
    public Task<IReadOnlyList<GitCommit>> ReadAsync(
        string repositoryPath, CommitQuery query, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<GitCommit>>([StubRewriter.Head, StubRewriter.Older]);

    public Task<GitCommit?> ReadOneAsync(
        string repositoryPath, string revision, CancellationToken cancellationToken) =>
        Task.FromResult<GitCommit?>(StubRewriter.Head);

    public Task<IReadOnlyList<CommitFileChange>> ReadChangesAsync(
        string repositoryPath, string sha, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CommitFileChange>>(
            [new CommitFileChange(FileChangeStatus.Modified, "notes.txt", null, 1, 0)]);
}

/// <summary>A reader that offers one editable file, and refuses anything else.</summary>
internal sealed class StubFileReader : IFileContentReader
{
    /// <summary>Path this reader will hand over.</summary>
    public string EditablePath { get; set; } = "notes.txt";

    /// <summary>Content it reports for that path.</summary>
    public string Text { get; set; } = "alpha\n";

    public Task<FileContent?> ReadAsync(
        string repositoryPath, string sha, string path, CancellationToken cancellationToken) =>
        Task.FromResult(string.Equals(path, EditablePath, StringComparison.Ordinal)
            ? new FileContent(path, "100644", Text)
            : null);
}

/// <summary>A rewriter that records what it was asked to do, and changes nothing.</summary>
internal sealed class StubRewriter : IHistoryRewriter
{
    /// <summary>The tip commit the stub reader reports.</summary>
    public static GitCommit Head { get; } = new(
        "1111111111111111111111111111111111111111",
        "11111111",
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        ["2222222222222222222222222222222222222222"],
        "Ada Lovelace",
        "ada@example.invalid",
        new DateTimeOffset(2024, 3, 1, 9, 15, 0, TimeSpan.FromHours(3)),
        "Ada Lovelace",
        "ada@example.invalid",
        new DateTimeOffset(2024, 3, 1, 9, 15, 0, TimeSpan.FromHours(3)),
        "Second commit",
        string.Empty,
        CommitSignature.Unsigned);

    /// <summary>The commit before the tip.</summary>
    public static GitCommit Older { get; } = new(
        "2222222222222222222222222222222222222222",
        "22222222",
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
        [],
        "Ada Lovelace",
        "ada@example.invalid",
        new DateTimeOffset(2024, 2, 1, 9, 15, 0, TimeSpan.FromHours(3)),
        "Ada Lovelace",
        "ada@example.invalid",
        new DateTimeOffset(2024, 2, 1, 9, 15, 0, TimeSpan.FromHours(3)),
        "Initial commit",
        string.Empty,
        CommitSignature.Unsigned);

    /// <summary>Plans that were built.</summary>
    public List<RewritePlan> Planned { get; } = [];

    /// <summary>Plans that reached <see cref="ApplyAsync"/> and were not refused.</summary>
    public List<RewritePlan> Applied { get; } = [];

    /// <summary>When true, the next plan built carries a blocker.</summary>
    public bool BlockNextPlan { get; set; }

    /// <summary>
    /// Conflicts the next plan reports, one for each commit named here.
    /// </summary>
    /// <remarks>
    /// A conflict disappears once a resolution naming the same commit and path arrives, which is
    /// how the real rewriter behaves and what lets a test drive the resolution loop.
    /// </remarks>
    public List<string> ConflictingPaths { get; } = [];

    /// <summary>Resolutions each plan was built with, in order.</summary>
    public List<IReadOnlyList<ConflictResolution>> ResolutionsSeen { get; } = [];

    public Task<RewritePlan> PlanAsync(
        string repositoryPath, IReadOnlyList<CommitEdit> edits, CancellationToken cancellationToken) =>
        PlanAsync(repositoryPath, edits, [], cancellationToken);

    public Task<RewritePlan> PlanAsync(
        string repositoryPath,
        IReadOnlyList<CommitEdit> edits,
        IReadOnlyList<ConflictResolution> resolutions,
        CancellationToken cancellationToken)
    {
        var blockers = new List<string>();

        if (BlockNextPlan)
        {
            blockers.Add(RewriteBlockers.WorkingTreeDirty);
            BlockNextPlan = false;
        }

        var conflicts = ConflictingPaths
            .Where(path => !resolutions.Any(r =>
                r.Sha == Head.Sha && string.Equals(r.Path, path, StringComparison.Ordinal)))
            .Select(path => new ContentConflict(
                Head.Sha, Head.ShortSha, Head.Subject, path, "<<<<<<< " + path + "\n=======\n>>>>>>>\n"))
            .ToList();

        if (conflicts.Count > 0)
        {
            blockers.Add(RewriteBlockers.UnresolvedConflicts);
        }

        var plan = new RewritePlan(repositoryPath, "main", "refs/heads/main")
        {
            Steps = [.. edits.Select(e => new RewriteStep(
                e.Sha == Older.Sha ? Older : Head, e, true))],
            RefsToBackUp = ["refs/heads/main"],
            Blockers = blockers,
            Conflicts = conflicts,
            OriginalTip = Head.Sha,
        };

        Planned.Add(plan);
        ResolutionsSeen.Add([.. resolutions]);
        return Task.FromResult(plan);
    }

    public Task<RewriteResult> ApplyAsync(RewritePlan plan, CancellationToken cancellationToken)
    {
        // Mirrors the real rewriter: a plan that cannot be applied is refused here too.
        if (plan.CanApply)
        {
            Applied.Add(plan);
        }

        return Task.FromResult(new RewriteResult("backup", Head.Sha)
        {
            Steps = [.. plan.Steps.Select(s => new GitVault.Core.Models.ActivationStepResult(
                HistoryRewriter.StepId,
                GitVault.Core.Models.StepOutcome.Applied,
                s.Original.ShortSha))],
        });
    }
}
