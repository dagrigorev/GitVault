using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using GitVault.App.ViewModels;
using GitVault.App.Views;
using GitVault.Localization;

namespace GitVault.App.Services;

/// <summary>Shows the application's modal dialogs.</summary>
/// <remarks>
/// An interface rather than direct window construction, so a view model can require the user to
/// confirm something without referencing a window type — and so the headless tests can assert
/// that a dangerous action asked first, by answering the dialog instead of rendering it.
/// </remarks>
internal interface IDialogService : IFolderPicker
{
    /// <summary>Shows a dialog and waits for the user to accept or dismiss it.</summary>
    /// <param name="dialog">The dialog's view model.</param>
    /// <returns><see langword="true"/> when the user accepted it.</returns>
    Task<bool> ShowAsync(DialogViewModel dialog);
}

/// <summary>Opens a classic dialog window over the main window.</summary>
internal sealed class DialogService : IDialogService
{
    private readonly Localizer _localizer;

    /// <summary>Creates the service.</summary>
    /// <param name="localizer">Localizer, for the folder picker's title.</param>
    public DialogService(Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        _localizer = localizer;
    }

    /// <inheritdoc/>
    public async Task<bool> ShowAsync(DialogViewModel dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        var owner = MainWindow();
        if (owner is null)
        {
            // No window means no user to ask. Refusing is the safe answer: every caller treats
            // false as "the user did not agree", and nothing mutates on that path.
            return false;
        }

        var window = new DialogWindow { DataContext = dialog };
        return await window.ShowDialog<bool>(owner).ConfigureAwait(true);
    }

    /// <inheritdoc/>
    public async Task<string?> PickFolderAsync(string titleKey, string? startPath)
    {
        var owner = MainWindow();
        if (owner?.StorageProvider is not { CanPickFolder: true } storage)
        {
            return null;
        }

        var options = new FolderPickerOpenOptions
        {
            Title = _localizer[titleKey],
            AllowMultiple = false,
        };

        if (!string.IsNullOrWhiteSpace(startPath) && Directory.Exists(startPath))
        {
            options.SuggestedStartLocation = await storage.TryGetFolderFromPathAsync(startPath).ConfigureAwait(true);
        }

        var folders = await storage.OpenFolderPickerAsync(options).ConfigureAwait(true);
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    private static Window? MainWindow() =>
        Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;
}
