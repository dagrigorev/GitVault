using Avalonia.Controls;
using Avalonia.Input;
using GitVault.App.ViewModels;

namespace GitVault.App.Views;

/// <summary>Application shell window. Contains view wiring only.</summary>
internal sealed partial class MainWindow : Window
{
    private MainWindowViewModel? _shell;

    /// <summary>Creates the window and loads its XAML.</summary>
    public MainWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_shell is not null)
        {
            _shell.ExitRequested -= OnExitRequested;
        }

        _shell = DataContext as MainWindowViewModel;

        if (_shell is not null)
        {
            _shell.ExitRequested += OnExitRequested;
        }
    }

    private void OnExitRequested(object? sender, EventArgs e) => Close();

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel shell)
        {
            return;
        }

        // The classic shortcuts. A menu item's InputGesture in Avalonia only draws the text
        // beside the caption, so the keys themselves are bound here.
        switch (e.Key)
        {
            case Key.F when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                this.FindControl<TextBox>("SearchBox")?.Focus();
                e.Handled = true;
                break;

            case Key.F5:
                if (shell.RescanCommand.CanExecute(null))
                {
                    shell.RescanCommand.Execute(null);
                }

                e.Handled = true;
                break;

            case Key.C when e.KeyModifiers.HasFlag(KeyModifiers.Control) && !IsTextEntry(e.Source):
                if (shell.CopySelectionCommand.CanExecute(null))
                {
                    shell.CopySelectionCommand.Execute(null);
                }

                e.Handled = true;
                break;

            case Key.Enter when e.KeyModifiers.HasFlag(KeyModifiers.Alt):
                if (shell.ShowPropertiesCommand.CanExecute(null))
                {
                    shell.ShowPropertiesCommand.Execute(null);
                }

                e.Handled = true;
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// True when the key went to something the user is typing or selecting text in.
    /// </summary>
    /// <remarks>
    /// Without this, Ctrl+C inside the search box or over a selectable fingerprint would copy the
    /// grid selection instead of the text the user had highlighted.
    /// </remarks>
    private static bool IsTextEntry(object? source) => source is TextBox or SelectableTextBlock;
}
