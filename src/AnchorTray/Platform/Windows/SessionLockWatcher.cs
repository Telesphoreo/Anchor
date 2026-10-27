using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Anchor;

/// <summary>Lock-screen state from SystemEvents.SessionSwitch and the input desktop.</summary>
internal sealed class SessionLockWatcher : ISessionLockWatcher
{
    /// <param name="log">Unused on Windows, where neither source can fail.</param>
    public SessionLockWatcher(FileLog log)
    {
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    public event Action<bool>? LockChanged;

    /// <summary>
    /// True when the interactive session is on the secure desktop (lock screen).
    /// Covers the boot-straight-to-lock-screen case, which happens before any
    /// SessionSwitch event this process could observe.
    /// </summary>
    public Task<bool> IsLockedAsync()
    {
        const uint desktopSwitchDesktop = 0x0100;
        var desktop = OpenInputDesktop(0, false, desktopSwitchDesktop);
        if (desktop == IntPtr.Zero)
            return Task.FromResult(true);
        CloseDesktop(desktop);
        return Task.FromResult(false);
    }

    public void Dispose() => SystemEvents.SessionSwitch -= OnSessionSwitch;

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
                LockChanged?.Invoke(true);
                break;

            case SessionSwitchReason.SessionUnlock:
                LockChanged?.Invoke(false);
                break;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);

    [DllImport("user32.dll")]
    private static extern bool CloseDesktop(IntPtr desktop);
}
