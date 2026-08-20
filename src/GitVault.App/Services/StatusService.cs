using CommunityToolkit.Mvvm.ComponentModel;
using GitVault.Localization;

namespace GitVault.App.Services;

/// <summary>What the status bar is currently reporting.</summary>
internal enum StatusKind
{
    /// <summary>Idle, nothing in progress.</summary>
    Ready = 0,

    /// <summary>A scan is running.</summary>
    Busy,

    /// <summary>Something completed successfully.</summary>
    Done,

    /// <summary>Something needs the user's attention but is not an error.</summary>
    Warning,

    /// <summary>Something failed, including a refusal by the operating system.</summary>
    Error,
}

/// <summary>
/// The one place the status bar's text comes from.
/// </summary>
/// <remarks>
/// A shared service rather than a property on the shell, because the pages are what know when
/// something happened — a plan was applied, a folder could not be read — and routing that through
/// the shell view model would mean every page holding a reference to it.
///
/// The message is stored as a resource key plus its already-formatted text, so a culture change
/// re-renders whatever the bar is currently saying instead of leaving the previous language on
/// screen until the next event.
/// </remarks>
internal sealed partial class StatusService : ObservableObject, IDisposable
{
    private readonly Localizer _localizer;
    private Func<string>? _render;
    private bool _disposed;

    [ObservableProperty]
    private string _message = string.Empty;

    [ObservableProperty]
    private StatusKind _kind = StatusKind.Ready;

    /// <summary>Creates the service and reports the idle state.</summary>
    /// <param name="localizer">Localizer used to render messages.</param>
    public StatusService(Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);

        _localizer = localizer;
        _localizer.Service.CultureChanged += OnCultureChanged;

        Ready();
    }

    /// <summary>Reports the idle state.</summary>
    public void Ready() => Set(StatusKind.Ready, () => _localizer[Keys.Status_Ready]);

    /// <summary>Reports a message with no arguments.</summary>
    /// <param name="kind">How the message should be read.</param>
    /// <param name="key">Resource key of the message.</param>
    public void Report(StatusKind kind, string key) => Set(kind, () => _localizer[key]);

    /// <summary>Reports a formatted message.</summary>
    /// <param name="kind">How the message should be read.</param>
    /// <param name="key">Resource key of the format string.</param>
    /// <param name="arguments">Format arguments.</param>
    public void Report(StatusKind kind, string key, params object[] arguments) =>
        Set(kind, () => _localizer.Format(key, arguments));

    /// <summary>Reports text that is already localized, such as a message from a plan.</summary>
    /// <param name="kind">How the message should be read.</param>
    /// <param name="text">The text.</param>
    public void ReportText(StatusKind kind, string text) => Set(kind, () => text);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _localizer.Service.CultureChanged -= OnCultureChanged;
        _disposed = true;
    }

    private void Set(StatusKind kind, Func<string> render)
    {
        _render = render;
        Kind = kind;
        Message = render();
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        if (_render is not null)
        {
            Message = _render();
        }
    }
}
