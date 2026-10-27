using Tmds.DBus.Protocol;

namespace Anchor;

/// <summary>
/// Desktop notifications through org.freedesktop.Notifications on the session
/// bus. A click arrives as the "default" action on ActionInvoked. The
/// countdown notification also carries a "Cancel shutdown" action, so the
/// countdown can be cancelled even when KWin keeps the countdown window from
/// taking focus.
/// </summary>
internal sealed class Notifier : INotifier
{
    private const string Service = "org.freedesktop.Notifications";
    private const string ObjectPath = "/org/freedesktop/Notifications";
    private const string Interface = "org.freedesktop.Notifications";
    private const string DefaultAction = "default";
    private const string CancelAction = "cancel";

    private readonly FileLog _log;
    private readonly DBusConnection _connection = DBusConnection.Session;
    private readonly Dictionary<uint, NotificationKind> _shown = new();
    private readonly Task<IDisposable[]> _subscriptions;
    private bool _disposed;

    public Notifier(FileLog log)
    {
        _log = log;
        _subscriptions = SubscribeAsync();
    }

    public event Action<NotificationClick>? Clicked;

    public void Show(NotificationKind kind, NotificationUrgency urgency, string title, string body) =>
        _ = ShowAsync(kind, urgency, title, body);

    public void Dispose()
    {
        _disposed = true;
        if (_subscriptions.IsCompletedSuccessfully)
        {
            foreach (var subscription in _subscriptions.Result)
                subscription.Dispose();
        }
    }

    private async Task<IDisposable[]> SubscribeAsync()
    {
        // Matched without a sender so the subscriptions survive the notification
        // server restarting (plasmashell owns it on KDE).
        var invoked = await _connection.WatchSignalAsync(null, ObjectPath, Interface, "ActionInvoked",
            static (message, _) =>
            {
                var reader = message.GetBodyReader();
                return (Id: reader.ReadUInt32(), Action: reader.ReadString());
            },
            (Notification<(uint Id, string Action)> signal) =>
            {
                if (signal.HasValue)
                    OnActionInvoked(signal.Value.Id, signal.Value.Action);
            },
            ObserverFlags.None, emitOnCapturedContext: false).ConfigureAwait(false);

        var closed = await _connection.WatchSignalAsync(null, ObjectPath, Interface, "NotificationClosed",
            static (message, _) => message.GetBodyReader().ReadUInt32(),
            (Notification<uint> signal) =>
            {
                if (!signal.HasValue)
                    return;
                lock (_shown)
                    _shown.Remove(signal.Value);
            },
            ObserverFlags.None, emitOnCapturedContext: false).ConfigureAwait(false);

        return [invoked, closed];
    }

    private async Task ShowAsync(NotificationKind kind, NotificationUrgency urgency, string title, string body)
    {
        try
        {
            // Without the subscriptions a click would be lost, but the text still matters.
            try
            {
                await _subscriptions.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error($"Subscribing to notification clicks failed: {ex.Message}");
            }

            string[] actions = kind == NotificationKind.Countdown
                ? [DefaultAction, "Open status", CancelAction, "Cancel shutdown"]
                : [DefaultAction, kind == NotificationKind.Update ? "Update" : "Open status"];

            var id = await _connection.CallMethodAsync(
                CreateNotifyMessage(urgency, title, body, actions),
                static (message, _) => message.GetBodyReader().ReadUInt32()).ConfigureAwait(false);

            lock (_shown)
                _shown[id] = kind;
        }
        catch (Exception ex)
        {
            _log.Error($"Showing notification '{title}' failed: {ex.Message}");
        }
    }

    private MessageBuffer CreateNotifyMessage(NotificationUrgency urgency, string title, string body, string[] actions)
    {
        using var writer = _connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: Service,
            path: ObjectPath,
            @interface: Interface,
            member: "Notify",
            signature: "susssasa{sv}i");
        writer.WriteString("Anchor");     // app_name
        writer.WriteUInt32(0);            // replaces_id: none
        writer.WriteString("anchor");     // app_icon
        writer.WriteString(title);
        writer.WriteString(body);
        writer.WriteArray(actions);

        var hints = writer.WriteDictionaryStart();
        writer.WriteDictionaryEntryStart();
        writer.WriteString("urgency");
        writer.WriteVariantByte((byte)urgency); // 0 low, 1 normal, 2 critical.
        writer.WriteDictionaryEntryStart();
        writer.WriteString("desktop-entry");
        writer.WriteVariantString("anchor");
        writer.WriteDictionaryEnd(hints);

        writer.WriteInt32(-1);            // expire_timeout: server default.
        return writer.CreateMessage();
    }

    private void OnActionInvoked(uint id, string action)
    {
        NotificationKind kind;
        lock (_shown)
        {
            if (_disposed || !_shown.TryGetValue(id, out kind))
                return; // Another application's notification.
        }

        var click = action switch
        {
            CancelAction => NotificationClick.CancelShutdown,
            _ when kind == NotificationKind.Update => NotificationClick.InstallUpdate,
            _ => NotificationClick.OpenStatus,
        };
        Clicked?.Invoke(click);
    }
}
