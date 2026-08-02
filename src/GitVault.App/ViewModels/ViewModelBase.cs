using CommunityToolkit.Mvvm.ComponentModel;
using GitVault.Localization;

namespace GitVault.App.ViewModels;

/// <summary>
/// Base for every view model. Holds the bindable <see cref="Localizer"/> and re-raises a
/// blanket property change when the culture switches, so computed captions refresh live.
/// </summary>
internal abstract class ViewModelBase : ObservableObject, IDisposable
{
    private bool _disposed;

    protected ViewModelBase(Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        L = localizer;
        L.Service.CultureChanged += OnCultureChanged;
    }

    /// <summary>Bindable localizer. XAML binds captions through <c>L[Key]</c>.</summary>
    public Localizer L { get; }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Called after the UI culture changed. Override to refresh cached text.</summary>
    protected virtual void OnCultureChanged()
    {
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            L.Service.CultureChanged -= OnCultureChanged;
        }

        _disposed = true;
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        // An empty name means "every property changed" for both WPF and Avalonia bindings.
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(string.Empty));
        OnCultureChanged();
    }
}

/// <summary>Base for the navigable pages hosted by the main window.</summary>
internal abstract class PageViewModel : ViewModelBase
{
    protected PageViewModel(Localizer localizer)
        : base(localizer)
    {
    }

    /// <summary>Resource key of the navigation-rail caption.</summary>
    public abstract string NavKey { get; }

    /// <summary>Resource key of the page heading.</summary>
    public abstract string TitleKey { get; }

    /// <summary>
    /// Key of the icon shown beside this page, resolved through <c>IconLookupConverter</c>.
    /// A name rather than a geometry, so the view models stay free of drawing types.
    /// </summary>
    public abstract string IconKey { get; }

    /// <summary>Localized navigation-rail caption.</summary>
    public string NavCaption => L[NavKey];

    /// <summary>Localized page heading.</summary>
    public string Title => L[TitleKey];

    /// <summary>Called the first time the page becomes visible.</summary>
    /// <param name="cancellationToken">Cancels the activation work.</param>
    /// <returns>A task that completes when the page is ready.</returns>
    public virtual Task OnActivatedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
