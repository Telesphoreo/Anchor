namespace SolixBle;

/// <summary>The BlueZ Bluetooth transport, over the system D-Bus.</summary>
internal static class BleTransport
{
    public static IBleScanner Scanner { get; } = new BlueZScanner();

    /// <param name="maintainConnection">Unused: BlueZ holds the link until it is disconnected.</param>
    public static IGattLink CreateLink(ulong bluetoothAddress, bool maintainConnection) =>
        new BlueZGattLink(bluetoothAddress);
}
