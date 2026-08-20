using System.Collections;
using Avalonia;
using Avalonia.Controls;

namespace GitVault.App.Views;

/// <summary>
/// A label-and-value list, shared by the properties pane, the properties dialog and the detail
/// panels on the pages.
/// </summary>
/// <remarks>
/// A control rather than a copied <c>ItemsControl</c> template: the properties pane appears in
/// nine places, and a column width that drifts between them is exactly the kind of small
/// inconsistency that makes a dense interface feel unfinished.
/// </remarks>
internal sealed partial class PropertyList : UserControl
{
    /// <summary>Backing property for <see cref="Entries"/>.</summary>
    public static readonly StyledProperty<IEnumerable?> EntriesProperty =
        AvaloniaProperty.Register<PropertyList, IEnumerable?>(nameof(Entries));

    public PropertyList() => InitializeComponent();

    /// <summary>The properties to list.</summary>
    public IEnumerable? Entries
    {
        get => GetValue(EntriesProperty);
        set => SetValue(EntriesProperty, value);
    }
}
