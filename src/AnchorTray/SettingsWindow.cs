using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using SolixBle;

namespace Anchor;

/// <summary>
/// Settings window: device selection (BLE scan or manual MAC), model choice,
/// shutdown policy numbers, dry run, and the "start at login" entry.
/// </summary>
public sealed class SettingsWindow : Window
{
    private static readonly SolixDeviceModel[] SelectableModels =
    [
        SolixDeviceModel.C1000,
        SolixDeviceModel.C1000G2,
        SolixDeviceModel.C300,
        SolixDeviceModel.C800,
        SolixDeviceModel.F2000,
        SolixDeviceModel.F2600,
        SolixDeviceModel.F3800,
    ];

    /// <summary>Widths of the Name, MAC and RSSI columns of the device list.</summary>
    private const string DeviceColumns = "260,220,*";

    private sealed record DeviceRow(string Name, string Mac, string Rssi, DiscoveredDevice Device);

    private readonly FileLog _log;
    private readonly Action<AppConfig> _onSaved;
    private readonly string _priorMac;
    private readonly string _priorName;

    private readonly Button _scanButton;
    private readonly TextBlock _scanStatus;
    private readonly TextBlock _scanGuidance;
    private readonly ListBox _deviceList;
    private readonly TextBox _macBox;
    private readonly ComboBox _modelBox;
    private readonly NumericUpDown _floorBox;
    private readonly NumericUpDown _countdownBox;
    private readonly NumericUpDown _debounceBox;
    private readonly CheckBox _dryRunBox;
    private readonly CheckBox _startupBox;
    private bool _closed;

    public SettingsWindow(AppConfig config, FileLog log, Action<AppConfig> onSaved)
    {
        _log = log;
        _onSaved = onSaved;
        _priorMac = config.DeviceMac;
        _priorName = config.DeviceName;

        Title = "Anchor Settings";
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        CanMinimize = false;
        CanMaximize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Closed += (_, _) => _closed = true;

        // --- Device group -------------------------------------------------
        _scanButton = new Button
        {
            Content = "Scan",
            MinWidth = 100,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        _scanButton.Click += OnScanClick;

        _scanStatus = new TextBlock { VerticalAlignment = VerticalAlignment.Center };

        var scanRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 0, 0, 6),
            Children = { _scanButton, _scanStatus },
        };

        var listHeader = MakeDeviceRow("Name", "MAC", "RSSI");
        listHeader.Margin = new Thickness(12, 0, 12, 2);
        listHeader.Opacity = 0.65;

        _deviceList = new ListBox
        {
            Width = 620,
            Height = 280,
            SelectionMode = SelectionMode.Single,
            Margin = new Thickness(0, 0, 0, 8),
            ItemTemplate = new FuncDataTemplate<DeviceRow>((row, _) =>
                row is null ? null : MakeDeviceRow(row.Name, row.Mac, row.Rssi)),
        };

        _scanGuidance = new TextBlock
        {
            Text = "No Solix devices found. On the power station, press the Bluetooth/IoT button; "
                   + "setting up Wi-Fi turns Bluetooth off. If it still doesn't appear, power-cycle "
                   + "the station and close the Anker mobile app.",
            MaxWidth = 620,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
            Margin = new Thickness(0, 0, 0, 6),
        };

        _macBox = new TextBox
        {
            MinWidth = 220,
            HorizontalAlignment = HorizontalAlignment.Left,
            Text = config.DeviceMac,
        };

        _modelBox = new ComboBox
        {
            MinWidth = 150,
            ItemsSource = SelectableModels,
            SelectedItem = config.ParseModel(),
        };

        _deviceList.SelectionChanged += (_, _) =>
        {
            if (_deviceList.SelectedItem is not DeviceRow selected)
                return;
            _macBox.Text = selected.Mac;

            // The advertised product type (or name) identifies the model on newer firmware.
            if (selected.Device is { SuggestedModel: { } model } && SelectableModels.Contains(model))
                _modelBox.SelectedItem = model;
        };

        var deviceFields = MakeFieldGrid(
            ("MAC address:", _macBox),
            ("Model:", _modelBox));

        var deviceGroup = MakeGroup("Device", new StackPanel
        {
            Children = { scanRow, listHeader, _deviceList, _scanGuidance, deviceFields },
        });

        // --- Shutdown policy group ----------------------------------------
        _floorBox = MakeNumericBox(1, 95, config.BatteryFloorPercent);
        _countdownBox = MakeNumericBox(0, 600, config.CountdownSeconds);
        _debounceBox = MakeNumericBox(1, 10, config.DebounceSamples);

        _dryRunBox = new CheckBox
        {
            Content = "Dry run (log and notify instead of shutting down)",
            Margin = new Thickness(0, 6, 0, 0),
            IsChecked = config.DryRun,
        };

        var policyGroup = MakeGroup("Shutdown policy", new StackPanel
        {
            Children =
            {
                MakeFieldGrid(
                    ("Battery floor (%):", _floorBox),
                    ("Countdown (seconds):", _countdownBox),
                    ("Debounce (samples):", _debounceBox)),
                _dryRunBox,
            },
        });

        // --- Startup + buttons ---------------------------------------------
        _startupBox = new CheckBox
        {
            Content = "Start Anchor at login",
            Margin = new Thickness(4, 0, 0, 8),
            IsChecked = StartupRegistration.IsEnabled(),
        };

        var saveButton = new Button
        {
            Content = "Save",
            MinWidth = 88,
            IsDefault = true,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        saveButton.Click += OnSaveClick;

        var cancelButton = new Button
        {
            Content = "Cancel",
            MinWidth = 88,
            IsCancel = true,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        cancelButton.Click += (_, _) => Close();

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { saveButton, cancelButton },
        };

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Children = { deviceGroup, policyGroup, _startupBox, buttonRow },
        };
    }

    private static Grid MakeDeviceRow(string name, string mac, string rssi)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(DeviceColumns) };
        string[] cells = [name, mac, rssi];
        for (var i = 0; i < cells.Length; i++)
        {
            var text = new TextBlock { Text = cells[i], TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(text, i);
            grid.Children.Add(text);
        }

        return grid;
    }

    private static Control MakeGroup(string header, Control content) => new Border
    {
        BorderBrush = Brushes.Gray,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(14, 10, 14, 12),
        Margin = new Thickness(0, 0, 0, 10),
        Child = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = header, FontWeight = FontWeight.SemiBold },
                content,
            },
        },
    };

    /// <summary>Label/field pairs in two auto-sized columns.</summary>
    private static Grid MakeFieldGrid(params (string Label, Control Field)[] rows)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        for (var row = 0; row < rows.Length; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var label = new TextBlock
            {
                Text = rows[row].Label,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            Grid.SetRow(label, row);
            grid.Children.Add(label);

            var field = rows[row].Field;
            field.Margin = new Thickness(0, 3, 0, 3);
            field.HorizontalAlignment = HorizontalAlignment.Left;
            Grid.SetRow(field, row);
            Grid.SetColumn(field, 1);
            grid.Children.Add(field);
        }

        return grid;
    }

    private static NumericUpDown MakeNumericBox(int min, int max, int value) => new()
    {
        Minimum = min,
        Maximum = max,
        Increment = 1,
        FormatString = "0",
        Value = Math.Clamp(value, min, max),
        Tag = Math.Clamp(value, min, max),
        MinWidth = 120,
    };

    /// <summary>The box's whole-number value; an emptied box keeps the value it opened with.</summary>
    private static int ReadNumber(NumericUpDown box) =>
        box.Value is { } value
            ? (int)Math.Clamp(Math.Round(value), box.Minimum, box.Maximum)
            : (int)box.Tag!;

    private async void OnScanClick(object? sender, EventArgs e)
    {
        _scanButton.IsEnabled = false;
        _scanGuidance.IsVisible = false;
        _scanStatus.Text = "Checking Bluetooth…";
        try
        {
            var bluetooth = await SolixScanner.GetBluetoothStatusAsync();
            if (_closed)
                return;

            if (bluetooth != BluetoothStatus.Ready)
            {
                _scanStatus.Text = bluetooth == BluetoothStatus.RadioOff
                    ? PlatformInfo.BluetoothOffMessage
                    : "No Bluetooth adapter found.";
                return;
            }

            _scanStatus.Text = "Scanning…";
            var devices = await SolixScanner.DiscoverDevicesAsync(TimeSpan.FromSeconds(10));
            if (_closed)
                return;

            _deviceList.ItemsSource = devices
                .OrderByDescending(d => d.Rssi)
                .Select(device => new DeviceRow(
                    string.IsNullOrEmpty(device.Name) ? "(unnamed)" : device.Name,
                    device.Mac,
                    device.Rssi.ToString(),
                    device))
                .ToList();

            if (devices.Count == 0)
            {
                _scanStatus.Text = "";
                _scanGuidance.IsVisible = true;
            }
            else
            {
                _scanStatus.Text = $"Found {devices.Count} device(s).";
            }
        }
        catch (Exception ex)
        {
            _log.Error($"BLE scan failed: {ex}");
            if (!_closed)
                _scanStatus.Text = "Scan failed; see log.";
        }
        finally
        {
            if (!_closed)
                _scanButton.IsEnabled = true;
        }
    }

    private async void OnSaveClick(object? sender, EventArgs e)
    {
        var macInput = (_macBox.Text ?? "").Trim();
        var mac = "";
        if (macInput.Length > 0 && !TryNormalizeMac(macInput, out mac))
        {
            await MessageDialog.ShowAsync(this, "Invalid MAC address",
                "The MAC address must be 6 hex byte pairs, e.g. AA:BB:CC:DD:EE:FF.");
            return;
        }

        var config = new AppConfig
        {
            DeviceMac = mac,
            DeviceName = ResolveDeviceName(mac),
            DeviceModel = ((SolixDeviceModel)_modelBox.SelectedItem!).ToString(),
            BatteryFloorPercent = ReadNumber(_floorBox),
            CountdownSeconds = ReadNumber(_countdownBox),
            DebounceSamples = ReadNumber(_debounceBox),
            DryRun = _dryRunBox.IsChecked == true,
        };
        if (!config.Save(_log))
        {
            await MessageDialog.ShowAsync(this, "Could not save settings",
                "Anchor could not save the configuration. Your current settings are still active. " +
                "Check the log for details and try again.");
            return;
        }

        StartupRegistration.SetEnabled(_startupBox.IsChecked == true, _log);
        _onSaved(config);
        Close();
    }

    /// <summary>
    /// The saved device name: the scan-list name for the saved MAC when one is
    /// on screen, else the previously saved name as long as the MAC is unchanged.
    /// </summary>
    private string ResolveDeviceName(string mac)
    {
        foreach (var row in _deviceList.ItemsSource?.OfType<DeviceRow>() ?? [])
        {
            if (!string.Equals(row.Mac, mac, StringComparison.OrdinalIgnoreCase))
                continue;
            return row.Name == "(unnamed)" ? "" : row.Name;
        }

        return string.Equals(mac, _priorMac, StringComparison.OrdinalIgnoreCase) ? _priorName : "";
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
