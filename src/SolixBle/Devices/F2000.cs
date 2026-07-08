namespace SolixBle.Devices;

/// <summary>
/// F2000(P) Power Station. Use this class to connect and monitor an F2000(P)
/// power station. This model is also known as the A1780 or the 767 PowerHouse.
/// </summary>
/// <remarks>
/// This model was added using data from anker-solix-api and has not been tested.
/// </remarks>
public class F2000 : SolixBleDevice, ISolixUpsDevice
{
    private const int ExpectedTelemetryLength = 253;

    public F2000(ulong bluetoothAddress, string? name = null) : base(bluetoothAddress, name)
    {
    }

    public F2000(DiscoveredDevice device) : base(device)
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

    /// <summary>AC power that is going to the battery, or the default int value.</summary>
    public int AcToBattery => ParseInt("a5", begin: 1);

    /// <summary>AC power out to sockets, or the default int value.</summary>
    public int AcPowerOutSockets => ParseInt("a6", begin: 1);

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

    /// <summary>DC power out for port 1, or the default int value.</summary>
    public int Dc1PowerOut => ParseInt("ac", begin: 1);

    /// <summary>DC power out for port 2, or the default int value.</summary>
    public int Dc2PowerOut => ParseInt("ad", begin: 1);

    /// <summary>Total solar power in, or the default int value.</summary>
    public int SolarPowerIn => ParseInt("ae", begin: 1);

    /// <summary>Total AC power in, or the default int value.</summary>
    public int AcPowerIn => ParseInt("af", begin: 1);

    /// <summary>Total AC power out, or the default int value.</summary>
    public int AcPowerOut => ParseInt("b0", begin: 1);

    /// <summary>Main software version, or the default string value.</summary>
    public string SoftwareVersion => HasData
        ? string.Join(".", ParseInt("b3", begin: 1).ToString().Select(digit => digit.ToString()))
        : SolixConst.DefaultMetadataString;

    /// <summary>
    /// Software version of any expansion batteries ("0" if there is no
    /// expansion battery), or the default string value.
    /// </summary>
    public string SoftwareVersionExpansion => HasData
        ? string.Join(".", ParseInt("b9", begin: 1).ToString().Select(digit => digit.ToString()))
        : SolixConst.DefaultMetadataString;

    /// <summary>Software version of the controller, or the default string value.</summary>
    public string SoftwareVersionController => HasData
        ? string.Join(".", ParseInt("ba", begin: 1).ToString().Select(digit => digit.ToString()))
        : SolixConst.DefaultMetadataString;

    /// <summary>Temperature of the unit in degrees C.</summary>
    public int Temperature => ParseInt("bd", begin: 1, signed: true);

    /// <summary>
    /// Temperature of the expansion battery in degrees C, 0 if not present,
    /// or the default int value.
    /// </summary>
    public int TemperatureExpansion => ParseInt("be", begin: 1, signed: true);

    /// <summary>Percentage charge of battery, or the default int value.</summary>
    public int BatteryPercentage => ParseInt("c1", begin: 1);

    /// <summary>
    /// Percentage charge of the expansion battery, 0 if not present,
    /// or the default int value.
    /// </summary>
    public int BatteryPercentageExpansion => ParseInt("c2", begin: 1);

    /// <summary>Battery health as a percentage, or the default int value.</summary>
    public int BatteryHealth => ParseInt("c3", begin: 1);

    /// <summary>
    /// Battery health of the expansion battery as a percentage, 0 if not
    /// present, or the default int value.
    /// </summary>
    public int BatteryHealthExpansion => ParseInt("c4", begin: 1);

    /// <summary>Number of expansion batteries, or the default int value.</summary>
    public int NumExpansion => ParseInt("c5", begin: 1);

    /// <summary>Device serial number, or the default string value.</summary>
    public string SerialNumber => ParseString("d0", begin: 1);

    /// <summary>This model does not report a total power out value.</summary>
    int ISolixUpsDevice.PowerOut => SolixConst.DefaultMetadataInt;
}
