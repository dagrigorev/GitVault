using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitVault.App.Services;
using GitVault.Core.Abstractions;
using GitVault.Core.Models;
using GitVault.Core.Repository;
using GitVault.Localization;

namespace GitVault.App.ViewModels;

/// <summary>One configuration entry, as the grid lists it.</summary>
internal sealed class ConfigRow(Localizer localizer, GitConfigValue value) : ObservableObject
{
    /// <summary>Bindable localizer.</summary>
    public Localizer L { get; } = localizer;

    /// <summary>The underlying value.</summary>
    public GitConfigValue Value { get; } = value;

    /// <summary>Configuration key, shown verbatim.</summary>
    public string Key => Value.Key;

    /// <summary>The value, shown verbatim.</summary>
    public string Setting => Value.Value;

    /// <summary>Localized scope name.</summary>
    public string Scope => L[DisplayNames.ScopeKey(Value.Scope)];

    /// <summary>Where git says the value came from.</summary>
    public string Origin => Value.Origin;

    /// <summary>True when this entry can be edited here, i.e. it is not from an include.</summary>
    public bool IsEditable => Value.Scope is not (GitConfigScope.Command or GitConfigScope.Unknown);

    /// <summary>Re-reads the localized members.</summary>
    internal void RefreshCaptions() =>
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(string.Empty));
}

/// <summary>
/// The configuration of one repository, at every scope that contributes to it.
/// </summary>
/// <remarks>
/// Every scope is listed together because that is the question people actually have — not "what
/// is in this file" but "why is this value what it is". The scope column answers it, and editing
/// is always explicit about the scope being written rather than the scope a value happens to have
/// come from.
/// </remarks>
internal sealed partial class RepositoryConfigViewModel : ListPageViewModel
{
    private readonly IGitConfigService _config;
    private readonly IConfigEditor _editor;
    private readonly IDialogService _dialogs;
    private readonly StatusService _status;
    private readonly RepositoryContext _repository;

    [ObservableProperty]
    private ConfigRow? _selectedRow;

    [ObservableProperty]
    private ScopeFilterOption? _selectedScopeFilter;

    [ObservableProperty]
    private bool _isLoading;

    public RepositoryConfigViewModel(
        Localizer localizer,
        IGitConfigService config,
        IConfigEditor editor,
        IDialogService dialogs,
        StatusService status,
        RepositoryContext repository)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(repository);

        _config = config;
        _editor = editor;
        _dialogs = dialogs;
        _status = status;
        _repository = repository;

        ScopeFilters =
        [
            new ScopeFilterOption(localizer, null, Keys.Config_Scope_All),
            new ScopeFilterOption(localizer, GitConfigScope.Local, Keys.Scope_Local),
            new ScopeFilterOption(localizer, GitConfigScope.Global, Keys.Scope_Global),
            new ScopeFilterOption(localizer, GitConfigScope.System, Keys.Scope_System),
        ];

        _selectedScopeFilter = ScopeFilters[0];
        _repository.PropertyChanged += OnRepositoryChanged;
    }

    /// <inheritdoc/>
    public override string NavKey => Keys.Nav_Configuration;

    /// <inheritdoc/>
    public override string TitleKey => Keys.Config_Title;

    /// <inheritdoc/>
    public override string SubtitleKey => Keys.Config_Subtitle;

    /// <inheritdoc/>
    public override string IconKey => "IconOptions";

    /// <inheritdoc/>
    public override string EmptyKey => Keys.Config_Empty;

    /// <inheritdoc/>
    public override bool IsEmpty => Rows.Count == 0;

    /// <summary>Every configuration value contributing to this repository.</summary>
    public ObservableCollection<ConfigRow> Rows { get; } = [];

    /// <summary>Scope filters offered above the grid.</summary>
    public ObservableCollection<ScopeFilterOption> ScopeFilters { get; }

    /// <summary>Name of the repository being shown.</summary>
    public string RepositoryName => _repository.CurrentName;

    /// <summary>Path of the repository being shown.</summary>
    public string RepositoryPath => _repository.CurrentPath ?? string.Empty;

    /// <summary>True when a repository is selected.</summary>
    public bool HasRepository => _repository.HasRepository;

    /// <summary>True when the selected row can be edited or removed here.</summary>
    public bool CanEditSelection => SelectedRow?.IsEditable == true;

    /// <summary>Localized reminder that editing is previewed like everything else.</summary>
    public string PreviewNoteCaption => L[Keys.Config_PreviewNote];

    /// <inheritdoc/>
    public override Task OnActivatedAsync(CancellationToken cancellationToken) => ReloadAsync(cancellationToken);

    /// <summary>Re-reads the configuration.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>A task that completes once the grid is rebuilt.</returns>
    internal async Task ReloadAsync(CancellationToken cancellationToken)
    {
        if (!_repository.HasRepository)
        {
            Rows.Clear();
            OnPropertyChanged(nameof(IsEmpty));
            return;
        }

        IsLoading = true;
        try
        {
            var values = await _config
                .ListAsync(_repository.CurrentPath, cancellationToken)
                .ConfigureAwait(true);

            var previous = SelectedRow?.Key;

            Rows.Clear();
            foreach (var value in Filter(values))
            {
                Rows.Add(new ConfigRow(L, value));
            }

            SelectedRow = Rows.FirstOrDefault(r => r.Key == previous) ?? Rows.FirstOrDefault();
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(RepositoryName));
            OnPropertyChanged(nameof(RepositoryPath));
            OnPropertyChanged(nameof(HasRepository));
        }
    }

    /// <summary>Adds a configuration key after previewing the change.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the dialog closes.</returns>
    [RelayCommand]
    private Task AddAsync(CancellationToken cancellationToken) => EditAsync(null, cancellationToken);

    /// <summary>Edits the selected key after previewing the change.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the dialog closes.</returns>
    [RelayCommand]
    private Task EditSelectedAsync(CancellationToken cancellationToken) => EditAsync(SelectedRow, cancellationToken);

    /// <summary>Removes the selected key after previewing the change.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the dialog closes.</returns>
    [RelayCommand]
    private async Task RemoveSelectedAsync(CancellationToken cancellationToken)
    {
        if (SelectedRow is not { IsEditable: true } row || !_repository.HasRepository)
        {
            return;
        }

        var plan = await _editor
            .PlanUnsetAsync(row.Key, row.Value.Scope, _repository.CurrentPath, cancellationToken)
            .ConfigureAwait(true);

        await ReviewAndApplyAsync(plan, cancellationToken).ConfigureAwait(true);
    }

    private async Task EditAsync(ConfigRow? row, CancellationToken cancellationToken)
    {
        if (!_repository.HasRepository)
        {
            return;
        }

        var dialog = new ConfigEntryEditorViewModel(L, row?.Value, DefaultScope());

        if (!await _dialogs.ShowAsync(dialog).ConfigureAwait(true))
        {
            return;
        }

        var plan = await _editor
            .PlanSetAsync(dialog.Key.Trim(), dialog.Value, dialog.Scope, _repository.CurrentPath, cancellationToken)
            .ConfigureAwait(true);

        await ReviewAndApplyAsync(plan, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Shows the plan and applies it only if the user confirms.
    /// </summary>
    /// <remarks>
    /// The same gate as profile activation, for the same reason: what runs is the object the user
    /// was shown, and closing the preview leaves the configuration untouched.
    /// </remarks>
    private async Task ReviewAndApplyAsync(GitOperationPlan plan, CancellationToken cancellationToken)
    {
        var review = new OperationReviewViewModel(L, plan);

        if (!await _dialogs.ShowAsync(review).ConfigureAwait(true))
        {
            _status.Report(StatusKind.Ready, Keys.Status_PlanNotApplied);
            return;
        }

        var result = await _editor.ApplyAsync(plan, cancellationToken).ConfigureAwait(true);

        _status.Report(
            result.Succeeded ? StatusKind.Done : StatusKind.Error,
            result.Succeeded ? Keys.Status_ConfigSaved : Keys.Status_ConfigFailed);

        await ReloadAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>The scope a new key is offered at: the one being filtered, or local.</summary>
    private GitConfigScope DefaultScope() =>
        SelectedScopeFilter?.Scope ?? SelectedRow?.Value.Scope ?? GitConfigScope.Local;

    private IEnumerable<GitConfigValue> Filter(IReadOnlyList<GitConfigValue> values)
    {
        var scope = SelectedScopeFilter?.Scope;

        return values
            .Where(v => scope is null || v.Scope == scope)
            .OrderBy(v => v.Scope)
            .ThenBy(v => v.Key, StringComparer.OrdinalIgnoreCase);
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

        foreach (var filter in ScopeFilters)
        {
            filter.RefreshCaptions();
        }

        foreach (var row in Rows)
        {
            row.RefreshCaptions();
        }

        RebuildProperties();
    }

    /// <inheritdoc/>
    internal override void EnsureSelection()
    {
        if (Rows.Count == 0)
        {
            return;
        }

        var current = SelectedRow;
        SelectedRow = null;
        SelectedRow = current ?? Rows[0];
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

    partial void OnSelectedRowChanged(ConfigRow? value)
    {
        if (value is null && Rows.Count > 0)
        {
            SelectedRow = Rows[0];
            return;
        }

        _ = value;
        OnPropertyChanged(nameof(CanEditSelection));
        RebuildProperties();
    }

    partial void OnSelectedScopeFilterChanged(ScopeFilterOption? value)
    {
        _ = value;
        _ = ReloadAsync(CancellationToken.None);
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
            Property(Keys.Config_Column_Key, row.Key, PropertyStyle.Mono),
            Property(Keys.Config_Column_Value, row.Setting, PropertyStyle.Mono),
            Property(Keys.Config_Column_Scope, row.Scope, PropertyStyle.Badge),
            Property(Keys.Config_Column_Origin, row.Origin, PropertyStyle.Mono),
        ]);
    }
}

/// <summary>A scope the configuration grid can be filtered to, or "all".</summary>
internal sealed class ScopeFilterOption(Localizer localizer, GitConfigScope? scope, string labelKey)
    : ObservableObject
{
    /// <summary>Bindable localizer.</summary>
    public Localizer L { get; } = localizer;

    /// <summary>The scope, or null for every scope.</summary>
    public GitConfigScope? Scope { get; } = scope;

    /// <summary>Localized label.</summary>
    public string Label => L[labelKey];

    /// <inheritdoc/>
    public override string ToString() => Label;

    /// <summary>Re-reads the label.</summary>
    internal void RefreshCaptions() => OnPropertyChanged(nameof(Label));
}
