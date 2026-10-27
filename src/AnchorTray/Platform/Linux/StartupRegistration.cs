namespace Anchor;

/// <summary>
/// Manages the per-user XDG autostart entry ($XDG_CONFIG_HOME/autostart/anchor.desktop,
/// default ~/.config/autostart). The app owns this file exclusively (the
/// package does not write it). Removing the package leaves the file behind;
/// TryExec makes the session skip it once /usr/bin/anchor is gone.
/// </summary>
internal static class StartupRegistration
{
    private const string ExecutablePath = "/usr/bin/anchor";

    private const string DesktopEntry =
        $"""
        [Desktop Entry]
        Type=Application
        Name=Anchor
        Comment=Uses an Anker Solix power station as a UPS
        Exec={ExecutablePath}
        TryExec={ExecutablePath}
        Icon=anchor
        Terminal=false
        X-GNOME-Autostart-enabled=true

        """;

    private static string EntryPath
    {
        get
        {
            var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrEmpty(configHome) || !Path.IsPathRooted(configHome))
                configHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(configHome, "autostart", "anchor.desktop");
        }
    }

    /// <summary>True when Anchor is registered to start at login for the current user.</summary>
    public static bool IsEnabled() => File.Exists(EntryPath);

    /// <summary>Register or unregister Anchor to start at login for the current user.</summary>
    public static void SetEnabled(bool enabled, FileLog? log = null)
    {
        var path = EntryPath;
        try
        {
            if (enabled)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, DesktopEntry);
            }
            else
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            log?.Error($"Failed to update the autostart entry '{path}': {ex.Message}");
        }
    }
}
