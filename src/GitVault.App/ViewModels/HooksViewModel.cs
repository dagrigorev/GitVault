using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitVault.App.Services;
using GitVault.Core.Repository;
using GitVault.Localization;

namespace GitVault.App.ViewModels;

/// <summary>One hook, as the grid lists it.</summary>
internal sealed class HookRow(Localizer localizer, GitHook hook) : ObservableObject
{
    /// <summary>Bindable localizer.</summary>
    public Localizer L { get; } = localizer;

    /// <summary>The underlying hook.</summary>
    public GitHook Hook { get; } = hook;

    /// <summary>Hook name, shown verbatim.</summary>
    public string Name => Hook.Name;

    /// <summary>Localized state.</summary>
    public string State => L[Hook switch
    {
        { Exists: false } => Keys.Hooks_State_Absent,
        { IsInertlyDisabled: true } => Keys.Hooks_State_Inert,
        { IsEnabled: true } => Keys.Hooks_State_Enabled,
        _ => Keys.Hooks_State_Disabled,
    }];

    /// <summary>Size in bytes, or an empty cell when the hook is absent.</summary>
    public string Size => Hook.Exists
        ? L.Format(Keys.Hooks_SizeFormat, Hook.SizeBytes)
        : string.Empty;

    /// <summary>Re-reads the localized members.</summary>
    internal void RefreshCaptions() =>
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(string.Empty));
}

/// <summary>
/// The hooks page.
/// </summary>
/// <remarks>
/// A page of its own, and the only page in the application whose warning is unconditional. A hook
/// is a program git runs by itself with the user's privileges; every other editor here changes
/// what git reads, this one changes what it executes.
///
/// The page shows every hook git knows about, including the ones that are not there, so that
/// creating one is as visible as editing one. It shows the directory git actually uses, which is
/// not always <c>.git/hooks</c>. And it names the state git will not tell anyone about: a hook
/// that is in place, enabled, and skipped anyway because the file is not executable.
///
/// Nothing here runs a hook — not to check its syntax, not to preview what it does. There is no
/// safe way to offer that, so it is not offered.
/// </remarks>
internal sealed partial class HooksViewModel : ListPageViewModel
{
    private readonly IHookEditor _hooks;
    private readonly IDialogService _dialogs;
    private readonly StatusService _status;
    private readonly RepositoryContext _repository;

    [ObservableProperty]
    private HookRow? _selectedRow;

    [ObservableProperty]
    private string _directory = string.Empty;

    [ObservableProperty]
    private bool _isRedirected;

    public HooksViewModel(
        Localizer localizer,
        IHookEditor hooks,
        IDialogService dialogs,
        StatusService status,
        RepositoryContext repository)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(hooks);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(repository);

        _hooks = hooks;
        _dialogs = dialogs;
        _status = status;
        _repository = repository;

        _repository.PropertyChanged += OnRepositoryChanged;
    }

    /// <inheritdoc/>
    public override string NavKey => Keys.Nav_Hooks;

    /// <inheritdoc/>
    public override string TitleKey => Keys.Hooks_Title;

    /// <inheritdoc/>
    public override string SubtitleKey => Keys.Hooks_Subtitle;

    /// <inheritdoc/>
    public override string IconKey => "IconLogs";

    /// <inheritdoc/>
    public override string EmptyKey => Keys.Hooks_Empty;

    /// <inheritdoc/>
    public override bool IsEmpty => Rows.Count == 0;

    /// <summary>Every hook git knows about, present or not.</summary>
    public ObservableCollection<HookRow> Rows { get; } = [];

    /// <summary>True when a repository is selected.</summary>
    public bool HasRepository => _repository.HasRepository;

    /// <summary>Localized prompt shown when no repository is selected.</summary>
    public string NoRepositoryCaption => L[Keys.Hooks_Empty];

    /// <summary>Localized statement of what a hook is and what writing one means.</summary>
    public string DangerCaption => L[Keys.Hooks_Danger];

    /// <summary>Localized note that hooks have been pointed somewhere else.</summary>
    public string RedirectedCaption => L[Keys.Hooks_Redirected];

    /// <summary>Localized explanation of a hook git will skip anyway.</summary>
    public string InertCaption => L[Keys.Hooks_InertNote];

    /// <summary>True when the selected hook is in place, enabled, and skipped anyway.</summary>
    public bool SelectionIsInert => SelectedRow?.Hook.IsInertlyDisabled == true;

    /// <summary>True when a hook is selected.</summary>
    public bool HasSelectedHook => SelectedRow is not null;

    /// <summary>True when the selected hook exists, so it can be deleted.</summary>
    public bool CanDelete => SelectedRow?.Hook.Exists == true;

    /// <inheritdoc/>
    public override Task OnActivatedAsync(CancellationToken cancellationToken) => ReloadAsync(cancellationToken);

    /// <summary>Re-reads the hooks directory.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>A task that completes once the grid is rebuilt.</returns>
    internal async Task ReloadAsync(CancellationToken cancellationToken)
    {
        if (_repository.CurrentPath is not { Length: > 0 } path)
        {
            Rows.Clear();
            Directory = string.Empty;
            IsRedirected = false;
            Notify();
            return;
        }

        var directory = await _hooks.ListAsync(path, cancellationToken).ConfigureAwait(true);
        var previous = SelectedRow?.Name;

        Rows.Clear();
        foreach (var hook in directory.Hooks)
        {
            Rows.Add(new HookRow(L, hook));
        }

        Directory = directory.Directory;
        IsRedirected = directory.IsRedirected;

        SelectedRow = Rows.FirstOrDefault(r => r.Name == previous) ?? Rows.FirstOrDefault();
        Notify();
    }

    /// <summary>Edits the selected hook, after showing what a hook is.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the dialogs close.</returns>
    [RelayCommand]
    private async Task EditAsync(CancellationToken cancellationToken)
    {
        if (_repository.CurrentPath is not { Length: > 0 } path || SelectedRow is not { } row)
        {
            return;
        }

        var script = row.Hook.Exists
            ? await _hooks.ReadAsync(path, row.Name, cancellationToken).ConfigureAwait(true)
            : string.Empty;

        if (script is null)
        {
            // A compiled hook is a real thing; it is simply not something a text box can change
            // without destroying it.
            _status.Report(StatusKind.Error, HookBlockers.NotEditableText);
            return;
        }

        var dialog = new HookEditorViewModel(L, row.Hook, script);
        if (!await _dialogs.ShowAsync(dialog).ConfigureAwait(true))
        {
            return;
        }

        var plan = await _hooks
            .PlanWriteAsync(path, row.Name, dialog.Script, dialog.IsEnabled, cancellationToken)
            .ConfigureAwait(true);

        await ReviewAndApplyAsync(plan, Keys.Status_HookSaved, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Deletes the selected hook, after previewing the change.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the dialog closes.</returns>
    [RelayCommand]
    private async Task DeleteAsync(CancellationToken cancellationToken)
    {
        if (_repository.CurrentPath is not { Length: > 0 } path || SelectedRow is not { } row)
        {
            return;
        }

        var plan = await _hooks.PlanDeleteAsync(path, row.Name, cancellationToken).ConfigureAwait(true);
        await ReviewAndApplyAsync(plan, Keys.Status_HookDeleted, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Re-reads the hooks directory.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>A task that completes once the grid is rebuilt.</returns>
    [RelayCommand]
    private Task RefreshAsync(CancellationToken cancellationToken) => ReloadAsync(cancellationToken);

    private async Task ReviewAndApplyAsync(
        GitOperationPlan plan,
        string successKey,
        CancellationToken cancellationToken)
    {
        var review = new OperationReviewViewModel(L, plan);

        if (!await _dialogs.ShowAsync(review).ConfigureAwait(true))
        {
            _status.Report(StatusKind.Ready, Keys.Status_PlanNotApplied);
            return;
        }

        var result = await _hooks.ApplyAsync(plan, cancellationToken).ConfigureAwait(true);

        if (result.Succeeded)
        {
            _status.Report(StatusKind.Done, successKey);
        }
        else
        {
            _status.ReportText(
                StatusKind.Error,
                string.Join(
                    L[Keys.Common_ListSeparator],
                    result.Steps
                        .Where(s => s.Outcome == GitVault.Core.Models.StepOutcome.Failed)
                        .Select(s => s.Detail)));
        }

        await ReloadAsync(cancellationToken).ConfigureAwait(true);
    }

    partial void OnSelectedRowChanged(HookRow? value)
    {
        if (value is null && Rows.Count > 0)
        {
            SelectedRow = Rows[0];
            return;
        }

        OnPropertyChanged(nameof(SelectionIsInert));
        OnPropertyChanged(nameof(HasSelectedHook));
        OnPropertyChanged(nameof(CanDelete));

        RebuildProperties();
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasRepository));
        OnPropertyChanged(nameof(CanDelete));
    }

    private void RebuildProperties()
    {
        if (SelectedRow is not { } row)
        {
            SetProperties([]);
            return;
        }

        SetProperties(
        [
            Property(Keys.Hooks_Column_Name, row.Name),
            Property(Keys.Hooks_Column_State, row.State, row.Hook switch
            {
                { IsInertlyDisabled: true } => PropertyStyle.BadgeWarn,
                { IsEnabled: true } => PropertyStyle.BadgeOk,
                _ => PropertyStyle.Badge,
            }),
            Property(Keys.Keys_Column_Path, row.Hook.Path, PropertyStyle.Mono),
        ]);
    }

    private void OnRepositoryChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RepositoryContext.CurrentPath))
        {
            _ = ReloadAsync(CancellationToken.None);
        }
    }

    /// <inheritdoc/>
    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();

        foreach (var row in Rows)
        {
            row.RefreshCaptions();
        }

        RebuildProperties();
    }

    /// <inheritdoc/>
    internal override void EnsureSelection()
    {
        if (Rows.Count > 0)
        {
            var current = SelectedRow;
            SelectedRow = null;
            SelectedRow = current ?? Rows[0];
        }
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _repository.PropertyChanged -= OnRepositoryChanged;
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// Editing one hook script.
/// </summary>
/// <remarks>
/// The warning is part of the dialog rather than something shown once and dismissed, because the
/// thing it warns about is true every time: what gets written here is a program git will run by
/// itself.
///
/// Whether git runs it is a checkbox rather than a consequence of the file's permissions, because
/// the permission bit is not reliable across platforms and the suffix git itself uses is.
/// </remarks>
internal sealed partial class HookEditorViewModel : DialogViewModel
{
    private readonly GitHook _hook;
    private readonly string _original;

    [ObservableProperty]
    private string _script;

    [ObservableProperty]
    private bool _isEnabled;

    internal HookEditorViewModel(Localizer localizer, GitHook hook, string script)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(hook);
        ArgumentNullException.ThrowIfNull(script);

        _hook = hook;
        _original = script;
        _script = script.Length > 0 ? script : DefaultScript;
        _isEnabled = !hook.Exists || hook.IsEnabled;
    }

    /// <summary>
    /// What a new hook starts as.
    /// </summary>
    /// <remarks>
    /// A shebang and nothing else. Anything more would be GitVault putting behaviour into a file
    /// git executes, which is the user's decision rather than a convenience to be helpful about.
    /// </remarks>
    private const string DefaultScript = "#!/bin/sh\n";

    /// <inheritdoc/>
    public override string TitleKey => Keys.Hooks_Editor_Title;

    /// <inheritdoc/>
    public override string ConfirmKey => Keys.Common_Ok;

    /// <summary>Hook name, shown verbatim.</summary>
    public string Name => _hook.Name;

    /// <summary>Where the file will be written.</summary>
    public string Path => _hook.Path;

    /// <summary>Localized statement of what a hook is.</summary>
    public string DangerCaption => L[Keys.Hooks_Danger];

    /// <summary>Localized note about what unchecking the box does.</summary>
    public string DisabledNoteCaption => L[Keys.Hooks_Editor_DisabledNote];

    /// <inheritdoc/>
    public override bool CanConfirm =>
        Script.Trim().Length > 0
        && (!string.Equals(Script, _original, StringComparison.Ordinal)
            || IsEnabled != (_hook.Exists && _hook.IsEnabled));

    /// <inheritdoc/>
    public override double DialogWidth => 760;

    /// <inheritdoc/>
    public override double DialogHeight => 620;

    partial void OnScriptChanged(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(CanConfirm));
    }

    partial void OnIsEnabledChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(CanConfirm));
    }
}
