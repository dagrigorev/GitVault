using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitVault.App.Services;
using GitVault.Core.Repository;
using GitVault.Localization;

namespace GitVault.App.ViewModels;

/// <summary>One tag, as the grid lists it.</summary>
internal sealed class TagRow(Localizer localizer, GitTag tag) : ObservableObject
{
    /// <summary>Bindable localizer.</summary>
    public Localizer L { get; } = localizer;

    /// <summary>The underlying tag.</summary>
    public GitTag Tag { get; } = tag;

    /// <summary>Tag name, shown verbatim.</summary>
    public string Name => Tag.Name;

    /// <summary>Abbreviated target commit.</summary>
    public string Target => Tag.TargetCommit.Length >= 8 ? Tag.TargetCommit[..8] : Tag.TargetCommit;

    /// <summary>Annotation subject, or an empty cell.</summary>
    public string Message => Tag.Message;

    /// <summary>Who created the annotated tag, or an empty cell.</summary>
    public string Tagger => Tag.Tagger;

    /// <summary>Localized kind: annotated or lightweight.</summary>
    public string Kind => L[Tag.IsAnnotated ? Keys.Tags_Kind_Annotated : Keys.Tags_Kind_Lightweight];

    /// <summary>Localized signature state.</summary>
    public string Signature => L[Tag.IsSigned ? Keys.Tags_Signed : Keys.Tags_Unsigned];

    /// <summary>Re-reads the localized members.</summary>
    internal void RefreshCaptions() =>
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(string.Empty));
}

/// <summary>
/// The tags of one repository.
/// </summary>
/// <remarks>
/// A tag GitVault creates is never signed. Signing would need the user's key and passphrase, and
/// this application does not hold either — the parsers read public halves and delegate anything
/// requiring a passphrase to git's own tooling. Deleting a signed tag is allowed and warned about,
/// because the signature is the one thing a ref backup cannot recreate.
/// </remarks>
internal sealed partial class TagsViewModel : RepositoryObjectPageViewModel
{
    [ObservableProperty]
    private TagRow? _selectedRow;

    public TagsViewModel(
        Localizer localizer,
        IGitObjectEditor editor,
        IRepositoryInspector inspector,
        IDialogService dialogs,
        StatusService status,
        RepositoryContext repository)
        : base(localizer, editor, inspector, dialogs, status, repository)
    {
    }

    /// <inheritdoc/>
    public override string NavKey => Keys.Nav_Tags;

    /// <inheritdoc/>
    public override string TitleKey => Keys.Tags_Title;

    /// <inheritdoc/>
    public override string SubtitleKey => Keys.Tags_Subtitle;

    /// <inheritdoc/>
    public override string IconKey => "IconSnapshots";

    /// <inheritdoc/>
    public override string EmptyKey => Keys.Tags_Empty;

    /// <inheritdoc/>
    public override bool IsEmpty => Rows.Count == 0;

    /// <summary>The tags, newest first.</summary>
    public ObservableCollection<TagRow> Rows { get; } = [];

    /// <summary>True when a tag is selected, so the editing verbs apply.</summary>
    public bool HasSelectedTag => SelectedRow is not null;

    /// <summary>Localized note that GitVault does not sign the tags it creates.</summary>
    public string SigningNoteCaption => L[Keys.Tags_SigningNote];

    /// <inheritdoc/>
    internal override async Task ReloadAsync(CancellationToken cancellationToken)
    {
        var previous = SelectedRow?.Name;

        Rows.Clear();

        if (Repository.CurrentPath is { Length: > 0 } path)
        {
            foreach (var tag in await Inspector.ListTagsAsync(path, cancellationToken).ConfigureAwait(true))
            {
                Rows.Add(new TagRow(L, tag));
            }
        }

        SelectedRow = Rows.FirstOrDefault(r => r.Name == previous) ?? Rows.FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Creates a tag after previewing the change.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the dialogs close.</returns>
    [RelayCommand]
    private async Task CreateAsync(CancellationToken cancellationToken)
    {
        if (Repository.CurrentPath is not { Length: > 0 } path)
        {
            return;
        }

        var dialog = new TagEditorViewModel(L);
        if (!await ShowAsync(dialog).ConfigureAwait(true))
        {
            return;
        }

        var plan = await Editor
            .PlanCreateTagAsync(
                path,
                dialog.Name.Trim(),
                dialog.Target.Trim(),
                dialog.IsAnnotated ? dialog.Message : null,
                cancellationToken)
            .ConfigureAwait(true);

        await ReviewAndApplyAsync(plan, Keys.Status_TagCreated, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Deletes the selected tag after previewing the change.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the dialog closes.</returns>
    [RelayCommand]
    private async Task DeleteAsync(CancellationToken cancellationToken)
    {
        if (Repository.CurrentPath is not { Length: > 0 } path || SelectedRow is not { } row)
        {
            return;
        }

        var plan = await Editor.PlanDeleteTagAsync(path, row.Name, cancellationToken).ConfigureAwait(true);
        await ReviewAndApplyAsync(plan, Keys.Status_TagDeleted, cancellationToken).ConfigureAwait(true);
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

    partial void OnSelectedRowChanged(TagRow? value)
    {
        if (value is null && Rows.Count > 0)
        {
            SelectedRow = Rows[0];
            return;
        }

        OnPropertyChanged(nameof(HasSelectedTag));
        RebuildProperties();
    }

    private void RebuildProperties()
    {
        if (SelectedRow is not { } row)
        {
            SetProperties([]);
            return;
        }

        var entries = new List<PropertyEntry>
        {
            Property(Keys.Tags_Column_Name, row.Name),
            Property(Keys.Tags_Column_Kind, row.Kind, PropertyStyle.Badge),
            Property(Keys.Tags_Column_Target, row.Tag.TargetCommit, PropertyStyle.Mono),
            Property(
                Keys.Tags_Column_Signature,
                row.Signature,
                row.Tag.IsSigned ? PropertyStyle.BadgeOk : PropertyStyle.Badge),
        };

        if (row.Message.Length > 0)
        {
            entries.Add(Property(Keys.Tags_Column_Message, row.Message));
        }

        if (row.Tagger.Length > 0)
        {
            entries.Add(Property(Keys.Tags_Column_Tagger, row.Tagger));
        }

        SetProperties(entries);
    }
}
