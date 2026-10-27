using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace Anchor;

/// <summary>
/// The application: a tray icon with programmatically drawn battery icons,
/// notifications on policy transitions, and the Status/Settings/Countdown
/// windows. There is no main window. All monitor and platform events are
/// posted onto the UI thread.
/// </summary>
public sealed class TrayAppContext : Application
{
    private enum IconKind
    {
        Unconfigured,
        Unavailable,
        OnGrid,
        OnBattery,
        Pending,
    }

    private readonly FileLog _log;
    private readonly Dictionary<(IconKind Kind, int Bucket), WindowIcon> _iconCache = new();
    private readonly TrayClickFilter _clickFilter = new();

    private AppConfig _config;
    private UpsMonitor _monitor = null!;
    private TrayIcon _icon = null!;
    private INotifier _notifier = null!;
    private ISessionLockWatcher _lockWatcher = null!;
    private DispatcherTimer _unlockReconcileTimer = null!;
    private NativeMenuItem _cancelMenuItem = null!;
    private NativeMenuItem _installUpdateItem = null!;
    private NativeMenuItem _checkUpdatesItem = null!;
    private PosixSignalRegistration? _sigterm;

    private StatusWindow? _statusWindow;
    private SettingsWindow? _settingsWindow;
    private CountdownWindow? _countdownWindow;
    private string _lastTooltip = "";
    private (IconKind Kind, int Bucket)? _lastIconKey;
    private bool _powerNotificationSeen;
    private bool _sessionLocked;
    private bool _lockChangeSeen;
    private bool? _lastNotifiedAvailable;
    private bool _exiting;
    private UpdateInfo? _availableUpdate;
    private bool _updateBusy;

    internal TrayAppContext(AppConfig config, FileLog log)
    {
        _log = log;
        _config = config;
    }

    public override void Initialize()
    {
        Name = "Anchor";
        RequestedThemeVariant = ThemeVariant.Default;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();

        var statusItem = new NativeMenuItem("Status…");
        statusItem.Click += (_, _) => OpenStatus();
        var settingsItem = new NativeMenuItem("Settings…");
        settingsItem.Click += (_, _) => OpenSettings();
        _cancelMenuItem = new NativeMenuItem("Cancel shutdown") { IsEnabled = false };
        _cancelMenuItem.Click += (_, _) => CancelShutdown();
        _installUpdateItem = new NativeMenuItem("Update available…") { IsVisible = false };
        _installUpdateItem.Click += OnInstallUpdate;
        _checkUpdatesItem = new NativeMenuItem("Check for updates…");
        _checkUpdatesItem.Click += OnCheckForUpdates;
        var exitItem = new NativeMenuItem("Exit");
        exitItem.Click += OnExit;

        var menu = new NativeMenu();
        menu.Items.Add(statusItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(_cancelMenuItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(_installUpdateItem);
        menu.Items.Add(_checkUpdatesItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exitItem);

        _icon = new TrayIcon
        {
            Icon = GetIcon(IconKind.Unconfigured, -1),
            ToolTipText = "Anchor",
            Menu = menu,
            IsVisible = true,
        };
        _icon.Clicked += (_, _) =>
        {
            if (_clickFilter.IsActivation())
                OpenStatus();
        };
        TrayIcon.SetIcons(this, [_icon]);

        _notifier = new Notifier(_log);
        _notifier.Clicked += click => Dispatcher.UIThread.Post(() => OnNotificationClicked(click));

        // Availability notifications are suppressed while the session is locked
        // (boot straight to the lock screen, resume from sleep); after unlock
        // one notification is shown only if the state differs from what the
        // user last saw. The timer gives BLE a beat to reconnect after unlock.
        _unlockReconcileTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _unlockReconcileTimer.Tick += OnUnlockReconcile;
        _lockWatcher = new SessionLockWatcher(_log);
        _lockWatcher.LockChanged += locked => Dispatcher.UIThread.Post(() => OnLockChanged(locked));
        FireAndForget(ReadInitialLockStateAsync(), "Reading the lock-screen state");

        _monitor = new UpsMonitor(_config, _log);
        _monitor.StatusChanged += status => Dispatcher.UIThread.Post(() => OnStatusUi(status));
        _monitor.EffectOccurred += effect => Dispatcher.UIThread.Post(() => OnEffectUi(effect));

        // A Linux session ends its apps with SIGTERM; leave through Exit so the
        // monitor closes the BLE link and the log records the exit.
        _sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            Dispatcher.UIThread.Post(() => OnExit(this, EventArgs.Empty));
        });

        FireAndForget(_monitor.ApplySettingsAsync(_config), "Starting monitor");
        FireAndForget(Task.Run(StartupUpdateCheckAsync), "Update check");

        if (!_config.Configured)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Notify(NotificationUrgency.Low, "Anchor is not configured",
                    "Select your power station in Settings.");
                OpenSettings();
            });
        }
    }

    private async void OnExit(object? sender, EventArgs e)
    {
        if (_exiting)
            return;
        _exiting = true;
        _icon.IsVisible = false;

        // Close any open child windows so their timers stop before teardown.
        _statusWindow?.Close();
        _settingsWindow?.Close();
        CloseCountdownWindow();

        _unlockReconcileTimer.Stop();
        _lockWatcher.Dispose();
        _notifier.Dispose();

        try
        {
            await _monitor.DisposeAsync();
        }
        catch (Exception ex)
        {
            _log.Error($"Error stopping monitor on exit: {ex}");
        }

        _log.Info("Anchor exiting.");
        Environment.ExitCode = 0;
        _sigterm?.Dispose();
        _icon.Dispose();
        ((IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!).Shutdown();
    }

    private void OnStatusUi(UpsStatus status)
    {
        if (_exiting)
            return;

        UpdateIcon(status);
        UpdateTooltip(status);
        _cancelMenuItem.IsEnabled = status.PolicyState == PolicyState.Pending;

        // Persist a newly learned device name so it shows even before a connect.
        if (status.Configured
            && status.DeviceName is not ("" or "Unknown")
            && string.Equals(status.DeviceMac, _config.DeviceMac, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(status.DeviceName, _config.DeviceName, StringComparison.Ordinal))
        {
            _config.DeviceName = status.DeviceName;
            _ = _config.Save(_log);
        }

        _statusWindow?.UpdateStatus(status);

        if (status.PolicyState == PolicyState.Pending && status.ShutdownDeadline is { } deadline)
            _countdownWindow?.UpdateDeadline(deadline);
        else
            CloseCountdownWindow();
    }

    private void OnEffectUi(PolicyEffect effect)
    {
        if (_exiting)
            return;

        switch (effect)
        {
            case PolicyEffect.EnteredBattery:
                _powerNotificationSeen = true;
                Notify(NotificationUrgency.Normal, "Wall power lost",
                    "The power station is running on battery.");
                break;

            case PolicyEffect.EnteredGrid:
                // Suppress the notification for the very first grid state after
                // start; it is not a "restore", just the initial reading.
                if (_powerNotificationSeen)
                    Notify(NotificationUrgency.Low, "Wall power restored",
                        "The power station is back on grid power.");
                _powerNotificationSeen = true;
                break;

            case PolicyEffect.CountdownStarted:
                Notify(NotificationUrgency.Critical, "Shutdown countdown started",
                    $"Battery at or below {_config.BatteryFloorPercent}% on battery power.",
                    NotificationKind.Countdown);
                ShowCountdownWindow();
                break;

            case PolicyEffect.CountdownCancelledAcRestored:
                Notify(NotificationUrgency.Low, "Shutdown cancelled",
                    "AC power was restored during the countdown.");
                break;

            case PolicyEffect.CountdownCancelledReconfigured:
                CloseCountdownWindow();
                Notify(NotificationUrgency.Low, "Shutdown cancelled",
                    "The monitored device was reconfigured during the countdown.");
                break;

            case PolicyEffect.ShutdownNow:
                OnShutdownNow();
                break;

            case PolicyEffect.ShutdownFailed:
                Notify(NotificationUrgency.Critical, "Shutdown failed",
                    "The system did not accept the shutdown request. Anchor will retry shortly.");
                ShowCountdownWindow();
                break;

            case PolicyEffect.BecameUnavailable:
                if (!_sessionLocked)
                    NotifyAvailability(false);
                break;

            case PolicyEffect.BecameAvailable:
                // Skip the notification when the user was never told about a
                // drop (it happened entirely behind the lock screen).
                if (!_sessionLocked && _lastNotifiedAvailable != true)
                    NotifyAvailability(true);
                break;

            case PolicyEffect.FloorRaisedByDeviceLimit:
                var floor = _monitor.GetSnapshot().EffectiveFloorPercent;
                Notify(NotificationUrgency.Low, "Shutdown floor raised",
                    $"Shutdown floor raised to {floor}% to stay above the station's discharge cutoff.");
                break;
        }
    }

    private void OnShutdownNow()
    {
        CloseCountdownWindow();
        if (_config.DryRun)
        {
            _log.Info("[DRY RUN] Battery at floor on battery power; system shutdown would be initiated now.");
            Notify(NotificationUrgency.Critical, "Dry run: shutdown suppressed",
                "System shutdown would have been initiated now.");
            _monitor.CompleteShutdownAttempt(ShutdownAttemptResult.SuppressedDryRun);
            return;
        }

        _log.Warning("Battery at floor on battery power; initiating system shutdown.");
        Notify(NotificationUrgency.Critical, "Shutting down", "Power station battery is at the configured floor.");
        FireAndForget(ExecuteShutdownAsync(), "System shutdown");
    }

    private async Task ExecuteShutdownAsync()
    {
        var accepted = await ShutdownService.ShutdownAsync(_log);
        _monitor.CompleteShutdownAttempt(
            accepted ? ShutdownAttemptResult.Accepted : ShutdownAttemptResult.Failed);
    }

    private void OpenStatus()
    {
        if (_exiting)
            return;
        if (_statusWindow is not null)
        {
            _statusWindow.Activate();
            return;
        }

        var window = new StatusWindow(_monitor.GetSnapshot());
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_statusWindow, window))
                _statusWindow = null;
        };
        _statusWindow = window;
        window.Show();
    }

    private void OpenSettings()
    {
        if (_exiting)
            return;
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        var window = new SettingsWindow(_config, _log, OnSettingsSaved);
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_settingsWindow, window))
                _settingsWindow = null;
        };
        _settingsWindow = window;
        window.Show();
    }

    private void OnSettingsSaved(AppConfig config)
    {
        _config = config;
        FireAndForget(_monitor.ApplySettingsAsync(config), "Applying settings");
    }

    private void CancelShutdown()
    {
        _monitor.CancelCountdownByUser();
        CloseCountdownWindow();
    }

    private void ShowCountdownWindow()
    {
        var deadline = _monitor.GetSnapshot().ShutdownDeadline
                       ?? DateTimeOffset.Now.AddSeconds(_config.CountdownSeconds);
        if (_countdownWindow is not null)
        {
            _countdownWindow.UpdateDeadline(deadline);
            _countdownWindow.Activate();
            return;
        }

        var window = new CountdownWindow(deadline, () => _monitor.CancelCountdownByUser());
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_countdownWindow, window))
                _countdownWindow = null;
        };
        _countdownWindow = window;
        window.Show();
        window.Activate();
    }

    private void CloseCountdownWindow()
    {
        if (_countdownWindow is { } window)
        {
            _countdownWindow = null;
            window.Close();
        }
    }

    /// <summary>
    /// Background update check started at launch; never throws. Marshals back
    /// to the UI thread like the monitor events do.
    /// </summary>
    private async Task StartupUpdateCheckAsync()
    {
        try
        {
            var info = await Updater.CheckAsync(_log).ConfigureAwait(false);
            if (info is { IsNewer: true })
                Dispatcher.UIThread.Post(() => OnUpdateAvailableUi(info));
        }
        catch (Exception ex)
        {
            _log.Error($"Update check failed: {ex}");
        }
    }

    private void OnUpdateAvailableUi(UpdateInfo info)
    {
        if (_exiting)
            return;

        _availableUpdate = info;
        _installUpdateItem.Header = $"Update available: install {info.Latest}…";
        _installUpdateItem.IsVisible = true;
        Notify(NotificationUrgency.Low, "Update available",
            $"Anchor {info.Latest} is available. Click here or use the tray menu to update.",
            NotificationKind.Update);
    }

    private void OnNotificationClicked(NotificationClick click)
    {
        if (_exiting)
            return;

        switch (click)
        {
            case NotificationClick.InstallUpdate:
                FireAndForget(InstallUpdateAsync(), "Installing update");
                break;

            case NotificationClick.CancelShutdown:
                CancelShutdown();
                break;

            default:
                OpenStatus();
                break;
        }
    }

    private async void OnInstallUpdate(object? sender, EventArgs e)
    {
        try
        {
            await InstallUpdateAsync();
        }
        catch (Exception ex)
        {
            _log.Error($"Installing update failed: {ex}");
        }
    }

    private async Task InstallUpdateAsync()
    {
        if (_availableUpdate is not { } info || _updateBusy || _exiting)
            return;

        _updateBusy = true;
        _installUpdateItem.IsEnabled = false;
        try
        {
            var launched = await Updater.DownloadAndLaunchInstallerAsync(info, _log);
            if (launched)
            {
                // Exit so the installer can replace the running executable;
                // same path as the Exit menu item.
                OnExit(this, EventArgs.Empty);
                return;
            }

            if (info.DownloadUrl.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                Notify(NotificationUrgency.Normal, "Update failed",
                    "Could not download or launch the installer. See the log for details.");
        }
        finally
        {
            _updateBusy = false;
            _installUpdateItem.IsEnabled = true;
        }
    }

    private async void OnCheckForUpdates(object? sender, EventArgs e)
    {
        if (_updateBusy)
            return;

        _checkUpdatesItem.IsEnabled = false;
        try
        {
            var info = await Updater.CheckAsync(_log);
            if (_exiting)
                return;

            if (info is null)
                Notify(NotificationUrgency.Normal, "Update check failed",
                    "Could not reach GitHub to check for updates. See the log for details.");
            else if (info.IsNewer)
                OnUpdateAvailableUi(info);
            else
                Notify(NotificationUrgency.Low, "Up to date",
                    $"Anchor {info.Current.ToString(3)} is the latest version.");
        }
        catch (Exception ex)
        {
            _log.Error($"Update check failed: {ex}");
        }
        finally
        {
            _checkUpdatesItem.IsEnabled = true;
        }
    }

    private void NotifyAvailability(bool available)
    {
        _lastNotifiedAvailable = available;
        if (available)
            Notify(NotificationUrgency.Low, "Device connected",
                "Receiving data from the power station.");
        else
            Notify(NotificationUrgency.Normal, "Device unavailable",
                "No fresh data from the power station.");
    }

    /// <summary>
    /// Covers the boot-straight-to-lock-screen case, which happens before any
    /// lock change this process could observe. A change seen while the query
    /// was running is newer, so it wins.
    /// </summary>
    private async Task ReadInitialLockStateAsync()
    {
        var locked = await _lockWatcher.IsLockedAsync();
        if (!_lockChangeSeen)
            _sessionLocked = locked;
    }

    private void OnLockChanged(bool locked)
    {
        if (_exiting)
            return;
        _lockChangeSeen = true;

        _sessionLocked = locked;
        _unlockReconcileTimer.Stop();
        if (!locked)
            _unlockReconcileTimer.Start();
    }

    private void OnUnlockReconcile(object? sender, EventArgs e)
    {
        _unlockReconcileTimer.Stop();
        if (_exiting || !_config.Configured)
            return;

        var available = _monitor.GetSnapshot().Available;
        if (_lastNotifiedAvailable != available)
            NotifyAvailability(available);
    }

    private void Notify(NotificationUrgency urgency, string title, string text,
        NotificationKind kind = NotificationKind.Status) =>
        _notifier.Show(kind, urgency, title, text);

    private void UpdateIcon(UpsStatus status)
    {
        var kind = GetIconKind(status);
        var bucket = BatteryBucket(status.BatteryPercent);
        var key = (kind, bucket);
        if (_lastIconKey == key)
            return;
        _lastIconKey = key;
        _icon.Icon = GetIcon(kind, status.BatteryPercent);
    }

    private static IconKind GetIconKind(UpsStatus status)
    {
        if (!status.Configured)
            return IconKind.Unconfigured;
        if (!status.Available)
            return IconKind.Unavailable;
        return status.PolicyState switch
        {
            PolicyState.OnBattery => IconKind.OnBattery,
            PolicyState.Pending or PolicyState.ShutdownIssued => IconKind.Pending,
            _ => IconKind.OnGrid,
        };
    }

    private void UpdateTooltip(UpsStatus status)
    {
        var text = BuildTooltip(status);
        if (text == _lastTooltip)
            return;
        _lastTooltip = text;
        _icon.ToolTipText = text;
    }

    private static string BuildTooltip(UpsStatus status)
    {
        string text;
        if (!status.Configured)
        {
            text = "Anchor · not configured";
        }
        else if (!status.Available)
        {
            text = "Anchor · device unavailable";
        }
        else
        {
            var battery = status.BatteryPercent < 0 ? "?" : status.BatteryPercent.ToString();
            var state = status.PolicyState switch
            {
                PolicyState.OnBattery => "On battery",
                PolicyState.Pending => status.ShutdownDeadline is { } deadline
                    ? $"Shutdown in {Math.Max(0, (int)(deadline - DateTimeOffset.Now).TotalSeconds)} s"
                    : "Shutdown pending",
                PolicyState.ShutdownIssued => "Shutdown issued",
                _ => "On grid",
            };
            text = $"{battery}% · {state}";
            if (status.PowerOut >= 0)
                text += $" · out {status.PowerOut} W";
        }

        return text.Length <= 63 ? text : text[..63];
    }

    private static int BatteryBucket(int percent) => percent < 0 ? -1 : Math.Clamp(percent, 0, 100) / 10;

    /// <summary>Get (or lazily draw and cache) the battery icon for a state and 10% bucket.</summary>
    private WindowIcon GetIcon(IconKind kind, int percent)
    {
        var bucket = BatteryBucket(percent);
        var key = (kind, bucket);
        if (_iconCache.TryGetValue(key, out var cached))
            return cached;

        // WindowIcon copies the pixels, so the bitmap can go right away.
        using var bitmap = DrawBatteryBitmap(GetFillColor(kind), bucket < 0 ? -1 : bucket * 10);
        var icon = new WindowIcon(bitmap);
        _iconCache[key] = icon;
        return icon;
    }

    private static Color GetFillColor(IconKind kind) => kind switch
    {
        IconKind.OnGrid => Color.FromRgb(76, 175, 80),      // Green.
        IconKind.OnBattery => Color.FromRgb(255, 152, 0),   // Orange.
        IconKind.Pending => Color.FromRgb(229, 57, 53),     // Red.
        IconKind.Unavailable => Color.FromRgb(128, 128, 128), // Gray.
        _ => Color.FromRgb(100, 116, 139),                  // Slate (unconfigured).
    };

    /// <summary>
    /// Draw the 16x16 battery on a 16-pixel grid at twice the size, so trays
    /// that show icons larger than 16 px (KDE, high DPI) still get sharp edges.
    /// </summary>
    private static RenderTargetBitmap DrawBatteryBitmap(Color fill, int percent)
    {
        const int scale = 2;
        var bitmap = new RenderTargetBitmap(new PixelSize(16 * scale, 16 * scale));
        using var context = bitmap.CreateDrawingContext(true);

        void FillCells(IBrush brush, int x, int y, int width, int height) =>
            context.FillRectangle(brush, new Rect(x * scale, y * scale, width * scale, height * scale));

        // Battery body outline (13 x 9 cells, one cell thick) plus the terminal nub on the right.
        var outline = new SolidColorBrush(Color.FromRgb(235, 235, 235));
        FillCells(outline, 0, 4, 13, 1);
        FillCells(outline, 0, 12, 13, 1);
        FillCells(outline, 0, 4, 1, 9);
        FillCells(outline, 12, 4, 1, 9);
        FillCells(outline, 13, 6, 2, 4);

        // Fill level inside the body (interior is 11 x 7 cells). Unknown level (-1)
        // renders as a full bar in the state colour.
        var level = percent < 0 ? 11 : (int)Math.Round(Math.Clamp(percent, 0, 100) / 100.0 * 11);
        if (percent > 0 && level == 0)
            level = 1;
        if (level > 0)
            FillCells(new SolidColorBrush(fill), 1, 5, level, 7);

        return bitmap;
    }

    private void FireAndForget(Task task, string operation) =>
        task.ContinueWith(
            t => _log.Error($"{operation} failed: {t.Exception?.GetBaseException()}"),
            TaskContinuationOptions.OnlyOnFaulted);
}
