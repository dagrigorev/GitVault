using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using GitVault.Localization;

namespace GitVault.App.ViewModels;

/// <summary>
/// One node of the navigation tree: the machine at the root, a page under it, a repository under
/// the repositories page, or one of that repository's own pages.
/// </summary>
/// <remarks>
/// The shape mirrors a management console — the computer, the categories of thing it holds, and
/// then the individual objects with their own pages beneath them. A repository's pages are the
/// same view-model instances as everywhere else; what distinguishes them is
/// <see cref="RepositoryPath"/>, which the shell puts into the repository context before the page
/// is shown. One instance per page type rather than one per repository keeps the tree cheap for a
/// machine holding a few hundred repositories.
///
/// A node whose caption is a repository name holds that name literally. Repository names are data,
/// not interface text, and translating them would be a bug.
/// </remarks>
internal sealed class NavigationNode : ObservableObject
{
    private readonly Localizer _localizer;
    private readonly string? _captionKey;
    private readonly string? _literalCaption;

    private NavigationNode(
        Localizer localizer,
        string? captionKey,
        string? literalCaption,
        string iconKey,
        PageViewModel? page,
        string? repositoryPath)
    {
        ArgumentNullException.ThrowIfNull(localizer);

        _localizer = localizer;
        _captionKey = captionKey;
        _literalCaption = literalCaption;
        IconKey = iconKey;
        Page = page;
        RepositoryPath = repositoryPath;
    }

    /// <summary>Creates the root node.</summary>
    /// <param name="localizer">Bindable localizer.</param>
    /// <param name="captionKey">Resource key of the caption.</param>
    /// <param name="iconKey">Icon resource key.</param>
    internal NavigationNode(Localizer localizer, string captionKey, string iconKey)
        : this(localizer, captionKey, null, iconKey, null, null)
    {
    }

    /// <summary>Creates a node for a page.</summary>
    /// <param name="localizer">Bindable localizer.</param>
    /// <param name="page">The page this node navigates to.</param>
    internal NavigationNode(Localizer localizer, PageViewModel page)
        : this(localizer, Key(page), null, Icon(page), page, null)
    {
    }

    /// <summary>Creates a node standing for one repository.</summary>
    /// <param name="localizer">Bindable localizer.</param>
    /// <param name="name">Repository name, shown verbatim.</param>
    /// <param name="path">Absolute path of the working tree.</param>
    /// <returns>The node.</returns>
    internal static NavigationNode ForRepository(Localizer localizer, string name, string path) =>
        new(localizer, null, name, "IconRepositories", null, path);

    /// <summary>Creates a node for a page shown in the context of one repository.</summary>
    /// <param name="localizer">Bindable localizer.</param>
    /// <param name="page">The page this node navigates to.</param>
    /// <param name="repositoryPath">Repository the page should show.</param>
    /// <returns>The node.</returns>
    internal static NavigationNode ForRepositoryPage(
        Localizer localizer,
        PageViewModel page,
        string repositoryPath)
    {
        ArgumentNullException.ThrowIfNull(page);
        return new NavigationNode(localizer, Key(page), null, Icon(page), page, repositoryPath);
    }

    /// <summary>The page this node shows, or null for the root and for a repository node.</summary>
    public PageViewModel? Page { get; }

    /// <summary>Icon resource key.</summary>
    public string IconKey { get; }

    /// <summary>Repository this node belongs to, when it belongs to one.</summary>
    public string? RepositoryPath { get; }

    /// <summary>Child nodes.</summary>
    public ObservableCollection<NavigationNode> Children { get; } = [];

    /// <summary>Caption: localized for a page, verbatim for a repository name.</summary>
    public string Caption => _literalCaption ?? _localizer[_captionKey!];

    /// <summary>The root and the repository nodes are bold, the way a tree groups things.</summary>
    public FontWeight CaptionWeight => Page is null ? FontWeight.Bold : FontWeight.Normal;

    /// <summary>True when this node is a heading rather than a destination.</summary>
    public bool IsRoot => Page is null;

    /// <summary>Re-reads the caption. Called when the culture changes.</summary>
    internal void RefreshCaptions()
    {
        OnPropertyChanged(nameof(Caption));
        foreach (var child in Children)
        {
            child.RefreshCaptions();
        }
    }

    private static string Key(PageViewModel page) => page.NavKey;

    private static string Icon(PageViewModel page) => page.IconKey;
}
