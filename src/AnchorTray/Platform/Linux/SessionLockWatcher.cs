using Tmds.DBus.Protocol;

namespace Anchor;

/// <summary>
/// Lock-screen state from org.freedesktop.ScreenSaver on the session bus, which
/// KDE's screen locker implements: GetActive for the current state and the
/// ActiveChanged signal after that.
/// </summary>
internal sealed class SessionLockWatcher : ISessionLockWatcher
{
    private const string Service = "org.freedesktop.ScreenSaver";
    private const string ObjectPath = "/org/freedesktop/ScreenSaver";
    private const string Interface = "org.freedesktop.ScreenSaver";

    private readonly FileLog _log;
    private readonly DBusConnection _connection = DBusConnection.Session;
    private readonly Task<IDisposable> _subscription;

    public SessionLockWatcher(FileLog log)
    {
        _log = log;
        _subscription = SubscribeAsync();
        _subscription.ContinueWith(
            t => _log.Warning($"Lock-screen changes cannot be watched: {t.Exception?.GetBaseException().Message}"),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    public event Action<bool>? LockChanged;

    public async Task<bool> IsLockedAsync()
    {
        try
        {
            return await _connection.CallMethodAsync(CreateGetActiveMessage(),
                static (message, _) => message.GetBodyReader().ReadBool()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warning($"Could not read the lock-screen state ({ex.Message}); assuming unlocked.");
            return false;
        }
    }

    private MessageBuffer CreateGetActiveMessage()
    {
        using var writer = _connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: Service,
            path: ObjectPath,
            @interface: Interface,
            member: "GetActive");
        return writer.CreateMessage();
    }

    public void Dispose()
    {
        if (_subscription.IsCompletedSuccessfully)
            _subscription.Result.Dispose();
    }

    private async Task<IDisposable> SubscribeAsync() =>
        // Matched without a sender so the subscription survives the screen
        // locker's service restarting.
        await _connection.WatchSignalAsync(null, ObjectPath, Interface, "ActiveChanged",
            static (message, _) => message.GetBodyReader().ReadBool(),
            (Notification<bool> signal) =>
            {
                if (signal.HasValue)
                    LockChanged?.Invoke(signal.Value);
            },
            ObserverFlags.None, emitOnCapturedContext: false).ConfigureAwait(false);
}
