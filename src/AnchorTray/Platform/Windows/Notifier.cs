using Microsoft.Toolkit.Uwp.Notifications;

namespace Anchor;

/// <summary>
/// Toast notifications through ToastNotificationManagerCompat, which registers
/// its own AUMID and click activator for this unpackaged app. Each toast
/// carries its kind as an argument so a click can be routed. Windows has no
/// urgency levels for toasts, so the urgency is not used here.
/// </summary>
internal sealed class Notifier : INotifier
{
    private const string KindArgument = "kind";

    private readonly FileLog _log;

    public Notifier(FileLog log)
    {
        _log = log;
        ToastNotificationManagerCompat.OnActivated += OnActivated;
    }

    public event Action<NotificationClick>? Clicked;

    public void Show(NotificationKind kind, NotificationUrgency urgency, string title, string body)
    {
        try
        {
            new ToastContentBuilder()
                .AddArgument(KindArgument, kind.ToString())
                .AddText(title)
                .AddText(body)
                .Show();
        }
        catch (Exception ex)
        {
            _log.Error($"Showing notification '{title}' failed: {ex.Message}");
        }
    }

    public void Dispose() => ToastNotificationManagerCompat.OnActivated -= OnActivated;

    private void OnActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        var arguments = ToastArguments.Parse(e.Argument);
        var isUpdate = arguments.TryGetValue(KindArgument, out var kind)
                       && kind == nameof(NotificationKind.Update);
        Clicked?.Invoke(isUpdate ? NotificationClick.InstallUpdate : NotificationClick.OpenStatus);
    }
}
