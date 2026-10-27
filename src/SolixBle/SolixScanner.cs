using System.Globalization;

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
            return await BleTransport.Scanner.GetStatusAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SolixLog.Warning($"Could not query the Bluetooth adapter state: {ex.Message}");
            return BluetoothStatus.NoAdapter;
        }
    }

    /// <summary>
    /// Scans the BLE neighborhood for Solix BLE device(s) and returns a list of
    /// nearby devices that advertise the Solix service UUID or carry the Anker
    /// manufacturer record, with the model the record or name identifies.
    /// </summary>
    /// <param name="timeout">Time to scan for devices (default 5 s).</param>
    /// <param name="ct">Cancellation token aborting the scan.</param>
    public static async Task<IReadOnlyList<DiscoveredDevice>> DiscoverDevicesAsync(
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var found = new Dictionary<ulong, DiscoveredDevice>();
        var gate = new object();

        void OnAdvertisement(BleAdvertisement args)
        {
            SolixLog.Debug(
                $"Found generic BT device '{args.Address:X12}' with local name '{args.LocalName}'");

            var hasIdentifier = args.ServiceUuids.Contains(SolixConst.UuidIdentifier);
            var record = ReadAnkerRecord(args.ManufacturerData);

            lock (gate)
            {
                var name = args.LocalName ?? string.Empty;
                if (found.TryGetValue(args.Address, out var existing))
                {
                    // The local name and the manufacturer record may only arrive
                    // on a later advertisement or scan response (which may lack
                    // the service UUIDs); update the entry when they show up.
                    var newName = string.IsNullOrEmpty(name) ? existing.Name : name;
                    var productType = record?.ProductType ?? existing.ProductType;
                    found[args.Address] = existing with
                    {
                        Name = newName,
                        Rssi = args.Rssi,
                        ProductType = productType,
                        Capability = record?.Capability ?? existing.Capability,
                        SuggestedModel = SolixDeviceFactory.SuggestModel(productType, newName),
                    };
                }

                // A passive scan can miss the services; the record is always there.
                else if (hasIdentifier || record is not null)
                {
                    SolixLog.Debug(
                        $"Found Anker device '{args.Address:X12}' with local name '{name}'" +
                        (record is null
                            ? ""
                            : $", product type {record.ProductType}, sku '{record.Sku}'," +
                              $" capability {(record.Capability is { } c ? c.ToString("x2") : "none")}"));
                    found[args.Address] = new DiscoveredDevice(
                        args.Address,
                        AddressToMac(args.Address),
                        name,
                        args.Rssi,
                        record?.ProductType,
                        record?.Capability,
                        SolixDeviceFactory.SuggestModel(record?.ProductType, name));
                }
            }
        }

        await BleTransport.Scanner
            .ScanAsync(timeout ?? TimeSpan.FromSeconds(5), OnAdvertisement, ct)
            .ConfigureAwait(false);

        lock (gate)
        {
            return found.Values.ToList();
        }
    }

    /// <summary>Decode the Anker record of an advertisement, if it carries a well-formed one.</summary>
    private static AnkerAdvertisement? ReadAnkerRecord(IReadOnlyList<BleManufacturerData> manufacturerData)
    {
        foreach (var (companyId, data) in manufacturerData)
        {
            if (companyId == SolixConst.AnkerCompanyId && AnkerAdvertisement.Parse(data) is { } record)
                return record;
        }

        return null;
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
