namespace SolixBle.Devices;

/// <summary>
/// F2600 Power Station. Use this class to connect and monitor an F2600
/// power station. This model is also known as the A1781.
/// </summary>
public class F2600 : F2000
{
    private const int ExpectedTelemetryLength = 253;

    private const string CmdAcTimer = "4042";
    private const string CmdDcTimer = "4043";
    private const string CmdAcChargingPower = "4044";
    private const string CmdDisplayTimeout = "4046";
    private const string CmdDisplayOnOff = "4052";
    private const string CmdAcOutput = "404a";
    private const string CmdDcOutput = "404b";
    private const string CmdDisplayMode = "404c";
    private const string CmdPowerSavingMode = "404e";
    private const string CmdLightMode = "404f";

    private const string PayloadOn = "a10121a2020101";
    private const string PayloadOff = "a10121a2020100";
    private const string PayloadLightMode = "a10121a20201";
    private const string PayloadTimeoutTime = "a10121a20302";
    private const string PayloadAcChargingPower = "a10121a20302";
    private const string PayloadTimer = "a10121a20502";

    public F2600(ulong bluetoothAddress, string? name = null) : base(bluetoothAddress, name)
    {
    }

    public F2600(DiscoveredDevice device) : base(device)
    {
    }

    /// <summary>
    /// Charging status of the device. Idle (0) also covers pure battery-only
    /// discharge (the device does not emit Discharging in that state);
    /// Discharging (1) means a solar source is present but insufficient to
    /// cover the load; Charging (2) means AC wall power is charging the battery.
    /// </summary>
    public ChargingStatus ChargingStatus => (ChargingStatus)ParseInt("bc", begin: 1);

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

    /// <summary>Status of the light bar.</summary>
    public LightStatus Light => (LightStatus)ParseInt("cf", begin: 1);

    /// <summary>
    /// AC Port Status. <see cref="PortStatus.NotConnected"/> signifies off,
    /// <see cref="PortStatus.Output"/> signifies on.
    /// </summary>
    public PortStatus AcOutput => (PortStatus)ParseInt("bb", begin: 1);

    /// <summary>
    /// DC Port Status. <see cref="PortStatus.NotConnected"/> signifies off,
    /// <see cref="PortStatus.Output"/> signifies on. Based on observed F2600
    /// telemetry, key "cb" tracks the DC output state.
    /// </summary>
    public PortStatus DcOutput => (PortStatus)ParseInt("cb", begin: 1);

    /// <summary>USB C1 Port Status.</summary>
    public PortStatus UsbPortC1 => (PortStatus)ParseInt("c6", begin: 1);

    /// <summary>USB C2 Port Status.</summary>
    public PortStatus UsbPortC2 => (PortStatus)ParseInt("c7", begin: 1);

    /// <summary>USB C3 Port Status.</summary>
    public PortStatus UsbPortC3 => (PortStatus)ParseInt("c8", begin: 1);

    /// <summary>USB A1 Port Status.</summary>
    public PortStatus UsbPortA1 => (PortStatus)ParseInt("c9", begin: 1);

    /// <summary>USB A2 Port Status.</summary>
    public PortStatus UsbPortA2 => (PortStatus)ParseInt("ca", begin: 1);

    /// <summary>AC socket output power in watts, or the default int value.</summary>
    public override int AcPowerOut => ParseInt("a6", begin: 1);

    /// <summary>Total output power (AC + USB + DC) in watts, or the default int value.</summary>
    public override int PowerOut => ParseInt("b0", begin: 1);

    /// <summary>
    /// Solar/DC input port status. The port remains an input after the Anderson
    /// connector loses power until AC wall charging takes over, at which point
    /// it clears to <see cref="PortStatus.NotConnected"/>.
    /// </summary>
    public PortStatus SolarPort => PortStatusExtensions.FromInputOnly(ParseInt("bf", begin: 1));

    /// <summary>Total input power in watts, or the default int value.</summary>
    public int PowerIn => ParseInt("af", begin: 1);

    /// <summary>
    /// Total AC wall input power in watts, or the default int value. On the
    /// F2600 key "a5" tracks AC wall input and key "af" the combined total of
    /// all inputs.
    /// </summary>
    public override int AcPowerIn => ParseInt("a5", begin: 1);

    /// <summary>Configured AC charging power limit in watts, or the default int value.</summary>
    public int AcChargingPower => HasParameter("d1")
        ? ParseInt("d1", begin: 1)
        : SolixConst.DefaultMetadataInt;

    /// <summary>Display timeout limit in seconds, or the default int value.</summary>
    public int DisplayTimeoutSeconds => HasParameter("d3")
        ? ParseInt("d3", begin: 1)
        : SolixConst.DefaultMetadataInt;

    /// <summary>Whether power saving mode is enabled, or the default bool value.</summary>
    public bool? PowerSavingModeEnabled => HasParameter("db")
        ? ParseInt("db", begin: 1) != 0
        : SolixConst.DefaultMetadataBool;

    /// <summary>Whether the LCD display is on, or the default bool value.</summary>
    public bool? IsDisplayOn => HasParameter("de")
        ? ParseInt("de", begin: 1) != 0
        : SolixConst.DefaultMetadataBool;

    /// <summary>Configured display brightness level (low/medium/high), or unknown.</summary>
    public LightStatus DisplayMode => HasParameter("d9")
        ? (LightStatus)ParseInt("d9", begin: 1)
        : LightStatus.Unknown;

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

    /// <summary>Set the AC auto-off timer. Pass 0 seconds to cancel.</summary>
    public async Task SetAcTimerAsync(int seconds)
    {
        var payload = Convert.FromHexString(PayloadTimer)
            .Concat(BitConverter.GetBytes((uint)seconds))
            .ToArray();
        await SendCommandAsync(Convert.FromHexString(CmdAcTimer), payload).ConfigureAwait(false);
    }

    /// <summary>Set the DC auto-off timer. Pass 0 seconds to cancel.</summary>
    public async Task SetDcTimerAsync(int seconds)
    {
        var payload = Convert.FromHexString(PayloadTimer)
            .Concat(BitConverter.GetBytes((uint)seconds))
            .ToArray();
        await SendCommandAsync(Convert.FromHexString(CmdDcTimer), payload).ConfigureAwait(false);
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

    /// <summary>Turn the power saving mode on.</summary>
    public async Task TurnPowerSavingModeOnAsync()
    {
        await SendCommandAsync(
            Convert.FromHexString(CmdPowerSavingMode), Convert.FromHexString(PayloadOn)).ConfigureAwait(false);
    }

    /// <summary>Turn the power saving mode off.</summary>
    public async Task TurnPowerSavingModeOffAsync()
    {
        await SendCommandAsync(
            Convert.FromHexString(CmdPowerSavingMode), Convert.FromHexString(PayloadOff)).ConfigureAwait(false);
    }

    /// <summary>Set the AC charging power limit in watts (100 to 1440 W).</summary>
    /// <exception cref="ArgumentOutOfRangeException">If the power value is out of range.</exception>
    public async Task SetAcChargingPowerAsync(int watts)
    {
        // Below 100 W the device charges at full power instead, and 1440 W is
        // the highest the app allows.
        if (watts is < 100 or > 1440)
            throw new ArgumentOutOfRangeException(nameof(watts), watts,
                "AC charging power must be between 100 and 1440 W");

        var payload = Convert.FromHexString(PayloadAcChargingPower)
            .Concat(new[] { (byte)(watts & 0xFF), (byte)(watts >> 8) })
            .ToArray();
        await SendCommandAsync(Convert.FromHexString(CmdAcChargingPower), payload).ConfigureAwait(false);
    }

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
        return parameters;
    }
}
