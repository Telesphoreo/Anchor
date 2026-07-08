namespace SolixBle;

/// <summary>The status of a port on the device.</summary>
public enum PortStatus
{
    /// <summary>The status of the port is unknown.</summary>
    Unknown = -1,

    /// <summary>The port is not connected / off.</summary>
    NotConnected = 0,

    /// <summary>The port is an output / on.</summary>
    Output = 1,

    /// <summary>The port is an input / on.</summary>
    Input = 2,
}

/// <summary>Helpers for <see cref="PortStatus"/>.</summary>
public static class PortStatusExtensions
{
    /// <summary>
    /// Custom factory for ports which only support being inputs.
    /// If the value would be an output (i.e. 1) it is mapped to input (i.e. 2).
    /// </summary>
    public static PortStatus FromInputOnly(int value) =>
        value == (int)PortStatus.Output ? PortStatus.Input : (PortStatus)value;
}

/// <summary>The status of charging/discharging on a device.</summary>
public enum ChargingStatus
{
    /// <summary>The status is unknown.</summary>
    Unknown = -1,

    /// <summary>The device is idle (battery not charging or discharging).</summary>
    Idle = 0,

    /// <summary>The device is discharging.</summary>
    Discharging = 1,

    /// <summary>The device is charging.</summary>
    Charging = 2,
}

/// <summary>The charging type of an F3800.</summary>
public enum ChargingStatusF3800
{
    /// <summary>The status is unknown.</summary>
    Unknown = -1,

    /// <summary>The device is idle.</summary>
    Inactive = 0,

    /// <summary>The device is charging via solar.</summary>
    Solar = 1,

    /// <summary>The device is charging via AC.</summary>
    Ac = 2,

    /// <summary>The device is charging via solar and AC.</summary>
    Both = 3,
}

/// <summary>The status of the light on the device.</summary>
public enum LightStatus
{
    /// <summary>The status of the light is unknown.</summary>
    Unknown = -1,

    /// <summary>The light is off.</summary>
    Off = 0,

    /// <summary>The light is on low.</summary>
    Low = 1,

    /// <summary>The light is on medium.</summary>
    Medium = 2,

    /// <summary>The light is on high.</summary>
    High = 3,

    /// <summary>SOS mode. Not supported by all devices.</summary>
    Sos = 4,
}

/// <summary>The light mode of the device.</summary>
public enum LightMode
{
    /// <summary>The light mode is unknown.</summary>
    Unknown = -1,

    /// <summary>Normal light mode.</summary>
    Normal = 0,

    /// <summary>Mood light mode.</summary>
    Mood = 1,
}

/// <summary>Display timeout on device in seconds. Only specific values are allowed.</summary>
public enum DisplayTimeout
{
    /// <summary>The status of the display timeout is unknown.</summary>
    Unknown = -1,

    /// <summary>20 seconds.</summary>
    S20 = 20,

    /// <summary>30 seconds.</summary>
    S30 = 30,

    /// <summary>60 seconds.</summary>
    S60 = 60,

    /// <summary>300 seconds (5m).</summary>
    S300 = 300,

    /// <summary>1800 seconds (30m).</summary>
    S1800 = 1800,
}

/// <summary>The status of the temperature unit of the device.</summary>
public enum TemperatureUnit
{
    /// <summary>The display unit is unknown.</summary>
    Unknown = -1,

    /// <summary>Display unit is Celsius.</summary>
    Celsius = 0,

    /// <summary>Display unit is Fahrenheit.</summary>
    Fahrenheit = 1,
}

/// <summary>The grid connection status.</summary>
public enum GridStatus
{
    /// <summary>The grid status is unknown.</summary>
    Unknown = -1,

    /// <summary>Grid is connected and OK.</summary>
    Ok = 1,

    /// <summary>
    /// Undocumented in API, but device operates as expected and outputs power to
    /// grid. Maybe a pure "dispense" state because SB2 can't draw power from the grid.
    /// </summary>
    OkAsWellIGuess = 2,

    /// <summary>Grid is connecting.</summary>
    Connecting = 3,

    /// <summary>No grid connection.</summary>
    NoGrid = 6,
}

/// <summary>The overload status of a port.</summary>
public enum PortOverload
{
    /// <summary>Overload status is unknown.</summary>
    Unknown = -1,

    /// <summary>No overload event.</summary>
    None = 0,

    /// <summary>USB C1 overload detected.</summary>
    UsbC1 = 8,

    /// <summary>USB C2 overload detected.</summary>
    UsbC2 = 9,

    /// <summary>USB C3 overload detected.</summary>
    UsbC3 = 10,
}
