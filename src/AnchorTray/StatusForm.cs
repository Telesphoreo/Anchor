namespace Anchor;

/// <summary>
/// Live device/policy status window. Refreshed by pushed snapshots from the
/// tray context and by a 1 s UI timer (for relative timestamps and the
/// countdown display).
/// </summary>
public sealed class StatusForm : Form
{
    private readonly System.Windows.Forms.Timer _refreshTimer;

    private readonly Label _model;
    private readonly Label _name;
    private readonly Label _mac;
    private readonly Label _connection;
    private readonly Label _battery;
    private readonly Label _acIn;
    private readonly Label _acOut;
    private readonly Label _powerOut;
    private readonly Label _temperature;
    private readonly Label _chargeLimitCaption;
    private readonly Label _chargeLimit;
    private readonly Label _dischargeCutoffCaption;
    private readonly Label _dischargeCutoff;
    private readonly Label _serial;
    private readonly Label _lastUpdate;
    private readonly Label _floor;
    private readonly Label _policyState;
    private readonly Label _countdown;

    private UpsStatus _status;

    public StatusForm(UpsStatus initial)
    {
        _status = initial;

        Text = "Anchor Status";
        AutoScaleMode = AutoScaleMode.Font;
        AutoScaleDimensions = new SizeF(7F, 15F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var table = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(16),
        };

        _model = AddRow(table, "Model").Value;
        _name = AddRow(table, "Name").Value;
        _mac = AddRow(table, "MAC").Value;
        _connection = AddRow(table, "Connection").Value;
        _battery = AddRow(table, "Battery").Value;
        _acIn = AddRow(table, "AC input").Value;
        _acOut = AddRow(table, "AC output").Value;
        _powerOut = AddRow(table, "Total output").Value;
        _temperature = AddRow(table, "Temperature").Value;
        (_chargeLimitCaption, _chargeLimit) = AddRow(table, "Charge limit");
        (_dischargeCutoffCaption, _dischargeCutoff) = AddRow(table, "Discharge cutoff");
        _serial = AddRow(table, "Serial").Value;
        _lastUpdate = AddRow(table, "Last update").Value;
        _floor = AddRow(table, "Shutdown floor").Value;
        // The raised-floor explanation can get long: wrap instead of widening the form.
        _floor.MaximumSize = new Size(280, 0);
        _policyState = AddRow(table, "Policy state").Value;
        _countdown = AddRow(table, "Countdown").Value;

        Controls.Add(table);

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _refreshTimer.Tick += (_, _) => RefreshLabels();
        _refreshTimer.Start();
        RefreshLabels();
    }

    /// <summary>Push a fresh snapshot (called on the UI thread).</summary>
    public void UpdateStatus(UpsStatus status)
    {
        _status = status;
        RefreshLabels();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _refreshTimer.Dispose();
        base.Dispose(disposing);
    }

    private static (Label Caption, Label Value) AddRow(TableLayoutPanel table, string caption)
    {
        var captionLabel = new Label
        {
            Text = caption + ":",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 4, 16, 4),
        };
        var valueLabel = new Label
        {
            Text = "—",
            AutoSize = true,
            Margin = new Padding(0, 4, 0, 4),
        };
        table.Controls.Add(captionLabel);
        table.Controls.Add(valueLabel);
        return (captionLabel, valueLabel);
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
            : "—";
    }

    /// <summary>Show a limit row only when the station actually reported the value.</summary>
    private static void SetLimitRow(Label caption, Label value, int percent)
    {
        var reported = percent != -1;
        if (reported)
            value.Text = percent + "%";
        caption.Visible = reported;
        value.Visible = reported;
    }

    private static string FloorText(UpsStatus status) =>
        status.EffectiveFloorPercent != status.ConfiguredFloorPercent
            ? $"{status.EffectiveFloorPercent}% (raised from {status.ConfiguredFloorPercent}% — "
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
