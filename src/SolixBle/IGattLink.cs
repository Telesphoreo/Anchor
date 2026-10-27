namespace SolixBle;

/// <summary>
/// GATT link to a Solix device: connects by Bluetooth address, locates the
/// Solix telemetry/command characteristics and subscribes to telemetry notifications.
/// </summary>
internal interface IGattLink : IAsyncDisposable
{
    /// <summary>True while the underlying BLE connection is up and the characteristics were found.</summary>
    bool IsConnected { get; }

    /// <summary>The OS-known name of the resolved device, or null before resolution.</summary>
    string? DeviceName { get; }

    /// <summary>The negotiated ATT MTU, or 0 if the OS does not report it.</summary>
    int Mtu { get; }

    /// <summary>Raised with a copy of the bytes of every telemetry characteristic notification.</summary>
    event Action<IGattLink, byte[]>? NotificationReceived;

    /// <summary>Raised when the BLE connection is lost.</summary>
    event Action<IGattLink>? Disconnected;

    /// <summary>Resolve the device, find the Solix characteristics and subscribe to notifications.</summary>
    Task ConnectAsync(CancellationToken ct);

    /// <summary>Write bytes to the command characteristic.</summary>
    Task WriteCommandAsync(byte[] data, bool withResponse, CancellationToken ct);
}
