namespace SolixBle;

/// <summary>A Solix device found during a BLE scan.</summary>
/// <param name="Address">Bluetooth address of the device.</param>
/// <param name="Mac">MAC address string ("AA:BB:CC:DD:EE:FF") of the device.</param>
/// <param name="Name">Advertised local name of the device (may be empty).</param>
/// <param name="Rssi">Received signal strength in dBm.</param>
/// <param name="ProductType">Advertised Anker product type (hex, e.g. "b118"), if the device published its record.</param>
/// <param name="Capability">
/// Advertised capability byte, if present. Bit 0x04 means the device accepts
/// the encrypted negotiation, which a device created from this scan result opens with.
/// </param>
/// <param name="SuggestedModel">The model the product type or name identifies, if known.</param>
public sealed record DiscoveredDevice(
    ulong Address,
    string Mac,
    string Name,
    short Rssi,
    string? ProductType = null,
    byte? Capability = null,
    SolixDeviceModel? SuggestedModel = null);
