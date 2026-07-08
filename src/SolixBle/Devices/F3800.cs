namespace SolixBle.Devices;

/// <summary>
/// F3800 Power Station. Use this class to connect and monitor an F3800 power
/// station. This model is also known as the A1790.
/// </summary>
/// <remarks>
/// This model was added using data from anker-solix-api and has not been tested.
/// </remarks>
public class F3800 : SolixBleDevice, ISolixUpsDevice
{
    private const int ExpectedTelemetryLength = 253;

    public F3800(ulong bluetoothAddress, string? name = null) : base(bluetoothAddress, name)
    {
    }

    public F3800(DiscoveredDevice device) : base(device)
    {
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

    /// <summary>USB port A2 power, or the default int value.</summary>
    public int UsbA2Power => ParseInt("ab", begin: 1);

    /// <summary>DC port status.</summary>
    public PortStatus DcOutput => (PortStatus)ParseInt("ac", begin: 1);

    /// <summary>Percentage charge of battery, or the default int value.</summary>
    public int BatteryPercentage => ParseInt("ad", begin: 1);

    /// <summary>Total solar power in, or the default int value.</summary>
    public int SolarPowerIn => ParseInt("ae", begin: 1);

    /// <summary>Solar power in for port 1, or the default int value.</summary>
    public int SolarPv1PowerIn => ParseInt("af", begin: 1);

    /// <summary>Solar power in for port 2, or the default int value.</summary>
    public int SolarPv2PowerIn => ParseInt("b0", begin: 1);

    /// <summary>Battery charging power (AC+DC), or the default int value.</summary>
    public int BatteryChargePower => ParseInt("b1", begin: 1);

    /// <summary>Total power out, or the default int value.</summary>
    public int PowerOut => ParseInt("b2", begin: 1);

    /// <summary>Battery discharging power (AC+DC), or the default int value.</summary>
    public int BatteryDischargePower => ParseInt("b4", begin: 1);

    /// <summary>Main software version, or the default string value.</summary>
    public string SoftwareVersion => HasData
        ? string.Join(".", ParseInt("b5", begin: 1).ToString().Select(digit => digit.ToString()))
        : SolixConst.DefaultMetadataString;

    /// <summary>
    /// Software version of any expansion batteries ("0" if there is no
    /// expansion battery), or the default string value.
    /// </summary>
    public string SoftwareVersionExpansion => HasData
        ? string.Join(".", ParseInt("ba", begin: 1).ToString().Select(digit => digit.ToString()))
        : SolixConst.DefaultMetadataString;

    /// <summary>
    /// AC Port Status. <see cref="PortStatus.NotConnected"/> signifies off,
    /// <see cref="PortStatus.Output"/> signifies on.
    /// </summary>
    public PortStatus AcOutput => (PortStatus)ParseInt("bc", begin: 1);

    /// <summary>Charging status of the device.</summary>
    public ChargingStatusF3800 ChargingStatus => (ChargingStatusF3800)ParseInt("bd", begin: 1);

    /// <summary>Temperature of the unit in degrees C.</summary>
    public int Temperature => ParseInt("be", begin: 1, signed: true);

    /// <summary>Battery percentage average across all batteries, or the default int value.</summary>
    public int BatteryPercentageAggregate => ParseInt("c0", begin: 1);

    /// <summary>Battery charge percentage upper limit, or the default int value.</summary>
    public int MaxBatteryPercentage => ParseInt("c1", begin: 1);

    /// <summary>USB C1 port status.</summary>
    public PortStatus UsbPortC1 => (PortStatus)ParseInt("c2", begin: 1);

    /// <summary>USB C2 port status.</summary>
    public PortStatus UsbPortC2 => (PortStatus)ParseInt("c3", begin: 1);

    /// <summary>USB C3 port status.</summary>
    public PortStatus UsbPortC3 => (PortStatus)ParseInt("c4", begin: 1);

    /// <summary>USB A1 port status.</summary>
    public PortStatus UsbPortA1 => (PortStatus)ParseInt("c5", begin: 1);

    /// <summary>USB A2 port status.</summary>
    public PortStatus UsbPortA2 => (PortStatus)ParseInt("c6", begin: 1);

    /// <summary>Device serial number, or the default string value.</summary>
    public string SerialNumber => ParseString("cc", begin: 1);

    /// <summary>Display timeout limit in seconds, or the default int value.</summary>
    public int DisplayTimeout => ParseInt("cf", begin: 1);
}
