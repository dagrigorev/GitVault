using Avalonia.Controls;
using Avalonia.Input;

namespace GitVault.App.Views;

/// <summary>Application shell window. Contains view wiring only.</summary>
internal sealed partial class MainWindow : Window
{
    /// <summary>Creates the window and loads its XAML.</summary>
    public MainWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Ctrl+F focuses the search box from anywhere in the window.
        if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            this.FindControl<TextBox>("SearchBox")?.Focus();
            e.Handled = true;
        }
    }
}
