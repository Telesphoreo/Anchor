using System.Globalization;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Radios;

namespace SolixBle;

/// <summary>State of the local Bluetooth stack, as reported by <see cref="SolixScanner.GetBluetoothStatusAsync"/>.</summary>
public enum BluetoothStatus
{
    /// <summary>A Bluetooth adapter is present and its radio is on.</summary>
    Ready,

    /// <summary>A Bluetooth adapter is present but its radio is switched off.</summary>
    RadioOff,

    /// <summary>No Bluetooth adapter is available (or its state could not be queried).</summary>
    NoAdapter,
}

/// <summary>
/// BLE discovery for Solix devices plus MAC address helpers
/// (port of SolixBLE <c>utilities.discover_devices</c>).
/// </summary>
public static class SolixScanner
{
    /// <summary>
    /// Report whether the local Bluetooth stack is usable for scanning:
    /// adapter present with the radio on, radio off, or no adapter at all.
    /// </summary>
    public static async Task<BluetoothStatus> GetBluetoothStatusAsync()
    {
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync().AsTask().ConfigureAwait(false);
            if (adapter is null)
                return BluetoothStatus.NoAdapter;

            var radio = await adapter.GetRadioAsync().AsTask().ConfigureAwait(false);
            return radio?.State == RadioState.On ? BluetoothStatus.Ready : BluetoothStatus.RadioOff;
        }
        catch (Exception ex)
        {
            SolixLog.Warning($"Could not query the Bluetooth adapter state: {ex.Message}");
            return BluetoothStatus.NoAdapter;
        }
    }

    /// <summary>
    /// Scans the BLE neighborhood for Solix BLE device(s) and returns a list of
    /// nearby devices based upon detection of a known advertised UUID.
    /// </summary>
    /// <param name="timeout">Time to scan for devices (default 5 s).</param>
    /// <param name="ct">Cancellation token aborting the scan.</param>
    public static async Task<IReadOnlyList<DiscoveredDevice>> DiscoverDevicesAsync(
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var found = new Dictionary<ulong, DiscoveredDevice>();
        var gate = new object();

        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active,
        };

        watcher.Received += (_, args) =>
        {
            SolixLog.Debug(
                $"Found generic BT device '{args.BluetoothAddress:X12}' with local name '{args.Advertisement.LocalName}'");

            var hasIdentifier = args.Advertisement.ServiceUuids.Contains(SolixConst.UuidIdentifier);

            lock (gate)
            {
                var name = args.Advertisement.LocalName ?? string.Empty;
                if (found.TryGetValue(args.BluetoothAddress, out var existing))
                {
                    // The local name may only arrive on a later advertisement or
                    // scan response (which may lack the service UUIDs) —
                    // update the record if a non-empty one shows up.
                    var newName = string.IsNullOrEmpty(name) ? existing.Name : name;
                    found[args.BluetoothAddress] = existing with
                    {
                        Name = newName,
                        Rssi = args.RawSignalStrengthInDBm,
                    };
                }
                else if (hasIdentifier)
                {
                    SolixLog.Debug($"Found Anker device '{args.BluetoothAddress:X12}' with local name '{name}'");
                    found[args.BluetoothAddress] = new DiscoveredDevice(
                        args.BluetoothAddress,
                        AddressToMac(args.BluetoothAddress),
                        name,
                        args.RawSignalStrengthInDBm);
                }
            }
        };

        watcher.Start();
        try
        {
            await Task.Delay(timeout ?? TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        }
        finally
        {
            watcher.Stop();
        }

        lock (gate)
        {
            return found.Values.ToList();
        }
    }

    /// <summary>Convert a MAC address string ("AA:BB:CC:DD:EE:FF") to a Bluetooth address.</summary>
    public static ulong MacToAddress(string mac)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mac);
        var hex = mac.Replace(":", string.Empty).Replace("-", string.Empty);
        if (hex.Length != 12)
            throw new FormatException($"'{mac}' is not a valid Bluetooth MAC address!");
        return ulong.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    /// <summary>Convert a Bluetooth address to a MAC address string ("AA:BB:CC:DD:EE:FF").</summary>
    public static string AddressToMac(ulong address)
    {
        var hex = address.ToString("X12", CultureInfo.InvariantCulture);
        return string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }
}
