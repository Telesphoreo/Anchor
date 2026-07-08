using SolixBle;

namespace Anchor;

/// <summary>
/// Settings window: device selection (BLE scan or manual MAC), model choice,
/// shutdown policy numbers, dry run, and the "start with Windows" Run-key entry.
/// Layout is fully auto-sized (no absolute coordinates) so it renders correctly
/// at any DPI scale under PerMonitorV2.
/// </summary>
public sealed class SettingsForm : Form
{
    private static readonly SolixDeviceModel[] SelectableModels =
    [
        SolixDeviceModel.C1000,
        SolixDeviceModel.C1000G2,
        SolixDeviceModel.C300,
        SolixDeviceModel.C800,
        SolixDeviceModel.F2000,
        SolixDeviceModel.F3800,
    ];

    private readonly FileLog _log;
    private readonly Action<AppConfig> _onSaved;

    private readonly Button _scanButton;
    private readonly Label _scanStatus;
    private readonly Label _scanGuidance;
    private readonly ListView _deviceList;
    private readonly TextBox _macBox;
    private readonly ComboBox _modelBox;
    private readonly NumericUpDown _floorBox;
    private readonly NumericUpDown _countdownBox;
    private readonly NumericUpDown _debounceBox;
    private readonly CheckBox _dryRunBox;
    private readonly CheckBox _startupBox;

    public SettingsForm(AppConfig config, FileLog log, Action<AppConfig> onSaved)
    {
        _log = log;
        _onSaved = onSaved;

        Text = "Anchor Settings";
        AutoScaleMode = AutoScaleMode.Font;
        AutoScaleDimensions = new SizeF(7F, 15F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        // --- Device group -------------------------------------------------
        var deviceTable = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 5,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
        };
        deviceTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        deviceTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        deviceTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        deviceTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        deviceTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        deviceTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        deviceTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _scanButton = new Button
        {
            Text = "Scan",
            AutoSize = true,
            MinimumSize = new Size(100, 28),
            Margin = new Padding(0, 0, 8, 0),
        };
        _scanButton.Click += OnScanClick;

        _scanStatus = new Label
        {
            Text = "",
            AutoSize = true,
            // Anchor None = vertically centered against the button in the flow row.
            Anchor = AnchorStyles.None,
        };

        var scanRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 6),
        };
        scanRow.Controls.Add(_scanButton);
        scanRow.Controls.Add(_scanStatus);

        _deviceList = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            Dock = DockStyle.Fill,
            MinimumSize = new Size(560, 260),
            Margin = new Padding(0, 0, 0, 6),
        };
        _deviceList.Columns.Add("Name", 230);
        _deviceList.Columns.Add("MAC", 170);
        _deviceList.Columns.Add("RSSI", -2); // Last column fills the remaining width.

        _scanGuidance = new Label
        {
            Text = "No Solix devices found. On the power station, press the Bluetooth/IoT button — "
                   + "setting up Wi-Fi turns Bluetooth off. If it still doesn't appear, power-cycle "
                   + "the station and close the Anker mobile app.",
            AutoSize = true,
            MaximumSize = new Size(560, 0), // Wrap at the list width (scaled with DPI).
            Visible = false,
            Margin = new Padding(0, 0, 0, 6),
        };

        var macLabel = new Label
        {
            Text = "MAC address:",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, 8, 0),
        };
        _macBox = new TextBox
        {
            MinimumSize = new Size(170, 0),
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 3, 0, 3),
            Text = config.DeviceMac,
        };
        _deviceList.SelectedIndexChanged += (_, _) =>
        {
            if (_deviceList.SelectedItems.Count > 0)
                _macBox.Text = _deviceList.SelectedItems[0].SubItems[1].Text;
        };

        var modelLabel = new Label
        {
            Text = "Model:",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, 8, 0),
        };
        _modelBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            MinimumSize = new Size(150, 0),
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 3, 0, 3),
        };
        foreach (var model in SelectableModels)
            _modelBox.Items.Add(model);
        _modelBox.SelectedItem = config.ParseModel();

        deviceTable.Controls.Add(scanRow, 0, 0);
        deviceTable.SetColumnSpan(scanRow, 2);
        deviceTable.Controls.Add(_deviceList, 0, 1);
        deviceTable.SetColumnSpan(_deviceList, 2);
        deviceTable.Controls.Add(_scanGuidance, 0, 2);
        deviceTable.SetColumnSpan(_scanGuidance, 2);
        deviceTable.Controls.Add(macLabel, 0, 3);
        deviceTable.Controls.Add(_macBox, 1, 3);
        deviceTable.Controls.Add(modelLabel, 0, 4);
        deviceTable.Controls.Add(_modelBox, 1, 4);

        var deviceGroup = new GroupBox
        {
            Text = "Device",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 6, 10, 8),
            Margin = new Padding(0, 0, 0, 8),
        };
        deviceGroup.Controls.Add(deviceTable);

        // --- Shutdown policy group ----------------------------------------
        var policyTable = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 4,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
        };
        policyTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        policyTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 4; i++)
            policyTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _floorBox = MakeNumericBox(1, 95, config.BatteryFloorPercent);
        _countdownBox = MakeNumericBox(0, 600, config.CountdownSeconds);
        _debounceBox = MakeNumericBox(1, 10, config.DebounceSamples);

        _dryRunBox = new CheckBox
        {
            Text = "Dry run (log and notify instead of shutting down)",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 6, 0, 0),
            Checked = config.DryRun,
        };

        policyTable.Controls.Add(MakeFieldLabel("Battery floor (%):"), 0, 0);
        policyTable.Controls.Add(_floorBox, 1, 0);
        policyTable.Controls.Add(MakeFieldLabel("Countdown (seconds):"), 0, 1);
        policyTable.Controls.Add(_countdownBox, 1, 1);
        policyTable.Controls.Add(MakeFieldLabel("Debounce (samples):"), 0, 2);
        policyTable.Controls.Add(_debounceBox, 1, 2);
        policyTable.Controls.Add(_dryRunBox, 0, 3);
        policyTable.SetColumnSpan(_dryRunBox, 2);

        var policyGroup = new GroupBox
        {
            Text = "Shutdown policy",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 6, 10, 8),
            Margin = new Padding(0, 0, 0, 8),
        };
        policyGroup.Controls.Add(policyTable);

        // --- Startup + buttons ---------------------------------------------
        _startupBox = new CheckBox
        {
            Text = "Start Anchor with Windows",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(4, 0, 0, 8),
            Checked = StartupRegistration.IsEnabled(),
        };

        var saveButton = new Button
        {
            Text = "Save",
            AutoSize = true,
            MinimumSize = new Size(88, 30),
        };
        saveButton.Click += OnSaveClick;

        var cancelButton = new Button
        {
            Text = "Cancel",
            AutoSize = true,
            MinimumSize = new Size(88, 30),
        };
        cancelButton.Click += (_, _) => Close();

        AcceptButton = saveButton;
        CancelButton = cancelButton;

        var buttonRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0),
        };
        buttonRow.Controls.Add(cancelButton); // Rightmost.
        buttonRow.Controls.Add(saveButton);

        var root = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 4,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
        };
        for (var i = 0; i < 4; i++)
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(deviceGroup, 0, 0);
        root.Controls.Add(policyGroup, 0, 1);
        root.Controls.Add(_startupBox, 0, 2);
        root.Controls.Add(buttonRow, 0, 3);

        Controls.Add(root);
    }

    private static Label MakeFieldLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(0, 0, 8, 0),
    };

    private static NumericUpDown MakeNumericBox(int min, int max, int value) => new()
    {
        Minimum = min,
        Maximum = max,
        Value = Math.Clamp(value, min, max),
        MinimumSize = new Size(80, 0),
        Anchor = AnchorStyles.Left,
        Margin = new Padding(0, 3, 0, 3),
    };

    private async void OnScanClick(object? sender, EventArgs e)
    {
        _scanButton.Enabled = false;
        _scanGuidance.Visible = false;
        _scanStatus.Text = "Checking Bluetooth…";
        try
        {
            var bluetooth = await SolixScanner.GetBluetoothStatusAsync();
            if (IsDisposed)
                return;

            if (bluetooth != BluetoothStatus.Ready)
            {
                _scanStatus.Text = bluetooth == BluetoothStatus.RadioOff
                    ? "Bluetooth is turned off — enable it in Windows settings."
                    : "No Bluetooth adapter found.";
                return;
            }

            _scanStatus.Text = "Scanning…";
            var devices = await SolixScanner.DiscoverDevicesAsync(TimeSpan.FromSeconds(10));
            if (IsDisposed)
                return;

            _deviceList.Items.Clear();
            foreach (var device in devices.OrderByDescending(d => d.Rssi))
            {
                var name = string.IsNullOrEmpty(device.Name) ? "(unnamed)" : device.Name;
                _deviceList.Items.Add(new ListViewItem([name, device.Mac, device.Rssi.ToString()]));
            }

            if (devices.Count == 0)
            {
                _scanStatus.Text = "";
                _scanGuidance.Visible = true;
            }
            else
            {
                _scanStatus.Text = $"Found {devices.Count} device(s).";
            }
        }
        catch (Exception ex)
        {
            _log.Error($"BLE scan failed: {ex}");
            if (!IsDisposed)
                _scanStatus.Text = "Scan failed — see log.";
        }
        finally
        {
            if (!IsDisposed)
                _scanButton.Enabled = true;
        }
    }

    private void OnSaveClick(object? sender, EventArgs e)
    {
        var macInput = _macBox.Text.Trim();
        var mac = "";
        if (macInput.Length > 0 && !TryNormalizeMac(macInput, out mac))
        {
            MessageBox.Show(this,
                "The MAC address must be 6 hex byte pairs, e.g. AA:BB:CC:DD:EE:FF.",
                "Invalid MAC address", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var config = new AppConfig
        {
            DeviceMac = mac,
            DeviceModel = ((SolixDeviceModel)_modelBox.SelectedItem!).ToString(),
            BatteryFloorPercent = (int)_floorBox.Value,
            CountdownSeconds = (int)_countdownBox.Value,
            DebounceSamples = (int)_debounceBox.Value,
            DryRun = _dryRunBox.Checked,
        };
        config.Save(_log);
        StartupRegistration.SetEnabled(_startupBox.Checked, _log);
        _onSaved(config);
        Close();
    }

    private static bool TryNormalizeMac(string input, out string mac)
    {
        mac = "";
        var hex = input.Replace(":", "").Replace("-", "");
        if (hex.Length != 12 || !hex.All(Uri.IsHexDigit))
            return false;
        mac = string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2).ToUpperInvariant()));
        return true;
    }

}
