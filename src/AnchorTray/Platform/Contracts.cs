namespace Anchor;

// Each OS folder (Platform/Windows, Platform/Linux) implements these with the
// same type names: Notifier, SessionLockWatcher, TrayClickFilter,
// ShutdownService, StartupRegistration and PlatformInfo. The project compiles
// only the folder for its target framework.

/// <summary>What a click on a notification asks the app to do.</summary>
internal enum NotificationClick
{
    OpenStatus,
    InstallUpdate,
    CancelShutdown,
}

/// <summary>Which notification this is; decides what a click on it does.</summary>
internal enum NotificationKind
{
    /// <summary>A click opens Status.</summary>
    Status,

    /// <summary>A click starts the update.</summary>
    Update,

    /// <summary>"Shutdown countdown started": a click opens Status; on Linux it also offers "Cancel shutdown".</summary>
    Countdown,
}

internal enum NotificationUrgency
{
    Low,
    Normal,
    Critical,
}

/// <summary>Desktop notifications with click routing.</summary>
internal interface INotifier : IDisposable
{
    /// <summary>Raised on any thread when the user clicks a notification or one of its actions.</summary>
    event Action<NotificationClick>? Clicked;

    void Show(NotificationKind kind, NotificationUrgency urgency, string title, string body);
}

/// <summary>Reports whether the user's session is on the lock screen.</summary>
internal interface ISessionLockWatcher : IDisposable
{
    /// <summary>Raised on any thread with true on lock and false on unlock.</summary>
    event Action<bool>? LockChanged;

    /// <summary>The state right now. False when it cannot be determined.</summary>
    Task<bool> IsLockedAsync();
}
