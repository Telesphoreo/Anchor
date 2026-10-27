using System.Runtime.InteropServices;

namespace Anchor;

/// <summary>
/// Windows opens Status on a double-click of the tray icon. Avalonia reports
/// each left-button release, so a click counts only when it follows the
/// previous one within the system double-click time.
/// </summary>
internal sealed class TrayClickFilter
{
    private long? _lastClick;

    /// <summary>True when this click should open Status.</summary>
    public bool IsActivation()
    {
        var now = Environment.TickCount64;
        var isDoubleClick = now - _lastClick <= GetDoubleClickTime();
        _lastClick = isDoubleClick ? null : now;
        return isDoubleClick;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();
}
