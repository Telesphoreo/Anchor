using System.Runtime.InteropServices;

namespace Anchor;

/// <summary>
/// Tray UI: NotifyIcon with programmatically drawn battery icons, balloon tips
/// on policy transitions, and the Status/Settings/Countdown windows. All
/// monitor events are marshaled onto the UI thread via the captured
/// SynchronizationContext.
/// </summary>
public sealed class TrayAppContext : ApplicationContext
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
    private readonly UpsMonitor _monitor;
    private readonly SynchronizationContext _sync;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _cancelMenuItem;
    private readonly ToolStripMenuItem _installUpdateItem;
    private readonly ToolStripMenuItem _checkUpdatesItem;
    private readonly Dictionary<(IconKind Kind, int Bucket), Icon> _iconCache = new();

    private AppConfig _config;
    private StatusForm? _statusForm;
    private SettingsForm? _settingsForm;
    private CountdownForm? _countdownForm;
    private string _lastTooltip = "";
    private (IconKind Kind, int Bucket)? _lastIconKey;
    private bool _powerBalloonSeen;
    private bool _exiting;
    private UpdateInfo? _availableUpdate;
    private bool _updateBusy;
    private bool _updateBalloonActive;

    public TrayAppContext(AppConfig config, FileLog log)
    {
        _log = log;
        _config = config;
        _sync = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        var menu = new ContextMenuStrip();
        var statusItem = new ToolStripMenuItem("Status…", null, (_, _) => OpenStatus());
        statusItem.Font = new Font(statusItem.Font, FontStyle.Bold);
        _cancelMenuItem = new ToolStripMenuItem("Cancel shutdown", null, (_, _) => CancelShutdown())
        {
            Enabled = false,
        };
        _installUpdateItem = new ToolStripMenuItem("Update available…", null, OnInstallUpdate)
        {
            Visible = false,
        };
        _checkUpdatesItem = new ToolStripMenuItem("Check for updates…", null, OnCheckForUpdates);
        menu.Items.Add(statusItem);
        menu.Items.Add(new ToolStripMenuItem("Settings…", null, (_, _) => OpenSettings()));
        menu.Items.Add(_cancelMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_installUpdateItem);
        menu.Items.Add(_checkUpdatesItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, OnExit));

        _icon = new NotifyIcon
        {
            Icon = GetIcon(IconKind.Unconfigured, -1),
            Text = "Anchor",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => OpenStatus();
        _icon.BalloonTipClicked += OnBalloonClicked;

        _monitor = new UpsMonitor(config, log);
        _monitor.StatusChanged += status => _sync.Post(_ => OnStatusUi(status), null);
        _monitor.EffectOccurred += effect => _sync.Post(_ => OnEffectUi(effect), null);

        FireAndForget(_monitor.ApplySettingsAsync(config), "Starting monitor");
        FireAndForget(Task.Run(StartupUpdateCheckAsync), "Update check");

        if (!config.Configured)
        {
            _sync.Post(_ =>
            {
                ShowBalloon(ToolTipIcon.Info, "Anchor is not configured",
                    "Select your power station in Settings.");
                OpenSettings();
            }, null);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _icon.Dispose();
            foreach (var icon in _iconCache.Values)
                icon.Dispose();
            _iconCache.Clear();
        }

        base.Dispose(disposing);
    }

    private async void OnExit(object? sender, EventArgs e)
    {
        if (_exiting)
            return;
        _exiting = true;
        _icon.Visible = false;

        // Close any open child windows so their timers stop before teardown.
        _statusForm?.Close();
        _settingsForm?.Close();
        CloseCountdownForm();

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
        ExitThread();
    }

    private void OnStatusUi(UpsStatus status)
    {
        if (_exiting)
            return;

        UpdateIcon(status);
        UpdateTooltip(status);
        _cancelMenuItem.Enabled = status.PolicyState == PolicyState.Pending;
        _statusForm?.UpdateStatus(status);

        if (status.PolicyState == PolicyState.Pending && status.ShutdownDeadline is { } deadline)
            _countdownForm?.UpdateDeadline(deadline);
        else
            CloseCountdownForm();
    }

    private void OnEffectUi(PolicyEffect effect)
    {
        if (_exiting)
            return;

        switch (effect)
        {
            case PolicyEffect.EnteredBattery:
                _powerBalloonSeen = true;
                ShowBalloon(ToolTipIcon.Warning, "Wall power lost",
                    "The power station is running on battery.");
                break;

            case PolicyEffect.EnteredGrid:
                // Suppress the balloon for the very first grid state after start;
                // it is not a "restore", just the initial reading.
                if (_powerBalloonSeen)
                    ShowBalloon(ToolTipIcon.Info, "Wall power restored",
                        "The power station is back on grid power.");
                _powerBalloonSeen = true;
                break;

            case PolicyEffect.CountdownStarted:
                ShowBalloon(ToolTipIcon.Warning, "Shutdown countdown started",
                    $"Battery at or below {_config.BatteryFloorPercent}% on battery power.");
                ShowCountdownForm();
                break;

            case PolicyEffect.CountdownCancelledAcRestored:
                ShowBalloon(ToolTipIcon.Info, "Shutdown cancelled",
                    "AC power was restored during the countdown.");
                break;

            case PolicyEffect.CountdownCancelledReconfigured:
                CloseCountdownForm();
                ShowBalloon(ToolTipIcon.Info, "Shutdown cancelled",
                    "The monitored device was reconfigured during the countdown.");
                break;

            case PolicyEffect.ShutdownNow:
                OnShutdownNow();
                break;

            case PolicyEffect.BecameUnavailable:
                ShowBalloon(ToolTipIcon.Warning, "Device unavailable",
                    "No fresh data from the power station.");
                break;

            case PolicyEffect.BecameAvailable:
                ShowBalloon(ToolTipIcon.Info, "Device connected",
                    "Receiving data from the power station.");
                break;

            case PolicyEffect.FloorRaisedByDeviceLimit:
                var floor = _monitor.GetSnapshot().EffectiveFloorPercent;
                ShowBalloon(ToolTipIcon.Info, "Shutdown floor raised",
                    $"Shutdown floor raised to {floor}% to stay above the station's discharge cutoff.");
                break;
        }
    }

    private void OnShutdownNow()
    {
        CloseCountdownForm();
        if (_config.DryRun)
        {
            _log.Info("[DRY RUN] Battery at floor on battery power — Windows shutdown would be initiated now.");
            ShowBalloon(ToolTipIcon.Warning, "Dry run: shutdown suppressed",
                "Windows shutdown would have been initiated now.");
            return;
        }

        _log.Warning("Battery at floor on battery power — initiating Windows shutdown.");
        ShowBalloon(ToolTipIcon.Error, "Shutting down", "Power station battery is at the configured floor.");
        FireAndForget(ShutdownService.ShutdownAsync(_log), "System shutdown");
    }

    private void OpenStatus()
    {
        if (_statusForm is { IsDisposed: false })
        {
            _statusForm.Activate();
            return;
        }

        var form = new StatusForm(_monitor.GetSnapshot());
        form.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_statusForm, form))
                _statusForm = null;
        };
        _statusForm = form;
        form.Show();
    }

    private void OpenSettings()
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.Activate();
            return;
        }

        var form = new SettingsForm(_config, _log, OnSettingsSaved);
        form.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_settingsForm, form))
                _settingsForm = null;
        };
        _settingsForm = form;
        form.Show();
    }

    private void OnSettingsSaved(AppConfig config)
    {
        _config = config;
        FireAndForget(_monitor.ApplySettingsAsync(config), "Applying settings");
    }

    private void CancelShutdown()
    {
        _monitor.CancelCountdownByUser();
        CloseCountdownForm();
    }

    private void ShowCountdownForm()
    {
        var deadline = _monitor.GetSnapshot().ShutdownDeadline
                       ?? DateTimeOffset.Now.AddSeconds(_config.CountdownSeconds);
        if (_countdownForm is { IsDisposed: false })
        {
            _countdownForm.UpdateDeadline(deadline);
            _countdownForm.Activate();
            return;
        }

        var form = new CountdownForm(deadline, () => _monitor.CancelCountdownByUser());
        form.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_countdownForm, form))
                _countdownForm = null;
        };
        _countdownForm = form;
        form.Show();
        form.Activate();
    }

    private void CloseCountdownForm()
    {
        if (_countdownForm is { IsDisposed: false } form)
        {
            _countdownForm = null;
            form.Close();
        }
    }

    /// <summary>
    /// Background update check kicked off from the constructor; never throws.
    /// Marshals back to the UI thread like the monitor events do.
    /// </summary>
    private async Task StartupUpdateCheckAsync()
    {
        try
        {
            var info = await Updater.CheckAsync(_log).ConfigureAwait(false);
            if (info is { IsNewer: true })
                _sync.Post(_ => OnUpdateAvailableUi(info), null);
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
        _installUpdateItem.Text = $"Update available — install {info.Latest}…";
        _installUpdateItem.Visible = true;
        ShowBalloon(ToolTipIcon.Info, "Update available",
            $"Anchor {info.Latest} is available — click here or use the tray menu to update.");
        _updateBalloonActive = true;
    }

    private void OnBalloonClicked(object? sender, EventArgs e)
    {
        if (_updateBalloonActive)
            FireAndForget(InstallUpdateAsync(), "Installing update");
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
        _installUpdateItem.Enabled = false;
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
                ShowBalloon(ToolTipIcon.Warning, "Update failed",
                    "Could not download or launch the installer. See the log for details.");
        }
        finally
        {
            _updateBusy = false;
            _installUpdateItem.Enabled = true;
        }
    }

    private async void OnCheckForUpdates(object? sender, EventArgs e)
    {
        if (_updateBusy)
            return;

        _checkUpdatesItem.Enabled = false;
        try
        {
            var info = await Updater.CheckAsync(_log);
            if (_exiting)
                return;

            if (info is null)
                ShowBalloon(ToolTipIcon.Warning, "Update check failed",
                    "Could not reach GitHub to check for updates. See the log for details.");
            else if (info.IsNewer)
                OnUpdateAvailableUi(info);
            else
                ShowBalloon(ToolTipIcon.Info, "Up to date",
                    $"Anchor {info.Current.ToString(3)} is the latest version.");
        }
        catch (Exception ex)
        {
            _log.Error($"Update check failed: {ex}");
        }
        finally
        {
            _checkUpdatesItem.Enabled = true;
        }
    }

    private void ShowBalloon(ToolTipIcon icon, string title, string text)
    {
        _updateBalloonActive = false;
        _icon.ShowBalloonTip(5000, title, text, icon);
    }

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
        _icon.Text = text;
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

    /// <summary>
    /// Get (or lazily draw and cache) a 16x16 battery icon. Icons are created
    /// via GetHicon + Icon.FromHandle + Clone, and the original handle is
    /// released with DestroyIcon to avoid GDI handle leaks.
    /// </summary>
    private Icon GetIcon(IconKind kind, int percent)
    {
        var bucket = BatteryBucket(percent);
        var key = (kind, bucket);
        if (_iconCache.TryGetValue(key, out var cached))
            return cached;

        using var bitmap = DrawBatteryBitmap(GetFillColor(kind), bucket < 0 ? -1 : bucket * 10);
        var handle = bitmap.GetHicon();
        Icon icon;
        try
        {
            using var fromHandle = Icon.FromHandle(handle);
            icon = (Icon)fromHandle.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }

        _iconCache[key] = icon;
        return icon;
    }

    private static Color GetFillColor(IconKind kind) => kind switch
    {
        IconKind.OnGrid => Color.FromArgb(76, 175, 80),      // Green.
        IconKind.OnBattery => Color.FromArgb(255, 152, 0),   // Orange.
        IconKind.Pending => Color.FromArgb(229, 57, 53),     // Red.
        IconKind.Unavailable => Color.FromArgb(128, 128, 128), // Gray.
        _ => Color.FromArgb(100, 116, 139),                  // Slate (unconfigured).
    };

    private static Bitmap DrawBatteryBitmap(Color fill, int percent)
    {
        var bitmap = new Bitmap(16, 16);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);

        var outlineColor = Color.FromArgb(235, 235, 235);
        using var outlinePen = new Pen(outlineColor);
        using var outlineBrush = new SolidBrush(outlineColor);

        // Battery body outline plus the terminal nub on the right.
        graphics.DrawRectangle(outlinePen, 0, 4, 12, 8);
        graphics.FillRectangle(outlineBrush, 13, 6, 2, 4);

        // Fill level inside the body (interior is 11 x 7 px). Unknown level (-1)
        // renders as a full bar in the state colour.
        var level = percent < 0 ? 11 : (int)Math.Round(Math.Clamp(percent, 0, 100) / 100.0 * 11);
        if (percent > 0 && level == 0)
            level = 1;
        if (level > 0)
        {
            using var fillBrush = new SolidBrush(fill);
            graphics.FillRectangle(fillBrush, 1, 5, level, 7);
        }

        return bitmap;
    }

    private void FireAndForget(Task task, string operation) =>
        task.ContinueWith(
            t => _log.Error($"{operation} failed: {t.Exception?.GetBaseException()}"),
            TaskContinuationOptions.OnlyOnFaulted);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
