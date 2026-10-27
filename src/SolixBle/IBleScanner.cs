namespace SolixBle;

/// <summary>One manufacturer-specific data record of an advertisement.</summary>
/// <param name="CompanyId">Bluetooth SIG company identifier.</param>
/// <param name="Data">Record bytes, without the company identifier.</param>
internal readonly record struct BleManufacturerData(ushort CompanyId, byte[] Data);

/// <summary>An advertisement (or scan response) heard during a scan.</summary>
/// <param name="Address">Bluetooth address of the advertiser.</param>
/// <param name="LocalName">Advertised local name, if this advertisement carried one.</param>
/// <param name="Rssi">Received signal strength in dBm.</param>
/// <param name="ServiceUuids">Advertised service UUIDs.</param>
/// <param name="ManufacturerData">Advertised manufacturer data records.</param>
internal sealed record BleAdvertisement(
    ulong Address,
    string? LocalName,
    short Rssi,
    IEnumerable<Guid> ServiceUuids,
    IReadOnlyList<BleManufacturerData> ManufacturerData);

/// <summary>The platform's Bluetooth adapter: its state and LE scanning.</summary>
internal interface IBleScanner
{
    /// <summary>Query the default adapter. Throws if the Bluetooth stack cannot be queried.</summary>
    Task<BluetoothStatus> GetStatusAsync();

    /// <summary>
    /// Scan for LE advertisements for <paramref name="duration"/>, reporting each one
    /// as it arrives. A device counts only if it was heard during this scan.
    /// </summary>
    Task ScanAsync(TimeSpan duration, Action<BleAdvertisement> onAdvertisement, CancellationToken ct);
}
