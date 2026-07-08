using Microsoft.Win32;

namespace Anchor;

/// <summary>
/// Manages the per-user "start at login" registry value (HKCU Run). The app owns
/// this exclusively — the installer does not write it — so an admin install never
/// touches a per-user area and there is a single source of truth for autostart.
/// </summary>
internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Anchor";

    /// <summary>True when Anchor is registered to start at login for the current user.</summary>
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunValueName) is string;
    }

    /// <summary>Register or unregister Anchor to start at login for the current user.</summary>
    public static void SetEnabled(bool enabled, FileLog? log = null)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (enabled)
                key.SetValue(RunValueName, $"\"{Application.ExecutablePath}\"");
            else
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            log?.Error($"Failed to update the 'start with Windows' registry value: {ex.Message}");
        }
    }
}
