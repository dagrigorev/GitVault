using Avalonia.Headless.XUnit;
using FluentAssertions;
using GitVault.App.Services;
using GitVault.App.ViewModels;
using GitVault.Core.Models;
using GitVault.Core.Profiles;
using GitVault.Core.Repository;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GitVault.App.Tests;

/// <summary>
/// The pages that edit the repository's own files: ignore lists, attributes, mailmap and hooks.
/// </summary>
/// <remarks>
/// The engines are covered in <c>GitVault.Core.Tests</c> against real repositories. What is
/// checked here is that both pages take the same route as every other write — a plan, a preview,
/// and a file touched only when the user confirms — and that the hooks page never runs anything.
/// </remarks>
public sealed class RepositoryFilePageTests
{
    [AvaloniaFact]
    public async Task Saving_a_file_is_previewed_and_closing_the_preview_writes_nothing()
    {
        using var provider = Build(out var files, out _);
        var page = await OpenFilesAsync(provider);
        var dialogs = provider.GetRequiredService<FakeDialogService>();

        page.Text = "bin/\nobj/\n";
        dialogs.Answer = false;

        await page.SaveCommand.ExecuteAsync(CancellationToken.None);

        dialogs.ShownOfType<OperationReviewViewModel>().Should().ContainSingle();
        files.Applied.Should().BeEmpty("closing the preview must write nothing");
    }

    [AvaloniaFact]
    public async Task Confirming_the_preview_is_what_writes_the_file()
    {
        using var provider = Build(out var files, out _);
        var page = await OpenFilesAsync(provider);
        var dialogs = provider.GetRequiredService<FakeDialogService>();

        page.Text = "bin/\nobj/\n";
        dialogs.Answer = true;

        await page.SaveCommand.ExecuteAsync(CancellationToken.None);

        files.Applied.Should().ContainSingle();
        files.Planned.Should().Contain(p => p.Text == "bin/\nobj/\n");
    }

    [AvaloniaFact]
    public async Task A_file_that_cannot_be_edited_is_shown_as_such_rather_than_offered()
    {
        using var provider = Build(out var files, out _);
        files.Unreadable.Add(RepositoryFileKind.Attributes);

        var page = await OpenFilesAsync(provider);
        page.SelectedRow = page.Rows.Single(r => r.Kind == RepositoryFileKind.Attributes);

        page.CanEdit.Should().BeFalse();
        page.IsUnreadable.Should().BeTrue();

        await page.SaveCommand.ExecuteAsync(CancellationToken.None);
        files.Applied.Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task The_page_says_whether_a_change_reaches_anyone_else()
    {
        using var provider = Build(out _, out _);
        var page = await OpenFilesAsync(provider);

        page.SelectedRow = page.Rows.Single(r => r.Kind == RepositoryFileKind.Ignore);
        page.IsTracked.Should().BeTrue("a committed file's change reaches everyone");

        page.SelectedRow = page.Rows.Single(r => r.Kind == RepositoryFileKind.Exclude);
        page.IsTracked.Should().BeFalse("the exclude file is this clone's own business");
    }

    [AvaloniaFact]
    public async Task Editing_a_hook_is_previewed_and_closing_the_preview_writes_nothing()
    {
        using var provider = Build(out _, out var hooks);
        var page = await OpenHooksAsync(provider);
        var dialogs = provider.GetRequiredService<FakeDialogService>();

        dialogs.Handler = dialog => dialog switch
        {
            HookEditorViewModel editor => Fill(editor),
            _ => false,
        };

        await page.EditCommand.ExecuteAsync(CancellationToken.None);

        dialogs.ShownOfType<OperationReviewViewModel>().Should().ContainSingle();
        hooks.Applied.Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task Confirming_the_preview_is_what_writes_a_hook()
    {
        using var provider = Build(out _, out var hooks);
        var page = await OpenHooksAsync(provider);
        var dialogs = provider.GetRequiredService<FakeDialogService>();

        dialogs.Handler = dialog => dialog switch
        {
            HookEditorViewModel editor => Fill(editor),
            _ => true,
        };

        await page.EditCommand.ExecuteAsync(CancellationToken.None);

        hooks.Applied.Should().ContainSingle();
        hooks.Written.Should().ContainSingle().Which.Should().Be("pre-commit enabled");
    }

    [AvaloniaFact]
    public async Task Unchecking_the_box_asks_for_a_hook_git_will_not_run()
    {
        using var provider = Build(out _, out var hooks);
        var page = await OpenHooksAsync(provider);
        var dialogs = provider.GetRequiredService<FakeDialogService>();

        dialogs.Handler = dialog =>
        {
            if (dialog is HookEditorViewModel editor)
            {
                editor.Script = "#!/bin/sh\nexit 0\n";
                editor.IsEnabled = false;
                return true;
            }

            return true;
        };

        await page.EditCommand.ExecuteAsync(CancellationToken.None);

        hooks.Written.Should().ContainSingle().Which.Should().Be("pre-commit disabled");
    }

    [AvaloniaFact]
    public async Task A_hook_that_git_will_skip_anyway_is_named_as_such()
    {
        using var provider = Build(out _, out var hooks);
        hooks.Hooks =
        [
            new GitHook("pre-commit", "/repo/.git/hooks/pre-commit", true, true, false, 40),
        ];

        var page = await OpenHooksAsync(provider);

        page.SelectedRow!.Hook.IsInertlyDisabled.Should().BeTrue();
        page.SelectionIsInert.Should().BeTrue("git skips it silently, so the page has to say it");
    }

    [AvaloniaFact]
    public async Task A_binary_hook_is_refused_before_an_editor_opens()
    {
        using var provider = Build(out _, out var hooks);
        hooks.ScriptIsReadable = false;

        var page = await OpenHooksAsync(provider);
        var dialogs = provider.GetRequiredService<FakeDialogService>();

        await page.EditCommand.ExecuteAsync(CancellationToken.None);

        dialogs.Shown.Should().BeEmpty("replacing a binary hook with text would destroy it");
        hooks.Applied.Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task Deleting_a_hook_goes_through_the_same_preview()
    {
        using var provider = Build(out _, out var hooks);
        var page = await OpenHooksAsync(provider);
        var dialogs = provider.GetRequiredService<FakeDialogService>();

        dialogs.Answer = true;
        await page.DeleteCommand.ExecuteAsync(CancellationToken.None);

        dialogs.ShownOfType<OperationReviewViewModel>().Should().ContainSingle();
        hooks.Deleted.Should().ContainSingle().Which.Should().Be("pre-commit");
        hooks.Applied.Should().ContainSingle();
    }

    [AvaloniaFact]
    public async Task A_redirected_hooks_directory_is_shown_rather_than_assumed()
    {
        using var provider = Build(out _, out var hooks);
        hooks.Directory = "/repo/tools/hooks";
        hooks.IsRedirected = true;

        var page = await OpenHooksAsync(provider);

        page.Directory.Should().Be("/repo/tools/hooks");
        page.IsRedirected.Should().BeTrue();
    }

    /// <summary>Fills a hook editor the way a user would.</summary>
    private static bool Fill(HookEditorViewModel editor)
    {
        editor.Script = "#!/bin/sh\nexit 0\n";
        editor.IsEnabled = true;
        return true;
    }

    private static async Task<RepositoryFilesViewModel> OpenFilesAsync(ServiceProvider provider)
    {
        provider.GetRequiredService<RepositoryContext>().Select("/src/alpha", "alpha");

        var page = provider.GetRequiredService<RepositoryFilesViewModel>();
        await page.ReloadAsync(CancellationToken.None);

        return page;
    }

    private static async Task<HooksViewModel> OpenHooksAsync(ServiceProvider provider)
    {
        provider.GetRequiredService<RepositoryContext>().Select("/src/alpha", "alpha");

        var page = provider.GetRequiredService<HooksViewModel>();
        await page.ReloadAsync(CancellationToken.None);

        page.SelectedRow.Should().NotBeNull();
        return page;
    }

    private static ServiceProvider Build(out StubFileEditor files, out StubHookEditor hooks)
    {
        var stubFiles = new StubFileEditor();
        var stubHooks = new StubHookEditor();

        files = stubFiles;
        hooks = stubHooks;

        return TestServices.Build(services =>
        {
            services.AddSingleton<IRepositoryFileEditor>(stubFiles);
            services.AddSingleton<IHookEditor>(stubHooks);
        });
    }
}

/// <summary>A file editor that records what it was asked to do, and writes nothing.</summary>
internal sealed class StubFileEditor : IRepositoryFileEditor
{
    /// <summary>Files this stub reports as not editable.</summary>
    public List<RepositoryFileKind> Unreadable { get; } = [];

    /// <summary>Writes that were planned.</summary>
    public List<(RepositoryFileKind Kind, string Text)> Planned { get; } = [];

    /// <summary>Plans that reached <see cref="ApplyAsync"/> and were not refused.</summary>
    public List<GitOperationPlan> Applied { get; } = [];

    public Task<RepositoryFile?> ReadAsync(
        string repositoryPath, RepositoryFileKind kind, CancellationToken cancellationToken) =>
        Task.FromResult(Unreadable.Contains(kind)
            ? null
            : new RepositoryFile(
                kind,
                "/src/alpha/" + RepositoryFileEditor.RelativePathOf(kind),
                "bin/\n",
                true,
                "\n",
                kind != RepositoryFileKind.Exclude));

    public Task<GitOperationPlan> PlanWriteAsync(
        string repositoryPath, RepositoryFileKind kind, string text, CancellationToken cancellationToken)
    {
        Planned.Add((kind, text));

        if (Unreadable.Contains(kind))
        {
            return Task.FromResult(new GitOperationPlan(
                RepositoryFileEditor.OperationId, GitConfigScope.Local, repositoryPath)
            {
                Blockers = [RepositoryFileBlockers.NotEditableText],
            });
        }

        return Task.FromResult(new GitOperationPlan(
            RepositoryFileEditor.OperationId, GitConfigScope.Local, repositoryPath)
        {
            Changes =
            [
                new PlannedChange(
                    RepositoryFileEditor.StepId, ChangeKind.FileWrite, "/src/alpha/.gitignore", "bin/\n", text),
            ],
        });
    }

    public Task<GitOperationResult> ApplyAsync(GitOperationPlan plan, CancellationToken cancellationToken)
    {
        if (plan.CanApply)
        {
            Applied.Add(plan);
        }

        return Task.FromResult(Result(plan));
    }

    /// <summary>Mirrors the real editor: an applied plan reports a step for each change.</summary>
    internal static GitOperationResult Result(GitOperationPlan plan) =>
        new(plan.OperationId, "snapshot")
        {
            Steps = [.. plan.Changes.Select(c => new GitVault.Core.Models.ActivationStepResult(
                c.StepId, GitVault.Core.Models.StepOutcome.Applied, c.Target))],
        };
}

/// <summary>A hook editor that records what it was asked to do, and runs nothing.</summary>
internal sealed class StubHookEditor : IHookEditor
{
    /// <summary>Directory reported to the page.</summary>
    public string Directory { get; set; } = "/src/alpha/.git/hooks";

    /// <summary>Whether the directory is reported as redirected.</summary>
    public bool IsRedirected { get; set; }

    /// <summary>Hooks reported to the page.</summary>
    public IReadOnlyList<GitHook> Hooks { get; set; } =
        [new GitHook("pre-commit", "/src/alpha/.git/hooks/pre-commit", true, true, true, 40)];

    /// <summary>False to report the script as something a text box cannot change.</summary>
    public bool ScriptIsReadable { get; set; } = true;

    /// <summary>Writes that were planned, as "name enabled" or "name disabled".</summary>
    public List<string> Written { get; } = [];

    /// <summary>Deletions that were planned.</summary>
    public List<string> Deleted { get; } = [];

    /// <summary>Plans that reached <see cref="ApplyAsync"/> and were not refused.</summary>
    public List<GitOperationPlan> Applied { get; } = [];

    public Task<HookDirectory> ListAsync(string repositoryPath, CancellationToken cancellationToken) =>
        Task.FromResult(new HookDirectory(Directory, IsRedirected, Hooks));

    public Task<string?> ReadAsync(string repositoryPath, string name, CancellationToken cancellationToken) =>
        Task.FromResult(ScriptIsReadable ? "#!/bin/sh\n" : null);

    public Task<GitOperationPlan> PlanWriteAsync(
        string repositoryPath, string name, string script, bool enabled, CancellationToken cancellationToken)
    {
        Written.Add(name + (enabled ? " enabled" : " disabled"));
        return Task.FromResult(Plan(repositoryPath, Directory + "/" + name, script));
    }

    public Task<GitOperationPlan> PlanDeleteAsync(
        string repositoryPath, string name, CancellationToken cancellationToken)
    {
        Deleted.Add(name);
        return Task.FromResult(Plan(repositoryPath, Directory + "/" + name, null));
    }

    public Task<GitOperationResult> ApplyAsync(GitOperationPlan plan, CancellationToken cancellationToken)
    {
        if (plan.CanApply)
        {
            Applied.Add(plan);
        }

        return Task.FromResult(StubFileEditor.Result(plan));
    }

    private static GitOperationPlan Plan(string repositoryPath, string target, string? after) =>
        new(HookEditor.OperationId, GitConfigScope.Local, repositoryPath)
        {
            Changes =
            [
                new PlannedChange(
                    HookEditor.StepId,
                    after is null ? ChangeKind.FileDelete : ChangeKind.FileWrite,
                    target,
                    "#!/bin/sh\n",
                    after),
            ],
        };
}
