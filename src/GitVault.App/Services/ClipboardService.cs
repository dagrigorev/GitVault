using Avalonia.Controls;
using GitVault.Core.Settings;

namespace GitVault.App.Services;

/// <summary>Puts text on the clipboard, clearing secrets again after a delay.</summary>
internal interface IClipboardService
{
    /// <summary>Copies text that is not secret and leaves it there.</summary>
    /// <param name="text">Text to copy.</param>
    /// <param name="cancellationToken">Cancels the copy.</param>
    /// <returns><see langword="true"/> when the clipboard accepted the text.</returns>
    Task<bool> CopyAsync(string text, CancellationToken cancellationToken);

    /// <summary>
    /// Copies a secret and schedules the clipboard to be cleared after the configured delay.
    /// Best effort: a clipboard manager may keep its own copy, and the UI says so.
    /// </summary>
    /// <param name="text">Secret to copy.</param>
    /// <param name="cancellationToken">Cancels the copy.</param>
    /// <returns><see langword="true"/> when the clipboard accepted the text.</returns>
    Task<bool> CopySecretAsync(string text, CancellationToken cancellationToken);
}

/// <summary>
/// Clipboard access through the main window. Avalonia exposes the clipboard on a
/// <see cref="TopLevel"/>, so the window is handed over once at startup.
/// </summary>
internal sealed class ClipboardService : IClipboardService
{
    private readonly ISettingsService _settings;
    private TopLevel? _topLevel;

    /// <summary>Creates the service.</summary>
    /// <param name="settings">Settings, for the clipboard-clear delay.</param>
    public ClipboardService(ISettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    /// <summary>Supplies the window the clipboard belongs to.</summary>
    /// <param name="topLevel">The application's top level.</param>
    internal void Attach(TopLevel topLevel) => _topLevel = topLevel;

    /// <inheritdoc/>
    public async Task<bool> CopyAsync(string text, CancellationToken cancellationToken)
    {
        var clipboard = _topLevel?.Clipboard;
        if (clipboard is null)
        {
            return false;
        }

        await clipboard.SetTextAsync(text).WaitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc/>
    public async Task<bool> CopySecretAsync(string text, CancellationToken cancellationToken)
    {
        if (!await CopyAsync(text, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var seconds = _settings.Current.RevealPolicy.ClipboardClearSeconds;
        if (seconds <= 0)
        {
            return true;
        }

        // Fire and forget: the clear must not block the copy, and a cancelled clear is not an
        // error worth surfacing.
        _ = ClearLaterAsync(text, TimeSpan.FromSeconds(seconds));
        return true;
    }

    private async Task ClearLaterAsync(string expected, TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay).ConfigureAwait(false);

            var clipboard = _topLevel?.Clipboard;
            if (clipboard is null)
            {
                return;
            }

            // Only clear what we put there: the user may have copied something else since.
            var current = await clipboard.GetTextAsync().ConfigureAwait(false);
            if (string.Equals(current, expected, StringComparison.Ordinal))
            {
                await clipboard.ClearAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down; nothing to clean up.
        }
    }
}
