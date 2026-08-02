using GitVault.Localization;

namespace GitVault.App.ViewModels;

/// <summary>
/// Base for the pages that render a list of discovered artifacts. Until the discovery engine
/// populates them they show a localized empty state.
/// </summary>
internal abstract class ListPageViewModel : PageViewModel
{
    protected ListPageViewModel(Localizer localizer)
        : base(localizer)
    {
    }

    /// <summary>Resource key of the empty-state caption.</summary>
    public abstract string EmptyKey { get; }

    /// <summary>Localized empty-state caption.</summary>
    public string EmptyCaption => L[EmptyKey];

    /// <summary>True while the page has nothing to show.</summary>
    public virtual bool IsEmpty => true;
}
