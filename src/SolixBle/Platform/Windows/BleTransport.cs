namespace SolixBle;

/// <summary>The WinRT Bluetooth transport.</summary>
internal static class BleTransport
{
    public static IBleScanner Scanner { get; } = new WinRtScanner();

    /// <param name="maintainConnection">Hold a GattSession with MaintainConnection so Windows keeps the link up.</param>
    public static IGattLink CreateLink(ulong bluetoothAddress, bool maintainConnection) =>
        new WinRtGattLink(bluetoothAddress, maintainConnection);
}
