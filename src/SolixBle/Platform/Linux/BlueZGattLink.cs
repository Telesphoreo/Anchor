using Tmds.DBus.Protocol;

namespace SolixBle;

/// <summary>
/// BlueZ BLE transport for a Solix device. Finds the device object (running a
/// short discovery if BlueZ has not seen it lately), connects, locates the
/// Solix telemetry/command characteristics and subscribes to telemetry
/// notifications. Never pairs or trusts the device: the Solix protocol does its
/// own key exchange.
/// </summary>
internal sealed class BlueZGattLink : IGattLink
{
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ServicesResolvedTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DisconnectTimeout = TimeSpan.FromSeconds(5);

    private readonly ulong _bluetoothAddress;
    private readonly List<IDisposable> _watches = [];
    private readonly TaskCompletionSource _servicesResolved = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _devicePath;
    private string? _telemetryPath;
    private string? _commandPath;
    private bool _commandWritesWithoutResponse;
    private string? _deviceName;
    private volatile int _mtu;
    private volatile bool _connected;
    private volatile bool _disposed;

    public BlueZGattLink(ulong bluetoothAddress)
    {
        _bluetoothAddress = bluetoothAddress;
    }

    /// <summary>True while the underlying BLE connection is up and the characteristics were found.</summary>
    public bool IsConnected =>
        !_disposed
        && _connected
        && _telemetryPath is not null
        && _commandPath is not null;

    /// <summary>The device's name in BlueZ, or null before resolution.</summary>
    public string? DeviceName => _deviceName;

    /// <summary>The characteristics' MTU property (BlueZ 5.62+), or 0 if BlueZ does not report it.</summary>
    public int Mtu => _mtu;

    /// <summary>Raised with a copy of the bytes of every telemetry characteristic notification.</summary>
    public event Action<IGattLink, byte[]>? NotificationReceived;

    /// <summary>Raised when the BLE connection is lost.</summary>
    public event Action<IGattLink>? Disconnected;

    /// <summary>Resolve the device, find the Solix characteristics and subscribe to notifications.</summary>
    public async Task ConnectAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            await ConnectCoreAsync(ct).ConfigureAwait(false);
        }
        catch (DBusExceptionBase ex)
        {
            throw new InvalidOperationException($"BlueZ connection failed: {BlueZ.Describe(ex)}", ex);
        }
    }

    private async Task ConnectCoreAsync(CancellationToken ct)
    {
        SolixLog.Debug($"Resolving BLE device {_bluetoothAddress:X12}...");
        var objects = await BlueZ.GetManagedObjectsAsync().WaitAsync(ct).ConfigureAwait(false);
        var adapterPath = BlueZ.FindAdapter(objects)
            ?? throw new InvalidOperationException("No Bluetooth adapter found!");
        var devicePath = BlueZ.DevicePath(adapterPath, _bluetoothAddress);

        _watches.Add(await BlueZ.WatchPropertiesAsync(
            devicePath, pathNamespace: null, BlueZ.DeviceInterface, OnDeviceChanged, OnBusClosed).ConfigureAwait(false));
        _watches.Add(await BlueZ.WatchInterfacesRemovedAsync(OnInterfacesRemoved).ConfigureAwait(false));

        // BlueZ only has a device object for a device it has seen recently.
        if (!objects.TryGetValue(devicePath, out var known) || !known.ContainsKey(BlueZ.DeviceInterface))
            await DiscoverAsync(adapterPath, devicePath, ct).ConfigureAwait(false);

        SolixLog.Debug($"Connecting to {devicePath}...");
        _devicePath = devicePath;
        try
        {
            await BlueZ.CallAsync(devicePath, BlueZ.DeviceInterface, "Connect").WaitAsync(ct).ConfigureAwait(false);
        }
        catch (DBusErrorReplyException ex) when (BlueZ.IsError(ex, "org.bluez.Error.AlreadyConnected"))
        {
            SolixLog.Debug($"{devicePath} was already connected.");
        }

        _connected = true;

        var resolved = await BlueZ.GetPropertyAsync(devicePath, BlueZ.DeviceInterface, "ServicesResolved")
            .WaitAsync(ct).ConfigureAwait(false);
        if (!(resolved.Type == VariantValueType.Bool && resolved.GetBool()))
        {
            SolixLog.Debug("Waiting for BlueZ to resolve the GATT services...");
            try
            {
                await _servicesResolved.Task.WaitAsync(ServicesResolvedTimeout, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new InvalidOperationException(
                    $"GATT service discovery did not finish within {ServicesResolvedTimeout.TotalSeconds:0} s!");
            }
        }

        objects = await BlueZ.GetManagedObjectsAsync().WaitAsync(ct).ConfigureAwait(false);
        if (objects.TryGetValue(devicePath, out var device)
            && device.TryGetValue(BlueZ.DeviceInterface, out var deviceProperties))
            _deviceName = deviceProperties.GetDeviceName();

        string[] telemetryFlags = [];
        string[] commandFlags = [];
        foreach (var (path, interfaces) in objects)
        {
            if (!path.StartsWith(devicePath + "/", StringComparison.Ordinal)
                || !interfaces.TryGetValue(BlueZ.CharacteristicInterface, out var properties)
                || !Guid.TryParse(properties.GetString("UUID"), out var uuid))
                continue;

            if (uuid == SolixConst.UuidTelemetry)
            {
                _telemetryPath = path;
                telemetryFlags = properties.GetStrings("Flags") ?? [];
            }
            else if (uuid == SolixConst.UuidCommand)
            {
                _commandPath = path;
                commandFlags = properties.GetStrings("Flags") ?? [];
            }
        }

        if (_telemetryPath is null || _commandPath is null)
            throw new InvalidOperationException("Device does not expose the Solix telemetry/command characteristics!");

        _commandWritesWithoutResponse = commandFlags.Contains("write-without-response");
        SolixLog.Debug(
            $"Characteristic properties: telemetry [{string.Join(", ", telemetryFlags)}], " +
            $"command [{string.Join(", ", commandFlags)}]");

        SolixLog.Debug($"Subscribing to telemetry notifications from {_bluetoothAddress:X12}...");
        _watches.Add(await BlueZ.WatchPropertiesAsync(
            _telemetryPath, pathNamespace: null, BlueZ.CharacteristicInterface, OnTelemetryChanged).ConfigureAwait(false));
        try
        {
            await BlueZ.CallAsync(_telemetryPath, BlueZ.CharacteristicInterface, "StartNotify")
                .WaitAsync(ct).ConfigureAwait(false);
        }
        catch (DBusErrorReplyException ex)
        {
            throw new InvalidOperationException($"Failed to subscribe to telemetry notifications: '{BlueZ.Describe(ex)}'!", ex);
        }

        // Negotiation frames are up to 90 bytes, but a single ATT write only
        // carries MTU-3 bytes; above that BlueZ falls back to a chunked long
        // write, which some device firmwares reject by dropping the link.
        // Give the stack a moment to finish its MTU exchange first.
        if (await ReadMtuAsync(ct).ConfigureAwait(false) > 0)
        {
            for (var i = 0; i < 20 && _mtu <= 23; i++)
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
                await ReadMtuAsync(ct).ConfigureAwait(false);
            }

            SolixLog.Debug($"Negotiated ATT MTU: {_mtu}");
        }
        else
        {
            SolixLog.Debug("BlueZ does not report the ATT MTU (it needs 5.62 or newer).");
        }
    }

    /// <summary>Run an LE discovery until BlueZ creates the device's object.</summary>
    private async Task DiscoverAsync(string adapterPath, string devicePath, CancellationToken ct)
    {
        SolixLog.Debug(
            $"BlueZ has no object for {_bluetoothAddress:X12}; discovering for up to {DiscoveryTimeout.TotalSeconds:0} s...");
        var appeared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watch = await BlueZ.WatchInterfacesAddedAsync(signal =>
        {
            if (signal.Path == devicePath && signal.Interfaces.ContainsKey(BlueZ.DeviceInterface))
                appeared.TrySetResult();
        }).ConfigureAwait(false);

        await BlueZ.SetLeDiscoveryFilterAsync(adapterPath).WaitAsync(ct).ConfigureAwait(false);
        await BlueZ.CallAsync(adapterPath, BlueZ.AdapterInterface, "StartDiscovery").WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // The object may have appeared before the watch was in place.
            var objects = await BlueZ.GetManagedObjectsAsync().WaitAsync(ct).ConfigureAwait(false);
            if (objects.TryGetValue(devicePath, out var interfaces) && interfaces.ContainsKey(BlueZ.DeviceInterface))
                appeared.TrySetResult();

            await appeared.Task.WaitAsync(DiscoveryTimeout, ct).ConfigureAwait(false);
            SolixLog.Debug($"BlueZ discovered {devicePath}.");
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException(
                $"Unable to resolve BLE device with address {_bluetoothAddress:X12}: " +
                $"it was not heard within {DiscoveryTimeout.TotalSeconds:0} s!");
        }
        finally
        {
            try
            {
                await BlueZ.CallAsync(adapterPath, BlueZ.AdapterInterface, "StopDiscovery").ConfigureAwait(false);
            }
            catch (DBusExceptionBase ex)
            {
                SolixLog.Debug($"Could not stop Bluetooth discovery on {adapterPath}: {BlueZ.Describe(ex)}");
            }
        }
    }

    /// <summary>Refresh <see cref="_mtu"/> from the command characteristic; 0 if BlueZ has no MTU property.</summary>
    private async Task<int> ReadMtuAsync(CancellationToken ct)
    {
        try
        {
            var mtu = await BlueZ.GetPropertyAsync(_commandPath!, BlueZ.CharacteristicInterface, "MTU")
                .WaitAsync(ct).ConfigureAwait(false);
            if (mtu.Type == VariantValueType.UInt16)
                _mtu = mtu.GetUInt16();
        }
        catch (DBusErrorReplyException ex) when (BlueZ.IsError(ex, "org.freedesktop.DBus.Error.InvalidArgs"))
        {
            // Older BlueZ: no MTU property.
        }

        return _mtu;
    }

    /// <summary>Write bytes to the command characteristic.</summary>
    public async Task WriteCommandAsync(byte[] data, bool withResponse, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var characteristic = _commandPath
            ?? throw new InvalidOperationException("Not connected to device");

        var type = withResponse || !_commandWritesWithoutResponse ? "request" : "command";

        var mtu = _mtu;
        if (mtu > 0 && data.Length > mtu - 3)
            SolixLog.Warning(
                $"Write of {data.Length} bytes exceeds the negotiated MTU ({mtu}); BlueZ will use a chunked " +
                "long write, which some device firmwares reject by dropping the link.");

        try
        {
            await BlueZ.WriteValueAsync(characteristic, data, type).WaitAsync(ct).ConfigureAwait(false);
        }
        catch (DBusErrorReplyException ex)
        {
            throw new InvalidOperationException($"GATT write failed: {BlueZ.Describe(ex)}", ex);
        }
        catch (DBusConnectionException ex)
        {
            throw new InvalidOperationException($"GATT write failed: the system bus connection was lost ({ex.Message}).", ex);
        }
    }

    private void OnTelemetryChanged(PropertiesChange change)
    {
        if (change.Changed.TryGetValue("MTU", out var mtu) && mtu.Type == VariantValueType.UInt16)
            _mtu = mtu.GetUInt16();

        if (_disposed
            || !change.Changed.TryGetValue("Value", out var value)
            || value.Type != VariantValueType.Array
            || value.ItemType != VariantValueType.Byte)
            return;

        NotificationReceived?.Invoke(this, value.GetArray<byte>());
    }

    private void OnDeviceChanged(PropertiesChange change)
    {
        if (change.Changed.GetBool("ServicesResolved") == true)
            _servicesResolved.TrySetResult();
        if (change.Changed.GetBool("Connected") == false)
            OnLinkLost("BLE connection status changed to disconnected");
    }

    private void OnInterfacesRemoved((string Path, string[] Interfaces) signal)
    {
        if (signal.Path == _devicePath && signal.Interfaces.Contains(BlueZ.DeviceInterface))
            OnLinkLost("BlueZ removed the device object");
    }

    private void OnBusClosed(Exception ex) => OnLinkLost($"the system bus connection closed ({ex.Message})");

    private void OnLinkLost(string reason)
    {
        if (_disposed || !_connected)
            return;
        _connected = false;
        _servicesResolved.TrySetException(new InvalidOperationException($"Connection lost: {reason}."));
        SolixLog.Debug($"{reason} for {_bluetoothAddress:X12}");
        Disconnected?.Invoke(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        foreach (var watch in _watches)
            watch.Dispose();
        _watches.Clear();
        _telemetryPath = null;
        _commandPath = null;

        // BlueZ keeps an LE link up until a client disconnects it.
        if (_devicePath is not null)
        {
            try
            {
                await BlueZ.CallAsync(_devicePath, BlueZ.DeviceInterface, "Disconnect")
                    .WaitAsync(DisconnectTimeout).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SolixLog.Debug($"Exception disconnecting BLE device: {ex.Message}");
            }
        }
    }
}
