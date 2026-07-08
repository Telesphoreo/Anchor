using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace SolixBle;

/// <summary>
/// WinRT BLE transport for a Solix device. Resolves the device by Bluetooth
/// address, locates the Solix telemetry/command characteristics and subscribes
/// to telemetry notifications.
/// </summary>
internal sealed class GattClient : IAsyncDisposable
{
    private readonly ulong _bluetoothAddress;
    private readonly bool _maintainConnection;
    private BluetoothLEDevice? _device;
    private GattSession? _gattSession;
    private IReadOnlyList<GattDeviceService> _services = [];
    private GattCharacteristic? _telemetryCharacteristic;
    private GattCharacteristic? _commandCharacteristic;
    private bool _disposed;

    public GattClient(ulong bluetoothAddress, bool maintainConnection = true)
    {
        _bluetoothAddress = bluetoothAddress;
        _maintainConnection = maintainConnection;
    }

    /// <summary>True while the underlying BLE connection is up and the characteristics were found.</summary>
    public bool IsConnected =>
        !_disposed
        && _device is { ConnectionStatus: BluetoothConnectionStatus.Connected }
        && _telemetryCharacteristic is not null
        && _commandCharacteristic is not null;

    /// <summary>Raised with a copy of the bytes of every telemetry characteristic notification.</summary>
    public event Action<GattClient, byte[]>? NotificationReceived;

    /// <summary>Raised when the BLE connection is lost.</summary>
    public event Action<GattClient>? Disconnected;

    /// <summary>Resolve the device, find the Solix characteristics and subscribe to notifications.</summary>
    public async Task ConnectAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        SolixLog.Debug($"Resolving BLE device {_bluetoothAddress:X12}...");
        var device = await BluetoothLEDevice.FromBluetoothAddressAsync(_bluetoothAddress).AsTask(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unable to resolve BLE device with address {_bluetoothAddress:X12}!");
        _device = device;
        device.ConnectionStatusChanged += OnConnectionStatusChanged;

        // Ask Windows to actively establish and hold the BLE link. Without this
        // the OS drops the connection shortly after the GATT operations go idle
        // (bleak, which upstream uses, sets the same flag on its WinRT backend).
        try
        {
            _gattSession = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId).AsTask(ct).ConfigureAwait(false);
            _gattSession.MaintainConnection = _maintainConnection;
            _gattSession.MaxPduSizeChanged += (s, _) => SolixLog.Debug($"Negotiated ATT MTU changed: {s.MaxPduSize}");
            SolixLog.Debug($"GattSession established (MaintainConnection={_maintainConnection}).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SolixLog.Warning($"Could not create GattSession for {_bluetoothAddress:X12}: {ex.Message}");
        }

        var servicesResult = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask(ct).ConfigureAwait(false);
        if (servicesResult.Status != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"GATT service discovery failed with status '{servicesResult.Status}'!");

        var services = servicesResult.Services.ToList();
        _services = services;

        foreach (var service in services)
        {
            ct.ThrowIfCancellationRequested();
            var characteristicsResult =
                await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(ct).ConfigureAwait(false);
            if (characteristicsResult.Status != GattCommunicationStatus.Success)
            {
                SolixLog.Debug(
                    $"Skipping service '{service.Uuid}': characteristic discovery failed with status '{characteristicsResult.Status}'");
                continue;
            }

            foreach (var characteristic in characteristicsResult.Characteristics)
            {
                if (characteristic.Uuid == SolixConst.UuidTelemetry)
                    _telemetryCharacteristic = characteristic;
                else if (characteristic.Uuid == SolixConst.UuidCommand)
                    _commandCharacteristic = characteristic;
            }
        }

        if (_telemetryCharacteristic is null || _commandCharacteristic is null)
            throw new InvalidOperationException("Device does not expose the Solix telemetry/command characteristics!");

        SolixLog.Debug(
            $"Characteristic properties — telemetry: [{_telemetryCharacteristic.CharacteristicProperties}], " +
            $"command: [{_commandCharacteristic.CharacteristicProperties}], " +
            $"command protection level: {_commandCharacteristic.ProtectionLevel}");

        SolixLog.Debug($"Subscribing to telemetry notifications from {_bluetoothAddress:X12}...");
        _telemetryCharacteristic.ValueChanged += OnValueChanged;
        var status = await _telemetryCharacteristic
            .WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify)
            .AsTask(ct)
            .ConfigureAwait(false);
        if (status != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"Failed to subscribe to telemetry notifications: '{status}'!");

        // Negotiation frames are up to 90 bytes, but a single ATT write only
        // carries MaxPduSize-3 bytes; below that Windows falls back to a chunked
        // "Write Long", which some device firmwares reject by dropping the link.
        // Give the OS a moment to finish its automatic MTU exchange first.
        if (_gattSession is not null)
        {
            for (var i = 0; i < 20 && _gattSession.MaxPduSize <= 23; i++)
                await Task.Delay(100, ct).ConfigureAwait(false);
            SolixLog.Debug($"Negotiated ATT MTU (MaxPduSize): {_gattSession.MaxPduSize}");
        }
    }

    /// <summary>Write bytes to the command characteristic.</summary>
    public async Task WriteCommandAsync(byte[] data, bool withResponse, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var characteristic = _commandCharacteristic
            ?? throw new InvalidOperationException("Not connected to device");

        var supportsWriteWithoutResponse =
            characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse);
        var option = withResponse || !supportsWriteWithoutResponse
            ? GattWriteOption.WriteWithResponse
            : GattWriteOption.WriteWithoutResponse;

        var mtu = _gattSession?.MaxPduSize ?? 0;
        if (mtu > 0 && data.Length > mtu - 3)
            SolixLog.Warning(
                $"Write of {data.Length} bytes exceeds the negotiated MTU ({mtu}); Windows will use a chunked " +
                "Write Long, which some device firmwares reject by dropping the link.");

        using var writer = new DataWriter();
        writer.WriteBytes(data);
        GattWriteResult result;
        try
        {
            result = await characteristic.WriteValueWithResultAsync(writer.DetachBuffer(), option)
                .AsTask(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or System.Runtime.InteropServices.COMException)
        {
            // WinRT throws these when the underlying link died out from under us —
            // surface it as an ordinary failed write, not a crash.
            var hr = ex is System.Runtime.InteropServices.COMException com ? $" HRESULT=0x{com.HResult:X8}" : "";
            throw new InvalidOperationException(
                $"GATT write failed — the BLE connection was lost ({ex.GetType().Name}{hr}).", ex);
        }

        if (result.Status != GattCommunicationStatus.Success)
        {
            var att = result.ProtocolError is { } code ? $" ATT protocol error 0x{code:X2}" : "";
            throw new InvalidOperationException($"GATT write failed with status '{result.Status}'.{att}");
        }
    }

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var buffer = args.CharacteristicValue;
        var data = new byte[buffer.Length];
        using (var reader = DataReader.FromBuffer(buffer))
        {
            reader.ReadBytes(data);
        }

        NotificationReceived?.Invoke(this, data);
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
        {
            SolixLog.Debug($"BLE connection status changed to disconnected for {_bluetoothAddress:X12}");
            Disconnected?.Invoke(this);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;
        _disposed = true;

        if (_telemetryCharacteristic is not null)
            _telemetryCharacteristic.ValueChanged -= OnValueChanged;
        _telemetryCharacteristic = null;
        _commandCharacteristic = null;

        foreach (var service in _services)
        {
            try
            {
                service.Dispose();
            }
            catch (Exception ex)
            {
                SolixLog.Debug($"Exception disposing GATT service: {ex.Message}");
            }
        }

        _services = [];

        if (_gattSession is not null)
        {
            try
            {
                _gattSession.MaintainConnection = false;
                _gattSession.Dispose();
            }
            catch (Exception ex)
            {
                SolixLog.Debug($"Exception disposing GATT session: {ex.Message}");
            }

            _gattSession = null;
        }

        if (_device is not null)
        {
            _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
            try
            {
                _device.Dispose();
            }
            catch (Exception ex)
            {
                SolixLog.Debug($"Exception disposing BLE device: {ex.Message}");
            }

            _device = null;
        }

        return ValueTask.CompletedTask;
    }
}
