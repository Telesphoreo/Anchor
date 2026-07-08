namespace SolixBle.Devices;

/// <summary>
/// C300(X) Power Station. Use this class to connect and monitor a C300(X)
/// power station. This model is also known as the A1722.
/// </summary>
public class C300 : SolixBleDevice, ISolixUpsDevice
{
    private const int ExpectedTelemetryLength = 253;

    private const string CmdAcOutput = "404a";
    private const string CmdDcOutput = "404b";
    private const string CmdDisplayOnOff = "4052";
    private const string CmdLightMode = "404f";
    private const string CmdDisplayTimeout = "4046";
    private const string CmdDisplayMode = "404c";

    private const string PayloadOn = "a10121a2020101";
    private const string PayloadOff = "a10121a2020100";
    private const string PayloadLightMode = "a10121a20201";
    private const string PayloadTimeoutTime = "a10121a20302";

    public C300(ulong bluetoothAddress, string? name = null) : base(bluetoothAddress, name)
    {
    }

    public C300(DiscoveredDevice device) : base(device)
    {
    }

    /// <summary>Time remaining on AC timer in seconds, or the default int value.</summary>
    public int AcTimerRemaining => ParseInt("a2", begin: 1);

    /// <summary>Timestamp of when the AC timer expires, or null.</summary>
    public DateTimeOffset? AcTimer
    {
        get
        {
            var remaining = AcTimerRemaining;
            if (remaining != SolixConst.DefaultMetadataInt && remaining != 0)
                return DateTimeOffset.Now + TimeSpan.FromSeconds(remaining);
            return null;
        }
    }

    /// <summary>Time remaining on DC timer in seconds, or the default int value.</summary>
    public int DcTimerRemaining => ParseInt("a3", begin: 1);

    /// <summary>Timestamp of when the DC timer expires, or null.</summary>
    public DateTimeOffset? DcTimer
    {
        get
        {
            var remaining = DcTimerRemaining;
            if (remaining != SolixConst.DefaultMetadataInt && remaining != 0)
                return DateTimeOffset.Now + TimeSpan.FromSeconds(remaining);
            return null;
        }
    }

    /// <summary>
    /// Time remaining to full/empty in hours. Any hours over 24 are overflowed
    /// to <see cref="DaysRemaining"/>; use <see cref="TimeRemaining"/> if you
    /// want days to be included.
    /// </summary>
    public double HoursRemaining
    {
        get
        {
            if (!HasData)
                return SolixConst.DefaultMetadataFloat;
            var timeRemaining = TimeRemaining;
            return Math.Round(timeRemaining - Math.Floor(timeRemaining / 24) * 24, 1);
        }
    }

    /// <summary>
    /// Time remaining to full/empty in days. Partial days are overflowed into
    /// <see cref="HoursRemaining"/>; use <see cref="TimeRemaining"/> if you
    /// want hours to be included.
    /// </summary>
    public int DaysRemaining => HasData
        ? (int)Math.Floor(TimeRemaining / 24)
        : SolixConst.DefaultMetadataInt;

    /// <summary>Time remaining to full/empty in hours, or the default float value.</summary>
    public double TimeRemaining => HasData
        ? ParseInt("a4", begin: 1) / 10.0
        : SolixConst.DefaultMetadataFloat;

    /// <summary>Timestamp of when the device will be full/empty, or null.</summary>
    public DateTimeOffset? TimestampRemaining => HasData
        ? DateTimeOffset.Now + TimeSpan.FromHours(TimeRemaining)
        : null;

    /// <summary>Total AC power in, or the default int value.</summary>
    public int AcPowerIn => ParseInt("a5", begin: 1);

    /// <summary>Total AC power out, or the default int value.</summary>
    public int AcPowerOut => ParseInt("a6", begin: 1);

    /// <summary>USB port C1 power, or the default int value.</summary>
    public int UsbC1Power => ParseInt("a7", begin: 1);

    /// <summary>USB port C2 power, or the default int value.</summary>
    public int UsbC2Power => ParseInt("a8", begin: 1);

    /// <summary>USB port C3 power, or the default int value.</summary>
    public int UsbC3Power => ParseInt("a9", begin: 1);

    /// <summary>USB port A1 power, or the default int value.</summary>
    public int UsbA1Power => ParseInt("aa", begin: 1);

    /// <summary>DC power out, or the default int value.</summary>
    public int DcPowerOut => ParseInt("ab", begin: 1);

    /// <summary>Total solar power in, or the default int value.</summary>
    public int SolarPowerIn => ParseInt("ac", begin: 1);

    /// <summary>Total power in, or the default int value.</summary>
    public int PowerIn => ParseInt("ad", begin: 1);

    /// <summary>Total power out, or the default int value.</summary>
    public int PowerOut => ParseInt("ae", begin: 1);

    /// <summary>Main software version, or the default string value.</summary>
    public string SoftwareVersion => HasData
        ? string.Join(".", ParseInt("b1", begin: 1).ToString().Select(digit => digit.ToString()))
        : SolixConst.DefaultMetadataString;

    /// <summary>
    /// AC Port Status. <see cref="PortStatus.NotConnected"/> signifies off,
    /// <see cref="PortStatus.Output"/> signifies on.
    /// </summary>
    public PortStatus AcOutput => (PortStatus)ParseInt("b7", begin: 1);

    /// <summary>Temperature of the unit in degrees C.</summary>
    public int Temperature => ParseInt("b9", begin: 1, signed: true);

    /// <summary>Charging status of the device.</summary>
    public ChargingStatus ChargingStatus => (ChargingStatus)ParseInt("ba", begin: 1);

    /// <summary>Percentage charge of battery, or the default int value.</summary>
    public int BatteryPercentage => ParseInt("bb", begin: 1);

    /// <summary>USB C1 port status.</summary>
    public PortStatus UsbPortC1 => (PortStatus)ParseInt("bd", begin: 1);

    /// <summary>USB C2 port status.</summary>
    public PortStatus UsbPortC2 => (PortStatus)ParseInt("be", begin: 1);

    /// <summary>USB C3 port status.</summary>
    public PortStatus UsbPortC3 => (PortStatus)ParseInt("bf", begin: 1);

    /// <summary>USB A1 port status.</summary>
    public PortStatus UsbPortA1 => (PortStatus)ParseInt("c0", begin: 1);

    /// <summary>
    /// DC Port Status. <see cref="PortStatus.NotConnected"/> signifies off,
    /// <see cref="PortStatus.Output"/> signifies on.
    /// </summary>
    public PortStatus DcOutput => (PortStatus)ParseInt("c1", begin: 1);

    /// <summary>Status of the light bar.</summary>
    public LightStatus Light => (LightStatus)ParseInt("cf", begin: 1);

    /// <summary>Device serial number, or the default string value.</summary>
    public string SerialNumber => ParseString("c5", begin: 1);

    /// <summary>Request and retrieve a status update from the device.</summary>
    /// <returns>Dictionary containing telemetry parameters.</returns>
    /// <exception cref="InvalidOperationException">If not connected to the device.</exception>
    /// <exception cref="TimeoutException">If there is no response from the device.</exception>
    public async Task<IReadOnlyDictionary<string, byte[]>> GetStatusUpdateAsync()
    {
        await SendCommandAsync(
            Convert.FromHexString("4040"), Convert.FromHexString("a10121")).ConfigureAwait(false);

        var packet1 = await ListenForPacketAsync(
            Convert.FromHexString("03010f"), Convert.FromHexString("c840")).ConfigureAwait(false);
        if (packet1 is null || packet1.Length == 0)
            throw new TimeoutException("Timed out waiting for packet 1!");

        var packet2 = await ListenForPacketAsync(
            Convert.FromHexString("03010f"), Convert.FromHexString("c840")).ConfigureAwait(false);
        if (packet2 is null || packet2.Length == 0)
            throw new TimeoutException("Timed out waiting for packet 2!");

        // We need to ignore the first byte of each packet with these types.
        var newPayload = packet1[1..].Concat(packet2[1..]).ToArray();
        var decryptedPayload = DecryptPayload(newPayload);
        var parameters = ParsePayload(decryptedPayload);
        SolixLog.Debug($"Parameters: {ParametersToDebugString(parameters)}");
        return parameters;
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

    /// <summary>Turn the DC output on.</summary>
    public async Task TurnDcOnAsync()
    {
        await SendCommandAsync(
            Convert.FromHexString(CmdDcOutput), Convert.FromHexString(PayloadOn)).ConfigureAwait(false);
    }

    /// <summary>Turn the DC output off.</summary>
    public async Task TurnDcOffAsync()
    {
        await SendCommandAsync(
            Convert.FromHexString(CmdDcOutput), Convert.FromHexString(PayloadOff)).ConfigureAwait(false);
    }

    /// <summary>Turn the display on.</summary>
    public async Task TurnDisplayOnAsync()
    {
        await SendCommandAsync(
            Convert.FromHexString(CmdDisplayOnOff), Convert.FromHexString(PayloadOn)).ConfigureAwait(false);
    }

    /// <summary>Turn the display off.</summary>
    public async Task TurnDisplayOffAsync()
    {
        await SendCommandAsync(
            Convert.FromHexString(CmdDisplayOnOff), Convert.FromHexString(PayloadOff)).ConfigureAwait(false);
    }

    /// <summary>Set the light mode of the LED bar.</summary>
    /// <exception cref="ArgumentException">If the requested mode is invalid.</exception>
    public async Task SetLightModeAsync(LightStatus mode)
    {
        if (mode == LightStatus.Unknown)
            throw new ArgumentException("You cannot set the light status to unknown", nameof(mode));
        var payload = Convert.FromHexString(PayloadLightMode).Append((byte)mode).ToArray();
        await SendCommandAsync(Convert.FromHexString(CmdLightMode), payload).ConfigureAwait(false);
    }

    /// <summary>Set the timeout of the LCD display (30 s, 5 m, 30 m, etc.).</summary>
    /// <exception cref="ArgumentException">If the requested timeout is invalid.</exception>
    public async Task SetDisplayTimeoutAsync(DisplayTimeout timeout)
    {
        if (timeout == DisplayTimeout.Unknown)
            throw new ArgumentException("You cannot set the display timeout to unknown", nameof(timeout));
        var value = (ushort)timeout;
        var payload = Convert.FromHexString(PayloadTimeoutTime)
            .Concat(new[] { (byte)(value & 0xFF), (byte)(value >> 8) })
            .ToArray();
        await SendCommandAsync(Convert.FromHexString(CmdDisplayTimeout), payload).ConfigureAwait(false);
    }

    /// <summary>Set the status/mode of the LCD display (off/low/med/high).</summary>
    /// <exception cref="ArgumentException">If the requested mode is invalid.</exception>
    public async Task SetDisplayModeAsync(LightStatus mode)
    {
        if (mode == LightStatus.Unknown)
            throw new ArgumentException("You cannot set the display brightness status to unknown", nameof(mode));
        if (mode == LightStatus.Sos)
            throw new ArgumentException("You cannot set the display brightness status to SOS", nameof(mode));
        var payload = Convert.FromHexString(PayloadLightMode).Append((byte)mode).ToArray();
        await SendCommandAsync(Convert.FromHexString(CmdDisplayMode), payload).ConfigureAwait(false);
    }

    private static string ParametersToDebugString(IReadOnlyDictionary<string, byte[]> parameters) =>
        "{" + string.Join(", ", parameters.Select(p => $"'{p.Key}': '{Convert.ToHexStringLower(p.Value)}'")) + "}";
}
