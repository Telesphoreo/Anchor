using System.Text;
using SolixBle;
using SolixConsole;

// Diagnostic CLI for exercising the SolixBle library against real hardware.
//   dotnet run --project tools/SolixConsole                 -> adapter status + 10s scan
//   dotnet run --project tools/SolixConsole -- <MAC> [model] -> connect and stream telemetry
//   add -v / --verbose anywhere to echo DEBUG-level library logging too.
//   add --pair to attempt Just Works BLE pairing before connecting
//   add --unpair to remove an existing pairing and exit.
//   add --gatt to dump the GATT table, or --probe <name> [--hex <bytes>] for raw negotiation probes.
// Scan and connect run on Windows and Linux; --pair, --unpair, --gatt and --probe are Windows-only.

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
    await Diagnostics.DumpGattAsync(address);
    return 0;
}

if (probe is not null)
{
    await Diagnostics.RunRawProbeAsync(address, probe, rawHex, noMaintain, initNoResp);
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
    await Diagnostics.UnpairAsync(address);
    return 0;
}

if (pair && !await Diagnostics.TryPairAsync(address))
{
    Console.WriteLine("Pairing did not succeed; attempting to connect anyway...");
}

await RunConnectAsync(model, address, mac);
return 0;

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

    Console.WriteLine("Connected. Streaming telemetry; press Ctrl+C to disconnect and exit.");
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
    Console.WriteLine("  1. Press the Bluetooth/IoT button on the station (Wi-Fi setup disables Bluetooth).");
    Console.WriteLine("  2. If still absent, power-cycle the station (it stops advertising after long disconnection).");
    Console.WriteLine("  3. Close the Anker phone app (it holds an exclusive BLE connection).");
}
