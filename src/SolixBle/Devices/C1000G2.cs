namespace SolixBle.Devices;

/// <summary>
/// C1000(X) Gen 2 Power Station (A1763). Uses the same encryption and telemetry
/// framing as the gen-1 models but with different command codes: it must be sent
/// a subscribe command ("4100") after connecting before it streams any telemetry,
/// its telemetry arrives on commands "c421"/"c900", its AC output is controlled
/// with command "4101" and its DC output with command "4102".
/// </summary>
public class C1000G2 : SolixBleDevice, ISolixUpsDevice
{
    private const int ExpectedTelemetryLength = 253;

    /// <summary>Command sent after connecting to start the telemetry stream.</summary>
    private const string CmdSubscribe = "4100";
    private const string SubscribePayload = "a10121";

    private const string CmdAcOutput = "4101";
    private const string CmdDcOutput = "4102";

    private const string PayloadOn = "a10121a2020101";
    private const string PayloadOff = "a10121a2020100";

    /// <summary>The Gen 2 pushes telemetry on different command codes to the gen-1 models.</summary>
    private static readonly string[] G2TelemetryCommands = ["c421", "c900"];

    public C1000G2(ulong bluetoothAddress, string? name = null) : base(bluetoothAddress, name)
    {
    }

    public C1000G2(DiscoveredDevice device) : base(device)
    {
    }

    /// <inheritdoc/>
    protected override IReadOnlyList<string> TelemetryCommands => G2TelemetryCommands;

    /// <inheritdoc/>
    protected override bool ResumeNegotiationAfterStage0Disconnect => true;

    /// <summary>
    /// Subscribe to telemetry once connected. The Gen 2 streams no telemetry
    /// until it receives this command, so it is sent after every (re)connection.
    /// </summary>
    protected override async Task PostConnectAsync()
    {
        await SendCommandAsync(
            Convert.FromHexString(CmdSubscribe), Convert.FromHexString(SubscribePayload)).ConfigureAwait(false);
    }

    /// <summary>Turn the AC output on.</summary>
    public async Task TurnAcOnAsync()
    {
        await SendCommandAsync(
            Convert.FromHexString(CmdAcOutput), Convert.FromHexString(PayloadOn)).ConfigureAwait(false);
    }

    /// <summary>Turn the AC output off.</summary>
    public async Task TurnAcOffAsync()
    {
        await SendCommandAsync(
            Convert.FromHexString(CmdAcOutput), Convert.FromHexString(PayloadOff)).ConfigureAwait(false);
    }

    /// <summary>
    /// Turn the DC (12 V) output on. The Gen 2 reuses the AC on/off payload on a
    /// different command code ("4102").
    /// </summary>
    public async Task TurnDcOnAsync()
    {
        await SendCommandAsync(
            Convert.FromHexString(CmdDcOutput), Convert.FromHexString(PayloadOn)).ConfigureAwait(false);
    }

    /// <summary>Turn the DC (12 V) output off.</summary>
    public async Task TurnDcOffAsync()
    {
        await SendCommandAsync(
            Convert.FromHexString(CmdDcOutput), Convert.FromHexString(PayloadOff)).ConfigureAwait(false);
    }

    /// <summary>Device serial number, or the default string value.</summary>
    public string SerialNumber => ParseString("a2", begin: 3, end: 20);

    /// <summary>Device part number, or the default string value.</summary>
    public string PartNumber => ParseString("a2", begin: 22, end: 27);

    /// <summary>Temperature of the unit in degrees C.</summary>
    public int Temperature => ParseInt("a5", begin: 1, end: 2, signed: true);

    /// <summary>Percentage charge of battery, or the default int value.</summary>
    public int BatteryPercentage => ParseInt("a5", begin: 3, end: 4);

    /// <summary>Battery health as a percentage, or the default int value.</summary>
    public int BatteryHealth => ParseInt("a5", begin: 4, end: 5);

    /// <summary>Total power out (watts), or the default int value.</summary>
    public int PowerOut => ParseInt("a6", begin: 1, end: 3);

    /// <summary>Total AC power in (watts), or the default int value.</summary>
    public int AcPowerIn => ParseInt("a6", begin: 3, end: 5);

    /// <summary>
    /// AC Port Status. <see cref="PortStatus.NotConnected"/> signifies off,
    /// <see cref="PortStatus.Output"/> signifies on. The AC port status is the
    /// first byte of the "a7" parameter, mirroring the "04 &lt;status&gt; &lt;watts LE&gt;"
    /// per-port shape used by the DC port ("b2") and the USB ports.
    /// </summary>
    public PortStatus AcOutput => (PortStatus)ParseInt("a7", begin: 1, end: 2);

    /// <summary>Total AC power out (watts), or the default int value.</summary>
    public int AcPowerOut => ParseInt("a7", begin: 2, end: 4);

    /// <summary>Solar port status.</summary>
    public PortStatus SolarPort => PortStatusExtensions.FromInputOnly(ParseInt("a8", begin: 1, end: 2));

    /// <summary>Solar/DC power in (watts), or the default int value. Offset inferred.</summary>
    public int SolarPowerIn => ParseInt("a8", begin: 2);

    /// <summary>USB C1 port status.</summary>
    public PortStatus UsbPortC1 => (PortStatus)ParseInt("aa", begin: 1, end: 2);

    /// <summary>USB port C1 power, or the default int value.</summary>
    public int UsbC1Power => ParseInt("aa", begin: 2);

    /// <summary>USB C2 port status.</summary>
    public PortStatus UsbPortC2 => (PortStatus)ParseInt("ab", begin: 1, end: 2);

    /// <summary>USB port C2 power, or the default int value.</summary>
    public int UsbC2Power => ParseInt("ab", begin: 2);

    /// <summary>USB C3 port status.</summary>
    public PortStatus UsbPortC3 => (PortStatus)ParseInt("ac", begin: 1, end: 2);

    /// <summary>USB port C3 power (watts), or the default int value.</summary>
    public int UsbC3Power => ParseInt("ac", begin: 2);

    /// <summary>USB A1 port status.</summary>
    public PortStatus UsbPortA1 => (PortStatus)ParseInt("ae", begin: 1, end: 2);

    /// <summary>USB port A1 power, or the default int value.</summary>
    public int UsbA1Power => ParseInt("ae", begin: 2);

    /// <summary>DC output port status.</summary>
    public PortStatus DcOutput => (PortStatus)ParseInt("b2", begin: 1, end: 2);

    /// <summary>DC power out (watts), or the default int value.</summary>
    public int DcPowerOut => ParseInt("b2", begin: 2);

    /// <summary>Battery charge percentage upper limit, or the default int value.</summary>
    public int MaxBatteryPercentage => ParseInt("d9", begin: 4, end: 5);

    /// <summary>Battery charge percentage lower limit, or the default int value.</summary>
    public int MinBatteryPercentage => ParseInt("d9", begin: 5, end: 6);

    /// <summary>
    /// The Gen 2 reports its user-set charge limit (<see cref="MaxBatteryPercentage"/>)
    /// in telemetry; read-only over BLE.
    /// </summary>
    public int MaxChargeLimitPercent => MaxBatteryPercentage;

    /// <summary>
    /// The Gen 2 reports its user-set discharge cutoff (<see cref="MinBatteryPercentage"/>)
    /// in telemetry; read-only over BLE. The station kills all output at this level.
    /// </summary>
    public int MinDischargeLimitPercent => MinBatteryPercentage;
}
