using System.Collections.ObjectModel;
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

    /// <summary>Resource key of the navigation-tree caption.</summary>
    public abstract string NavKey { get; }

    /// <summary>Resource key of the page heading.</summary>
    public abstract string TitleKey { get; }

    /// <summary>Resource key of the sentence under the heading.</summary>
    public abstract string SubtitleKey { get; }

    /// <summary>
    /// Key of the icon shown beside this page, resolved through <c>IconLookupConverter</c>.
    /// A name rather than a bitmap, so the view models stay free of drawing types.
    /// </summary>
    public abstract string IconKey { get; }

    /// <summary>Localized navigation-tree caption.</summary>
    public string NavCaption => L[NavKey];

    /// <summary>Localized page heading.</summary>
    public string Title => L[TitleKey];

    /// <summary>Localized sentence under the heading.</summary>
    public string Subtitle => L[SubtitleKey];

    /// <summary>
    /// Properties of whatever is selected on this page, shown in the shared properties pane.
    /// </summary>
    /// <remarks>
    /// A page rebuilds this list when its selection changes. Private key material, passwords and
    /// tokens never appear here — a page that has such a value puts a localized "hidden by
    /// design" marker in its place instead.
    /// </remarks>
    public ObservableCollection<PropertyEntry> Properties { get; } = [];

    /// <summary>True when the page has a selected item whose properties are worth showing.</summary>
    public bool HasSelection => Properties.Count > 0;

    /// <summary>Resource key of the title shown above the properties, when there is a selection.</summary>
    public virtual string PropertiesTitleKey => Keys.Common_Properties;

    /// <summary>True when Edit &gt; Copy applies to this page's selection.</summary>
    public virtual bool CanCopySelection => Properties.Count > 0;

    /// <summary>
    /// Re-asserts the page's current row once its grid has been attached.
    /// </summary>
    /// <remarks>
    /// A DataGrid clears its selection while it is being attached and pushes that null back
    /// through the binding. The view model repairs its own state immediately, but the control
    /// itself only shows the highlight if it is told again afterwards — which is what the shell
    /// does, on the dispatcher, once the page is on screen.
    /// </remarks>
    internal virtual void EnsureSelection()
    {
    }

    /// <summary>Called the first time the page becomes visible.</summary>
    /// <param name="cancellationToken">Cancels the activation work.</param>
    /// <returns>A task that completes when the page is ready.</returns>
    public virtual Task OnActivatedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Renders the current selection for the clipboard. The default joins the properties pane
    /// into tab-separated lines, which is what a classic list view puts on the clipboard.
    /// </summary>
    /// <returns>The text, or an empty string when nothing is selected.</returns>
    public virtual string BuildClipboardText() =>
        Properties.Count == 0 ? string.Empty : string.Join(Environment.NewLine, Properties);

    /// <summary>Replaces the properties pane contents and notifies the shell.</summary>
    /// <param name="entries">The new entries, or an empty sequence to clear the pane.</param>
    protected void SetProperties(IEnumerable<PropertyEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        Properties.Clear();
        foreach (var entry in entries)
        {
            Properties.Add(entry);
        }

        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanCopySelection));
    }

    /// <summary>Creates a property entry bound to this page's localizer.</summary>
    /// <param name="labelKey">Resource key of the label.</param>
    /// <param name="value">Already-formatted value.</param>
    /// <param name="style">How to present the value.</param>
    /// <returns>The entry.</returns>
    protected PropertyEntry Property(string labelKey, string? value, PropertyStyle style = PropertyStyle.Text) =>
        new(L, labelKey, value, style);

    /// <inheritdoc/>
    protected override void OnCultureChanged()
    {
        foreach (var entry in Properties)
        {
            entry.RefreshCaptions();
        }
    }
}
