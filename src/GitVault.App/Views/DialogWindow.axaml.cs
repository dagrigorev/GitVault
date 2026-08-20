using Avalonia.Controls;
using Avalonia.Interactivity;
using GitVault.App.ViewModels;

namespace GitVault.App.Views;

/// <summary>
/// The frame every modal dialog is shown in.
/// </summary>
/// <remarks>
/// Closing is driven by the view model rather than by the buttons, so a dialog that decides for
/// itself that it is finished — or one driven from a test — closes the same way a click does.
/// </remarks>
internal sealed partial class DialogWindow : Window
{
    private DialogViewModel? _dialog;

    public DialogWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <inheritdoc/>
    protected override void OnUnloaded(RoutedEventArgs e)
    {
        Detach();
        base.OnUnloaded(e);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        Detach();

        if (DataContext is DialogViewModel dialog)
        {
            _dialog = dialog;
            _dialog.CloseRequested += OnCloseRequested;
        }
    }

    private void Detach()
    {
        if (_dialog is not null)
        {
            _dialog.CloseRequested -= OnCloseRequested;
            _dialog = null;
        }
    }

    private void OnCloseRequested(object? sender, bool result) => Close(result);
}
