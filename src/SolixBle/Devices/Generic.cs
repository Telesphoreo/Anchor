namespace SolixBle.Devices;

/// <summary>
/// Generic device to be used for adding support for an unsupported device.
/// Add support for a device like this:
/// <list type="number">
/// <item>Copy this subclass to a new class with a name of the device.</item>
/// <item>Initialise the new class and connect to it.</item>
/// <item>Change values (e.g. turn things on and off) to cause changes in the device state.</item>
/// <item>Observe which values change in the log and add properties to your subclass that parse them (see C300, C1000, etc. for examples).</item>
/// <item>Profit???</item>
/// </list>
/// </summary>
public class Generic : SolixBleDevice
{
    private const int ExpectedTelemetryLength = 0;

    public Generic(ulong bluetoothAddress, string? name = null) : base(bluetoothAddress, name)
    {
    }

    public Generic(DiscoveredDevice device) : base(device)
    {
    }
}
