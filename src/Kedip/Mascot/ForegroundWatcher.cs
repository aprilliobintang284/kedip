using Kedip.Core;

namespace Kedip.Mascot;

/// <summary>
/// Tracks what kind of window is in the foreground.
/// OWNER: B. Scaffold stub — replace the implementation, keep the public API.
/// </summary>
public sealed class ForegroundWatcher : IAppService
{
    /// <summary>Desktop/shell (Progman, WorkerW, Shell_TrayWnd, Shell_SecondaryTrayWnd), no window, or a Kedip window.</summary>
    public bool IsDesktopForeground { get; private set; } = true;

    /// <summary>A non-shell window covers its whole monitor (game, video, presentation).</summary>
    public bool IsFullscreenAppForeground { get; private set; }

    /// <summary>Raised on the UI thread when either property changes.</summary>
    public event EventHandler? Changed;

    public void Start()
    {
    }

    public void Dispose()
    {
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
