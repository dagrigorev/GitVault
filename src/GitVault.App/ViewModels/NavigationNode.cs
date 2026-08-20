using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using GitVault.Localization;

namespace GitVault.App.ViewModels;

/// <summary>
/// One node of the navigation tree: either the machine at the root, or a page under it.
/// </summary>
/// <remarks>
/// The tree mirrors how a management console presents a computer — the machine, and the
/// categories of thing it holds. The root is a heading rather than a destination, so selecting it
/// leaves the current page alone instead of blanking the workspace.
/// </remarks>
internal sealed class NavigationNode : ObservableObject
{
    private readonly Localizer _localizer;
    private readonly string _captionKey;

    /// <summary>Creates the root node.</summary>
    /// <param name="localizer">Bindable localizer.</param>
    /// <param name="captionKey">Resource key of the caption.</param>
    /// <param name="iconKey">Icon resource key.</param>
    internal NavigationNode(Localizer localizer, string captionKey, string iconKey)
    {
        ArgumentNullException.ThrowIfNull(localizer);

        _localizer = localizer;
        _captionKey = captionKey;
        IconKey = iconKey;
    }

    /// <summary>Creates a node for a page.</summary>
    /// <param name="localizer">Bindable localizer.</param>
    /// <param name="page">The page this node navigates to.</param>
    internal NavigationNode(Localizer localizer, PageViewModel page)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(page);

        _localizer = localizer;
        _captionKey = page.NavKey;
        IconKey = page.IconKey;
        Page = page;
    }

    /// <summary>The page this node shows, or null for the root.</summary>
    public PageViewModel? Page { get; }

    /// <summary>Icon resource key.</summary>
    public string IconKey { get; }

    /// <summary>Child nodes.</summary>
    public ObservableCollection<NavigationNode> Children { get; } = [];

    /// <summary>Localized caption.</summary>
    public string Caption => _localizer[_captionKey];

    /// <summary>The root is bold, the way a tree root is in a management console.</summary>
    public FontWeight CaptionWeight => Page is null ? FontWeight.Bold : FontWeight.Normal;

    /// <summary>True when this node is the machine rather than a page.</summary>
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
}
