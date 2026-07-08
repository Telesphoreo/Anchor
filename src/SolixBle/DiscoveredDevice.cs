namespace SolixBle;

/// <summary>A Solix device found during a BLE scan.</summary>
/// <param name="Address">Bluetooth address of the device.</param>
/// <param name="Mac">MAC address string ("AA:BB:CC:DD:EE:FF") of the device.</param>
/// <param name="Name">Advertised local name of the device (may be empty).</param>
/// <param name="Rssi">Received signal strength in dBm.</param>
public sealed record DiscoveredDevice(ulong Address, string Mac, string Name, short Rssi);
