using CommunityToolkit.Mvvm.ComponentModel;

namespace GitVault.App.Services;

/// <summary>
/// Which repository the per-repository pages are currently showing.
/// </summary>
/// <remarks>
/// The tree holds one node per repository but shares a single view-model instance per page type,
/// so the page has to be told which repository it is looking at. Keeping that in a small shared
/// service rather than on the shell means a page depends on the repository it shows and not on
/// the window that shows it.
///
/// Nothing here writes. Selecting a repository changes what is displayed and nothing else; every
/// write still goes through a planned, previewed operation on the page itself.
/// </remarks>
internal sealed partial class RepositoryContext : ObservableObject
{
    [ObservableProperty]
    private string? _currentPath;

    [ObservableProperty]
    private string _currentName = string.Empty;

    /// <summary>True when a repository is selected.</summary>
    public bool HasRepository => !string.IsNullOrEmpty(CurrentPath);

    /// <summary>Points the per-repository pages at a repository.</summary>
    /// <param name="path">Absolute path of the working tree.</param>
    /// <param name="name">Repository name, for display.</param>
    public void Select(string path, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        CurrentName = name;
        CurrentPath = path;
    }

    partial void OnCurrentPathChanged(string? value)
    {
        _ = value;
        OnPropertyChanged(nameof(HasRepository));
    }
}
