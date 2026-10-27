using Tmds.DBus.Protocol;

namespace Anchor;

/// <summary>
/// Initiates a Linux shutdown without forcing applications closed. In a KDE
/// session it asks Plasma to log out and shut down, which lets every app
/// prompt about unsaved work exactly like a user-initiated shutdown. Only
/// when the session has no org.kde.Shutdown does it ask logind to power off.
/// A D-Bus error counts as a failed attempt; retry scheduling belongs to the
/// policy layer so AC restoration can cancel a pending retry.
/// </summary>
internal static class ShutdownService
{
    private const string KdeShutdownService = "org.kde.Shutdown";

    /// <summary>Attempt to shut the system down once.</summary>
    /// <returns>True if the shutdown was successfully initiated.</returns>
    public static async Task<bool> ShutdownAsync(FileLog log)
    {
        try
        {
            if (await IsKdeSessionAsync().ConfigureAwait(false))
            {
                await CallAsync(DBusConnection.Session, KdeShutdownService, "/Shutdown", "org.kde.Shutdown",
                    "logoutAndShutdown", null).ConfigureAwait(false);
                log.Info("System shutdown initiated via KDE session.");
                return true;
            }

            // interactive=false: polkit allows this for the active local session
            // without asking; anything else (other users logged in) is an error.
            await CallAsync(DBusConnection.System, "org.freedesktop.login1", "/org/freedesktop/login1",
                "org.freedesktop.login1.Manager", "PowerOff", false).ConfigureAwait(false);
            log.Info("System shutdown initiated via logind.");
            return true;
        }
        catch (DBusErrorReplyException ex)
        {
            log.Error($"Shutdown request failed: {ex.ErrorName}: {ex.ErrorMessage}");
            return false;
        }
        catch (Exception ex)
        {
            log.Error($"Exception initiating shutdown: {ex}");
            return false;
        }
    }

    /// <summary>
    /// True when org.kde.Shutdown is running or activatable on the session bus.
    /// Throws when the session bus cannot be asked, so a KDE session is never
    /// mistaken for a non-KDE one.
    /// </summary>
    private static async Task<bool> IsKdeSessionAsync()
    {
        var session = DBusConnection.Session;
        var running = await session.ListServicesAsync().ConfigureAwait(false);
        if (running.Contains(KdeShutdownService))
            return true;
        var activatable = await session.ListActivatableServicesAsync().ConfigureAwait(false);
        return activatable.Contains(KdeShutdownService);
    }

    private static Task CallAsync(DBusConnection connection, string destination, string path, string @interface,
        string member, bool? argument)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: destination,
            path: path,
            @interface: @interface,
            member: member,
            signature: argument is null ? null : "b");
        if (argument is { } value)
            writer.WriteBool(value);
        return connection.CallMethodAsync(writer.CreateMessage());
    }
}
