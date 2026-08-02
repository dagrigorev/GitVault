using Avalonia.Controls;

namespace GitVault.App.Views;

/// <summary>
/// Shared view for every list page. Resolved through <see cref="ViewLocator"/>'s base-type
/// walk, so each list page reuses it until its own view lands in a later milestone.
/// </summary>
internal sealed partial class ListPageView : UserControl
{
    /// <summary>Creates the view and loads its XAML.</summary>
    public ListPageView() => InitializeComponent();
}
