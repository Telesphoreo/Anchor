using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Anchor;

/// <summary>
/// Live device/policy status window. Refreshed by pushed snapshots from the
/// tray context and by a 1 s UI timer (for relative timestamps and the
/// countdown display).
/// </summary>
public sealed class StatusWindow : Window
{
    private readonly DispatcherTimer _refreshTimer;
    private readonly Grid _table;

    private readonly TextBlock _model;
    private readonly TextBlock _name;
    private readonly TextBlock _mac;
    private readonly TextBlock _connection;
    private readonly TextBlock _battery;
    private readonly TextBlock _acIn;
    private readonly TextBlock _acOut;
    private readonly TextBlock _powerOut;
    private readonly TextBlock _temperature;
    private readonly TextBlock _chargeLimitCaption;
    private readonly TextBlock _chargeLimit;
    private readonly TextBlock _dischargeCutoffCaption;
    private readonly TextBlock _dischargeCutoff;
    private readonly TextBlock _serial;
    private readonly TextBlock _lastUpdate;
    private readonly TextBlock _floor;
    private readonly TextBlock _policyState;
    private readonly TextBlock _countdown;

    private UpsStatus _status;

    public StatusWindow(UpsStatus initial)
    {
        _status = initial;

        Title = "Anchor Status";
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        CanMinimize = false;
        CanMaximize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _table = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto"),
            Margin = new Thickness(20),
        };

        _model = AddRow("Model").Value;
        _name = AddRow("Name").Value;
        _mac = AddRow("MAC").Value;
        _connection = AddRow("Connection").Value;
        _battery = AddRow("Battery").Value;
        _acIn = AddRow("AC input").Value;
        _acOut = AddRow("AC output").Value;
        _powerOut = AddRow("Total output").Value;
        _temperature = AddRow("Temperature").Value;
        (_chargeLimitCaption, _chargeLimit) = AddRow("Charge limit");
        (_dischargeCutoffCaption, _dischargeCutoff) = AddRow("Discharge cutoff");
        _serial = AddRow("Serial").Value;
        _lastUpdate = AddRow("Last update").Value;
        _floor = AddRow("Shutdown floor").Value;
        // The raised-floor explanation can get long: wrap instead of widening the window.
        _floor.MaxWidth = 320;
        _floor.TextWrapping = TextWrapping.Wrap;
        _policyState = AddRow("Policy state").Value;
        _countdown = AddRow("Countdown").Value;

        Content = _table;

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += (_, _) => RefreshLabels();
        _refreshTimer.Start();
        Closed += (_, _) => _refreshTimer.Stop();
        RefreshLabels();
    }

    /// <summary>Push a fresh snapshot (called on the UI thread).</summary>
    public void UpdateStatus(UpsStatus status)
    {
        _status = status;
        RefreshLabels();
    }

    private (TextBlock Caption, TextBlock Value) AddRow(string caption)
    {
        var row = _table.RowDefinitions.Count;
        _table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        var captionText = new TextBlock
        {
            Text = caption + ":",
            Opacity = 0.65,
            Margin = new Thickness(0, 4, 20, 4),
        };
        var valueText = new TextBlock
        {
            Text = "n/a",
            Margin = new Thickness(0, 4, 0, 4),
        };
        Grid.SetRow(captionText, row);
        Grid.SetRow(valueText, row);
        Grid.SetColumn(valueText, 1);
        _table.Children.Add(captionText);
        _table.Children.Add(valueText);
        return (captionText, valueText);
    }

    private void RefreshLabels()
    {
        var status = _status;
        _model.Text = status.ModelName;
        _name.Text = status.DeviceName;
        _mac.Text = string.IsNullOrEmpty(status.DeviceMac) ? "Not configured" : status.DeviceMac;
        _connection.Text = ConnectionText(status);
        _battery.Text = IntText(status.BatteryPercent, "%");
        _acIn.Text = IntText(status.AcPowerIn, " W");
        _acOut.Text = IntText(status.AcPowerOut, " W");
        _powerOut.Text = IntText(status.PowerOut, " W");
        _temperature.Text = IntText(status.Temperature, " °C");
        SetLimitRow(_chargeLimitCaption, _chargeLimit, status.MaxChargeLimit);
        SetLimitRow(_dischargeCutoffCaption, _dischargeCutoff, status.MinDischargeLimit);
        _serial.Text = status.SerialNumber;
        _lastUpdate.Text = RelativeTime(status.LastUpdate);
        _floor.Text = FloorText(status);
        _policyState.Text = PolicyText(status.PolicyState);
        _countdown.Text = status.PolicyState == PolicyState.Pending && status.ShutdownDeadline is { } deadline
            ? $"{Math.Max(0, (int)(deadline - DateTimeOffset.Now).TotalSeconds)} s"
            : "n/a";
    }

    /// <summary>Show a limit row only when the station actually reported the value.</summary>
    private static void SetLimitRow(TextBlock caption, TextBlock value, int percent)
    {
        var reported = percent != -1;
        if (reported)
            value.Text = percent + "%";
        caption.IsVisible = reported;
        value.IsVisible = reported;
    }

    private static string FloorText(UpsStatus status) =>
        status.EffectiveFloorPercent != status.ConfiguredFloorPercent
            ? $"{status.EffectiveFloorPercent}% (raised from {status.ConfiguredFloorPercent}%; "
              + $"station cuts output at {status.MinDischargeLimit}%)"
            : $"{status.ConfiguredFloorPercent}%";

    private static string ConnectionText(UpsStatus status)
    {
        if (!status.Connected)
            return "Disconnected";
        if (!status.Negotiated)
            return "Connected";
        return status.Receiving ? "Receiving" : "Negotiated";
    }

    private static string IntText(int value, string unit) => value < 0 ? "Unknown" : value + unit;

    private static string RelativeTime(DateTimeOffset? timestamp)
    {
        if (timestamp is null)
            return "Never";
        var elapsed = DateTimeOffset.Now - timestamp.Value;
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;
        if (elapsed.TotalSeconds < 60)
            return $"{(int)elapsed.TotalSeconds} s ago";
        if (elapsed.TotalMinutes < 60)
            return $"{(int)elapsed.TotalMinutes} min ago";
        return $"{(int)elapsed.TotalHours} h ago";
    }

    private static string PolicyText(PolicyState state) => state switch
    {
        PolicyState.Unconfigured => "Not configured",
        PolicyState.OnGrid => "On grid",
        PolicyState.OnBattery => "On battery",
        PolicyState.Pending => "Shutdown pending",
        PolicyState.ShutdownIssued => "Shutdown issued",
        _ => state.ToString(),
    };
}
