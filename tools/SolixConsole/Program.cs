using System.Text;
using SolixBle;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

// Diagnostic CLI for exercising the SolixBle library against real hardware.
//   dotnet run --project tools/SolixConsole                 -> adapter status + 10s scan
//   dotnet run --project tools/SolixConsole -- <MAC> [model] -> connect and stream telemetry
//   add -v / --verbose anywhere to echo DEBUG-level library logging too.
//   add --pair to attempt Just Works BLE pairing before connecting
//   add --unpair to remove an existing pairing and exit.

var verbose = args.Any(a => a is "-v" or "--verbose");
var pair = args.Any(a => a is "--pair");
var unpair = args.Any(a => a is "--unpair");
var initNoResp = args.Any(a => a is "--init-noresp");
var noMaintain = args.Any(a => a is "--no-maintain");

var delayMs = 0;
string? probe = null;
string? rawHex = null;
var positionalList = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "-v" or "--verbose" or "--pair" or "--unpair" or "--init-noresp" or "--gatt" or "--no-maintain":
            break;
        case "--delay" when i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedDelay):
            delayMs = parsedDelay;
            i++;
            break;
        case "--probe" when i + 1 < args.Length:
            probe = args[i + 1];
            i++;
            break;
        case "--hex" when i + 1 < args.Length:
            rawHex = args[i + 1];
            i++;
            break;
        default:
            positionalList.Add(args[i]);
            break;
    }
}

var positional = positionalList.ToArray();

SolixLog.Sink = (level, message) =>
{
    if (!verbose && level == "DEBUG")
        return;
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}");
};

if (positional.Length == 0)
{
    await RunScanAsync();
    return 0;
}

var mac = positional[0];
ulong address;
try
{
    address = SolixScanner.MacToAddress(mac);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Invalid MAC address '{mac}': {ex.Message}");
    Console.Error.WriteLine("Expected format: AA:BB:CC:DD:EE:FF");
    return 1;
}

if (args.Contains("--gatt"))
{
    await DumpGattAsync(address);
    return 0;
}

if (probe is not null)
{
    await RunRawProbeAsync(address, probe, rawHex, noMaintain, initNoResp);
    return 0;
}

var modelText = positional.Length > 1 ? positional[1] : nameof(SolixDeviceModel.C1000);
if (!Enum.TryParse<SolixDeviceModel>(modelText, ignoreCase: true, out var model)
    || !Enum.IsDefined(model))
{
    Console.Error.WriteLine($"Unknown model '{modelText}'.");
    Console.Error.WriteLine($"Valid models: {string.Join(", ", Enum.GetNames<SolixDeviceModel>())}");
    return 1;
}

if (unpair)
{
    await UnpairAsync(address);
    return 0;
}

if (pair && !await TryPairAsync(address))
{
    Console.WriteLine("Pairing did not succeed; attempting to connect anyway...");
}

await RunConnectAsync(model, address, mac);
return 0;

static async Task DumpGattAsync(ulong address)
{
    using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
    if (device is null)
    {
        Console.WriteLine("Could not resolve the device (is it advertising / in range?).");
        return;
    }

    Console.WriteLine($"Device: {device.Name}  ({address:X12})  status={device.ConnectionStatus}");
    var servicesResult = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
    Console.WriteLine($"Service discovery: {servicesResult.Status}, {servicesResult.Services.Count} service(s)\n");

    foreach (var service in servicesResult.Services)
    {
        Console.WriteLine($"Service {service.Uuid}  (handle 0x{service.AttributeHandle:X4})");
        GattCharacteristicsResult chars;
        try
        {
            chars = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    <characteristic read failed: {ex.Message}>");
            continue;
        }

        if (chars.Status != GattCommunicationStatus.Success)
        {
            Console.WriteLine($"    <characteristics: {chars.Status}>");
            continue;
        }

        foreach (var c in chars.Characteristics)
            Console.WriteLine(
                $"    char {c.Uuid}  handle 0x{c.AttributeHandle:X4}  [{c.CharacteristicProperties}]  prot={c.ProtectionLevel}");
        Console.WriteLine();
    }
}

static async Task<bool> TryPairAsync(ulong address)
{
    using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
    if (device is null)
    {
        Console.WriteLine("Could not resolve the device to pair (is it advertising?).");
        return false;
    }

    var pairing = device.DeviceInformation.Pairing;
    Console.WriteLine($"Pairing state: IsPaired={pairing.IsPaired}, CanPair={pairing.CanPair}");
    if (pairing.IsPaired)
        return true;

    var custom = pairing.Custom;
    custom.PairingRequested += (_, e) =>
    {
        Console.WriteLine($"Pairing requested (kind: {e.PairingKind}); accepting.");
        e.Accept();
    };
    var result = await custom.PairAsync(
        DevicePairingKinds.ConfirmOnly | DevicePairingKinds.ConfirmPinMatch,
        DevicePairingProtectionLevel.Default);
    Console.WriteLine($"Pairing result: {result.Status} (protection used: {result.ProtectionLevelUsed})");
    return result.Status is DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired;
}

static async Task UnpairAsync(ulong address)
{
    using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
    if (device is null)
    {
        Console.WriteLine("Could not resolve the device to unpair (is it advertising?).");
        return;
    }

    if (!device.DeviceInformation.Pairing.IsPaired)
    {
        Console.WriteLine("Device is not paired.");
        return;
    }

    var result = await device.DeviceInformation.Pairing.UnpairAsync();
    Console.WriteLine($"Unpair result: {result.Status}");
}

static async Task RunRawProbeAsync(
    ulong address,
    string probe,
    string? rawHex,
    bool noMaintain,
    bool initNoResp)
{
    const string uuidTelemetry = "8c850003-0302-41c5-b46e-cf057c562025";
    const string uuidCommand = "8c850002-0302-41c5-b46e-cf057c562025";
    const string negotiationCommand0 =
        "ff0936000300010001a10442ad8c69a22462326463306231372d623735642d346162662d626136652d656337633939376332336537b9";
    const string negotiationCommand1 =
        "ff093d000300010003a10442ad8c69a22462326463306231372d623735642d346162662d626136652d656337633939376332336537a30120a40200f064";
    const string negotiationCommand2 =
        "ff0936000300010029a10442ad8c69a22462326463306231372d623735642d346162662d626136652d65633763393937633233653791";
    const string negotiationCommand3 =
        "ff0940000300010005a10443ad8c69a22462326463306231372d623735642d346162662d626136652d656337633939376332336537a30120a40200f0a50140fa";
    const string negotiationCommand4 =
        "ff094c000300010021a140060ea168f232aedb37fb2d120c49180329ac72ab5ec3eb8fd30a2f252dc5e151dabccd9b1dc1e288704ca760a0d8c918e5c94823a1f609a4bf07fb4c33ee219085";
    const string negotiationCommand5 =
        "ff095a000300014022580bc0532a53c739adf3da7b994a7b5f221bcc16bab6392c215cb4faaf41d9d58e2c81c016e474c78eed5569147cb74a1f22ca2b3fad2e209dbbcfbdaca352034a6c479f055f68581b5f1e22348809f526";

    var probeName = probe.ToLowerInvariant();
    var cacheMode = probeName.Contains("cached", StringComparison.Ordinal)
        ? BluetoothCacheMode.Cached
        : BluetoothCacheMode.Uncached;
    var skipSession = probeName.Contains("no-session", StringComparison.Ordinal);
    var writeWithResponse = !initNoResp;
    var legacyWrite = probeName.Contains("oldwrite", StringComparison.Ordinal);

    LogProbe($"Raw probe '{probe}' for {address:X12}");
    LogProbe($"cache={cacheMode}, session={!skipSession}, MaintainConnection={!noMaintain}, writeWithResponse={writeWithResponse}, legacyWrite={legacyWrite}");

    using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
    if (device is null)
    {
        LogProbe("Could not resolve the device.");
        return;
    }

    device.ConnectionStatusChanged += (_, _) =>
        LogProbe($"ConnectionStatusChanged: {device.ConnectionStatus}");
    LogProbe($"Device: {device.Name} status={device.ConnectionStatus}");

    GattSession? session = null;
    if (!skipSession)
    {
        try
        {
            session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId);
            session.MaintainConnection = !noMaintain;
            session.MaxPduSizeChanged += (s, _) => LogProbe($"MaxPduSizeChanged: {s.MaxPduSize}");
            LogProbe($"GattSession established, MaxPduSize={session.MaxPduSize}");
        }
        catch (Exception ex)
        {
            LogProbe($"GattSession failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    var servicesResult = await device.GetGattServicesAsync(cacheMode);
    LogProbe($"Service discovery: {servicesResult.Status}, {servicesResult.Services.Count} service(s)");
    if (servicesResult.Status != GattCommunicationStatus.Success)
        return;

    GattCharacteristic? telemetry = null;
    GattCharacteristic? command = null;
    foreach (var service in servicesResult.Services)
    {
        var charsResult = await service.GetCharacteristicsAsync(cacheMode);
        LogProbe($"Characteristics for {service.Uuid}: {charsResult.Status}, {charsResult.Characteristics.Count}");
        if (charsResult.Status != GattCommunicationStatus.Success)
            continue;

        foreach (var characteristic in charsResult.Characteristics)
        {
            if (characteristic.Uuid == Guid.Parse(uuidTelemetry))
                telemetry = characteristic;
            else if (characteristic.Uuid == Guid.Parse(uuidCommand))
                command = characteristic;
        }
    }

    if (telemetry is null || command is null)
    {
        LogProbe("Required Solix characteristics were not found.");
        return;
    }

    var notificationGate = new object();
    var notifications = new List<byte[]>();
    TaskCompletionSource<byte[]>? awaitedNotification = null;
    string? awaitedCommand = null;

    telemetry.ValueChanged += (_, e) =>
    {
        var data = BufferToBytes(e.CharacteristicValue);
        LogProbe($"Notification len={data.Length}: {Convert.ToHexStringLower(data)}");
        lock (notificationGate)
        {
            notifications.Add(data);
            if (awaitedNotification is not null
                && awaitedCommand is not null
                && ProbeCommandHex(data) == awaitedCommand)
            {
                awaitedNotification.TrySetResult(data);
                awaitedNotification = null;
                awaitedCommand = null;
            }
        }
    };

    LogProbe($"Telemetry handle=0x{telemetry.AttributeHandle:X4} props=[{telemetry.CharacteristicProperties}] prot={telemetry.ProtectionLevel}");
    LogProbe($"Command handle=0x{command.AttributeHandle:X4} props=[{command.CharacteristicProperties}] prot={command.ProtectionLevel}");

    await WaitForMtuAsync(session);

    var stage0 = Convert.FromHexString(negotiationCommand0);
    var stage1 = Convert.FromHexString(negotiationCommand1);
    var stage2 = Convert.FromHexString(negotiationCommand2);
    var stage3 = Convert.FromHexString(negotiationCommand3);
    var stage4 = Convert.FromHexString(negotiationCommand4);
    var stage5 = Convert.FromHexString(negotiationCommand5);
    var plainSubscribe = BuildProbePacket("03000f", "4100", "a10121");
    var oneByte = new byte[] { 0x00 };
    var custom = rawHex is null ? null : Convert.FromHexString(rawHex);

    try
    {
        switch (probeName)
        {
            case "stage0":
            case "stage0-cached":
            case "stage0-no-session":
            case "stage0-oldwrite":
                await SubscribeAsync(telemetry);
                await WriteAsync(command, "stage0 negotiation", stage0, writeWithResponse, legacyWrite);
                await ObserveAsync(device, "after stage0");
                break;

            case "stage0-nosub":
                await WriteAsync(command, "stage0 negotiation without CCCD subscribe", stage0, writeWithResponse, legacyWrite);
                await ObserveAsync(device, "after stage0 without subscribe");
                break;

            case "stage0-notify-after":
                await WriteAsync(command, "stage0 negotiation before CCCD subscribe", stage0, writeWithResponse, legacyWrite);
                await Task.Delay(500);
                if (device.ConnectionStatus == BluetoothConnectionStatus.Connected)
                    await SubscribeAsync(telemetry);
                await ObserveAsync(device, "after late subscribe");
                break;

            case "stage0-nosub-subscribe-rewrite":
                await WriteAsync(command, "stage0 negotiation without CCCD subscribe", stage0, writeWithResponse, legacyWrite);
                await Task.Delay(500);
                if (device.ConnectionStatus == BluetoothConnectionStatus.Connected)
                    await SubscribeAsync(telemetry);
                await Task.Delay(500);
                if (device.ConnectionStatus == BluetoothConnectionStatus.Connected)
                    await WriteAsync(command, "stage0 negotiation after late subscribe", stage0, writeWithResponse, legacyWrite);
                await ObserveAsync(device, "after no-sub/sub/rewrite");
                break;

            case "stage0-then-stage1":
                await SubscribeAsync(telemetry);
                await WriteAsync(command, "stage0 negotiation", stage0, writeWithResponse, legacyWrite);
                await WaitForConnectionStatusAsync(device, BluetoothConnectionStatus.Disconnected, "post-stage0 disconnect");
                await WaitForConnectionStatusAsync(device, BluetoothConnectionStatus.Connected, "post-stage0 reconnect");
                await Task.Delay(500);
                if (device.ConnectionStatus == BluetoothConnectionStatus.Connected)
                    await SubscribeAsync(telemetry);
                await RunStages1Through5Async();
                await ObserveAsync(device, "after stage0 then stages1-5");
                break;

            case "read-then-stage0":
                await SubscribeAsync(telemetry);
                await ReadTelemetryAsync(telemetry);
                await WriteAsync(command, "stage0 negotiation after telemetry read", stage0, writeWithResponse, legacyWrite);
                await ObserveAsync(device, "after read then stage0");
                break;

            case "cccd-none-then-stage0":
                await SubscribeAsync(telemetry);
                await UnsubscribeAsync(telemetry);
                await WriteAsync(command, "stage0 negotiation after CCCD none", stage0, writeWithResponse, legacyWrite);
                await ObserveAsync(device, "after unsubscribe then stage0");
                break;

            case "plain-subscribe":
                await SubscribeAsync(telemetry);
                await WriteAsync(command, "plain subscribe 4100/a10121", plainSubscribe, writeWithResponse, legacyWrite);
                await ObserveAsync(device, "after plain subscribe");
                break;

            case "plain-subscribe-then-stage0":
                await SubscribeAsync(telemetry);
                await WriteAsync(command, "plain subscribe 4100/a10121", plainSubscribe, writeWithResponse, legacyWrite);
                await Task.Delay(1000);
                if (device.ConnectionStatus == BluetoothConnectionStatus.Connected)
                    await WriteAsync(command, "stage0 negotiation after plain subscribe", stage0, writeWithResponse, legacyWrite);
                await ObserveAsync(device, "after plain subscribe then stage0");
                break;

            case "one-byte":
                await SubscribeAsync(telemetry);
                await WriteAsync(command, "single byte 00", oneByte, writeWithResponse, legacyWrite);
                await ObserveAsync(device, "after one-byte write");
                break;

            case "stage1-first":
                await SubscribeAsync(telemetry);
                await WriteAsync(command, "stage1 frame first", stage1, writeWithResponse, legacyWrite);
                await ObserveAsync(device, "after stage1-first");
                break;

            case "stage1-through-stage5":
                await SubscribeAsync(telemetry);
                await RunStages1Through5Async();
                await ObserveAsync(device, "after stages1-5");
                break;

            case "custom":
                if (custom is null)
                {
                    LogProbe("custom probe requires --hex <hex bytes>.");
                    break;
                }

                await SubscribeAsync(telemetry);
                await WriteAsync(command, "custom hex", custom, writeWithResponse, legacyWrite);
                await ObserveAsync(device, "after custom write");
                break;

            default:
                LogProbe("Unknown probe. Known probes: stage0, stage0-nosub, stage0-notify-after, stage0-nosub-subscribe-rewrite, stage0-then-stage1, stage0-cached, stage0-no-session, stage0-oldwrite, read-then-stage0, cccd-none-then-stage0, plain-subscribe, plain-subscribe-then-stage0, one-byte, stage1-first, stage1-through-stage5, custom.");
                break;
        }
    }
    catch (Exception ex)
    {
        LogProbe($"Probe exception: {ex.GetType().Name}: {ex.Message}");
    }
    finally
    {
        if (session is not null)
        {
            session.MaintainConnection = false;
            session.Dispose();
        }

        foreach (var service in servicesResult.Services)
            service.Dispose();
    }

    LogProbe($"Final connection status: {device.ConnectionStatus}");

    static async Task SubscribeAsync(GattCharacteristic telemetry)
    {
        LogProbe("Writing CCCD=Notify");
        var status = await telemetry.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify);
        LogProbe($"CCCD Notify result: {status}");
    }

    static async Task UnsubscribeAsync(GattCharacteristic telemetry)
    {
        LogProbe("Writing CCCD=None");
        var status = await telemetry.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.None);
        LogProbe($"CCCD None result: {status}");
    }

    static async Task ReadTelemetryAsync(GattCharacteristic telemetry)
    {
        LogProbe("Reading telemetry characteristic");
        var result = await telemetry.ReadValueAsync(BluetoothCacheMode.Uncached);
        var detail = result.Status == GattCommunicationStatus.Success
            ? $", len={result.Value.Length}, value={Convert.ToHexStringLower(BufferToBytes(result.Value))}"
            : "";
        LogProbe($"Telemetry read result: {result.Status}{detail}");
    }

    static async Task WriteAsync(
        GattCharacteristic command,
        string label,
        byte[] data,
        bool withResponse,
        bool legacyWrite)
    {
        var option = withResponse
            ? GattWriteOption.WriteWithResponse
            : GattWriteOption.WriteWithoutResponse;

        LogProbe($"Writing {label}: len={data.Length}, option={option}, hex={Convert.ToHexStringLower(data)}");
        using var writer = new DataWriter();
        writer.WriteBytes(data);
        var buffer = writer.DetachBuffer();

        if (legacyWrite)
        {
            var status = await command.WriteValueAsync(buffer, option);
            LogProbe($"Legacy write result: {status}");
            return;
        }

        var result = await command.WriteValueWithResultAsync(buffer, option);
        var att = result.ProtocolError is { } code ? $", ATT=0x{code:X2}" : "";
        LogProbe($"Write result: {result.Status}{att}");
    }

    static async Task ObserveAsync(BluetoothLEDevice device, string label)
    {
        for (var i = 0; i < 12; i++)
        {
            await Task.Delay(250);
            LogProbe($"{label}: t+{(i + 1) * 250} ms status={device.ConnectionStatus}");
        }
    }

    async Task RunStages1Through5Async()
    {
        await WriteAsync(command, "stage1 response frame", stage1, writeWithResponse, legacyWrite);
        await WaitForCommandAsync("0803", "stage2 response");

        await WriteAsync(command, "stage2 response frame", stage2, writeWithResponse, legacyWrite);
        await WaitForCommandAsync("0829", "stage3 response");

        await WriteAsync(command, "stage3 response frame", stage3, writeWithResponse, legacyWrite);
        await WaitForCommandAsync("0805", "stage4 response");

        await WriteAsync(command, "stage4 response frame", stage4, writeWithResponse, legacyWrite);
        await WaitForCommandAsync("0821", "stage5 response");

        await WriteAsync(command, "stage5 response frame", stage5, writeWithResponse, legacyWrite);
        _ = await WaitForCommandAsync("4822", "optional stage6 response", timeoutMs: 1500);
    }

    async Task<byte[]?> WaitForCommandAsync(string cmdHex, string label, int timeoutMs = 3000)
    {
        lock (notificationGate)
        {
            var existing = notifications.FirstOrDefault(n => ProbeCommandHex(n) == cmdHex);
            if (existing is not null)
            {
                LogProbe($"Matched existing {label} ({cmdHex})");
                return existing;
            }

            awaitedCommand = cmdHex;
            awaitedNotification = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        try
        {
            var packet = await awaitedNotification.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
            LogProbe($"Matched {label} ({cmdHex})");
            return packet;
        }
        catch (TimeoutException)
        {
            LogProbe($"Timed out waiting for {label} ({cmdHex})");
            return null;
        }
        finally
        {
            lock (notificationGate)
            {
                if (awaitedCommand == cmdHex)
                {
                    awaitedCommand = null;
                    awaitedNotification = null;
                }
            }
        }
    }

    static async Task WaitForConnectionStatusAsync(
        BluetoothLEDevice device,
        BluetoothConnectionStatus status,
        string label,
        int timeoutMs = 5000)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (device.ConnectionStatus == status)
            {
                LogProbe($"Observed {label}: {status}");
                return;
            }

            await Task.Delay(100);
        }

        LogProbe($"Timed out waiting for {label}; current={device.ConnectionStatus}");
    }

    static async Task WaitForMtuAsync(GattSession? session)
    {
        if (session is null)
        {
            await Task.Delay(500);
            return;
        }

        for (var i = 0; i < 20 && session.MaxPduSize <= 23; i++)
            await Task.Delay(100);
        LogProbe($"MaxPduSize before write sequence: {session.MaxPduSize}");
    }
}

static byte[] BuildProbePacket(string patternHex, string cmdHex, string payloadHex)
{
    var pattern = Convert.FromHexString(patternHex);
    var cmd = Convert.FromHexString(cmdHex);
    var payload = Convert.FromHexString(payloadHex);
    var packet = new byte[2 + 2 + pattern.Length + cmd.Length + payload.Length + 1];
    packet[0] = 0xFF;
    packet[1] = 0x09;
    packet[2] = (byte)(packet.Length & 0xFF);
    packet[3] = (byte)(packet.Length >> 8);
    pattern.CopyTo(packet, 4);
    cmd.CopyTo(packet, 7);
    payload.CopyTo(packet, 9);
    packet[^1] = Checksum(packet.AsSpan(0, packet.Length - 1));
    return packet;
}

static byte Checksum(ReadOnlySpan<byte> bytes)
{
    byte checksum = 0;
    foreach (var b in bytes)
        checksum ^= b;
    return checksum;
}

static byte[] BufferToBytes(IBuffer buffer)
{
    var data = new byte[buffer.Length];
    using var reader = DataReader.FromBuffer(buffer);
    reader.ReadBytes(data);
    return data;
}

static string? ProbeCommandHex(byte[] packet)
{
    if (packet.Length < 9)
        return null;
    if (packet[0] != 0xFF || packet[1] != 0x09)
        return null;
    var encodedLength = packet[2] | (packet[3] << 8);
    if (encodedLength != packet.Length)
        return null;
    return Convert.ToHexStringLower(packet.AsSpan(7, 2));
}

static void LogProbe(string message)
{
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [PROBE] {message}");
}

async Task RunScanAsync()
{
    try
    {
        var status = await SolixScanner.GetBluetoothStatusAsync();
        Console.WriteLine($"Bluetooth adapter: {status}");
        if (!string.Equals(status.ToString(), "Ready", StringComparison.OrdinalIgnoreCase))
            Console.WriteLine("  Adapter is not ready; scanning is unlikely to find anything.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Could not read Bluetooth adapter status: {ex.Message}");
    }

    Console.WriteLine("Scanning for Anker Solix BLE devices (10 s)...");
    var devices = await SolixScanner.DiscoverDevicesAsync(TimeSpan.FromSeconds(10));

    if (devices.Count == 0)
    {
        Console.WriteLine("No Solix devices found.");
        PrintChecklist();
        return;
    }

    Console.WriteLine();
    Console.WriteLine($"{"MAC",-18} {"RSSI",5}  Name");
    Console.WriteLine($"{new string('-', 18)} {new string('-', 5)}  {new string('-', 20)}");
    foreach (var d in devices.OrderByDescending(d => d.Rssi))
        Console.WriteLine($"{d.Mac,-18} {d.Rssi,5}  {d.Name}");
}

async Task RunConnectAsync(SolixDeviceModel deviceModel, ulong deviceAddress, string deviceMac)
{
    Console.WriteLine($"Creating {deviceModel} device for {deviceMac}...");
    var device = SolixDeviceFactory.Create(deviceModel, deviceAddress);
    if (delayMs > 0)
    {
        device.NegotiationStartDelay = TimeSpan.FromMilliseconds(delayMs);
        Console.WriteLine($"Using {delayMs} ms pre-negotiation delay.");
    }
    if (initNoResp)
    {
        device.NegotiationInitWithResponse = false;
        Console.WriteLine("Sending the first negotiation command as write-without-response.");
    }
    if (noMaintain)
    {
        device.MaintainConnection = false;
        Console.WriteLine("MaintainConnection disabled (no GattSession keepalive).");
    }
    var ups = device as ISolixUpsDevice;
    if (ups is null)
        Console.WriteLine($"Note: model {deviceModel} does not expose UPS telemetry; only connection flags will print.");

    var gate = new object();
    var lastConnected = false;
    var lastNegotiated = false;
    var lastAvailable = false;

    device.StateChanged += () =>
    {
        lock (gate)
        {
            var ts = DateTimeOffset.Now.ToString("HH:mm:ss");

            if (ups is not null)
            {
                var batt = SafeRead(() => ups.BatteryPercentage);
                var acIn = SafeRead(() => ups.AcPowerIn);
                var acOut = SafeRead(() => ups.AcPowerOut);
                var totalOut = SafeRead(() => ups.PowerOut);
                var temp = SafeRead(() => ups.Temperature);
                var chgLimit = SafeRead(() => ups.MaxChargeLimitPercent);
                var disLimit = SafeRead(() => ups.MinDischargeLimitPercent);

                var sb = new StringBuilder();
                sb.Append($"[{ts}] batt={batt}% ac-in={acIn}W ac-out={acOut}W out={totalOut}W temp={temp}C");
                if (chgLimit != -1)
                    sb.Append($" chg-limit={chgLimit}%");
                if (disLimit != -1)
                    sb.Append($" dis-limit={disLimit}%");
                Console.WriteLine(sb.ToString());
            }

            if (device.Connected != lastConnected
                || device.Negotiated != lastNegotiated
                || device.Available != lastAvailable)
            {
                Console.WriteLine(
                    $"[{ts}] flags: connected={device.Connected}" +
                    $" negotiated={device.Negotiated} available={device.Available}");
                lastConnected = device.Connected;
                lastNegotiated = device.Negotiated;
                lastAvailable = device.Available;
            }
        }
    };

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    Console.WriteLine("Connecting and negotiating an encrypted session (this can take a while)...");
    bool connected;
    try
    {
        connected = await device.ConnectAsync(ct: cts.Token);
    }
    catch (OperationCanceledException)
    {
        connected = false;
    }

    if (!connected)
    {
        Console.WriteLine("Failed to connect to the device.");
        PrintChecklist();
        if (!verbose)
            Console.WriteLine("Re-run with -v / --verbose to see detailed protocol logging.");
        await device.DisconnectAsync();
        return;
    }

    Console.WriteLine("Connected. Streaming telemetry - press Ctrl+C to disconnect and exit.");
    try
    {
        await Task.Delay(Timeout.Infinite, cts.Token);
    }
    catch (OperationCanceledException)
    {
        // Ctrl+C requested.
    }

    Console.WriteLine();
    Console.WriteLine("Disconnecting...");
    await device.DisconnectAsync();
    Console.WriteLine("Done.");
}

static int SafeRead(Func<int> read)
{
    try
    {
        return read();
    }
    catch (KeyNotFoundException)
    {
        return -1;
    }
}

static void PrintChecklist()
{
    Console.WriteLine();
    Console.WriteLine("Device-side checklist:");
    Console.WriteLine("  1. Press the Bluetooth/IoT button on the station - Wi-Fi setup disables Bluetooth.");
    Console.WriteLine("  2. If still absent, power-cycle the station (it stops advertising after long disconnection).");
    Console.WriteLine("  3. Close the Anker phone app - it holds an exclusive BLE connection.");
}
