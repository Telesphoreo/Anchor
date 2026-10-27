namespace SolixBle;

/// <summary>Constants for the SolixBle library (port of SolixBLE <c>const.py</c>).</summary>
internal static class SolixConst
{
    /// <summary>GATT characteristic UUID for device telemetry. Is subscribable.</summary>
    public static readonly Guid UuidTelemetry = new("8c850003-0302-41c5-b46e-cf057c562025");

    /// <summary>GATT characteristic UUID for sending commands / negotiating.</summary>
    public static readonly Guid UuidCommand = new("8c850002-0302-41c5-b46e-cf057c562025");

    /// <summary>GATT service UUID for identifying Solix/Prime devices.</summary>
    public static readonly Guid UuidIdentifier = new("0000ff09-0000-1000-8000-00805f9b34fb");

    /// <summary>Time to wait before re-connecting on an unexpected disconnect.</summary>
    public static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(3);

    /// <summary>Maximum number of automatic re-connection attempts (-1 = unlimited).</summary>
    public const int ReconnectAttemptsMax = -1;

    /// <summary>
    /// Time to allow for a re-connect before considering the device to be
    /// disconnected and running state changed callbacks.
    /// </summary>
    public static readonly TimeSpan DisconnectTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Time to allow for encryption negotiation before timing out.</summary>
    public static readonly TimeSpan NegotiationTimeout = TimeSpan.FromSeconds(90);

    /// <summary>Maximum time to get no response in any negotiation stage before retrying.</summary>
    public static readonly TimeSpan NegotiationResponseTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Maximum time to get no response in the 1st negotiation stage before retrying.</summary>
    public static readonly TimeSpan NegotiationResponseDelay = TimeSpan.FromSeconds(10);

    /// <summary>String value for unknown string attributes.</summary>
    public const string DefaultMetadataString = "Unknown";

    /// <summary>Int value for unknown int attributes.</summary>
    public const int DefaultMetadataInt = -1;

    /// <summary>Float value for unknown float attributes.</summary>
    public const double DefaultMetadataFloat = -1.0;

    /// <summary>Bool value for unknown boolean attributes.</summary>
    public static readonly bool? DefaultMetadataBool = null;

    /// <summary>The client identifier sent to the device during negotiation (and registered in x027).</summary>
    public const string ClientId = "b2dc0b17-b75d-4abf-ba6e-ec7c997c23e7";

    /// <summary>The pattern of negotiation packets sent to the device.</summary>
    public static readonly byte[] NegotiationPattern = [0x03, 0x00, 0x01];

    /// <summary>The pattern of session commands sent to the device.</summary>
    public static readonly byte[] CommandPattern = [0x03, 0x00, 0x0F];

    /// <summary>Pattern channel of negotiation requests, replies and the grant.</summary>
    public const byte ChannelNegotiation = 0x01;

    /// <summary>Pattern channel of session commands, replies and pushes.</summary>
    public const byte ChannelSession = 0x0F;

    /// <summary>Pattern channel of app session pushes.</summary>
    public const byte ChannelApp = 0x11;

    /// <summary>Message type handled as telemetry on every model ("0300" / "4300").</summary>
    public const int TelemetryMsgType = 0x300;

    /// <summary>TLV tags start at 0xa1; a lower first payload byte is a reply's status.</summary>
    public const byte StatusLimit = 0xA1;

    /// <summary>
    /// Static AES-GCM key, nonce and additional authenticated data of the
    /// encrypted negotiation outer before the ECDH shared secret exists.
    /// </summary>
    public const string NegotiationKey = "b8ff7422955d4eb6d554a2c470280559";

    /// <inheritdoc cref="NegotiationKey"/>
    public const string NegotiationNonce = "6ba3e3f2f3a60f2971ce5d1f";

    /// <inheritdoc cref="NegotiationKey"/>
    public const string NegotiationAad = "3322110077665544bbaa9988ffeeddcc";

    /// <summary>POSIX time zone sent to the device if the system time zone cannot be expressed as one.</summary>
    public const string FallbackTimeZone = "GMT0BST,M3.5.0/1,M10.5.0";

    /// <summary>BLE company identifier Anker publishes its advertisement record under.</summary>
    public const ushort AnkerCompanyId = 0xFFFF;

    /// <summary>Advertised capability bit: the device accepts the encrypted (4xxx) negotiation.</summary>
    public const byte CapabilityEncryptedEcdh = 0x04;
}

/// <summary>
/// Pluggable diagnostics sink for the SolixBle library. Assign <see cref="Sink"/>
/// to receive (level, message) pairs; a null sink discards all logging.
/// </summary>
public static class SolixLog
{
    /// <summary>Diagnostics sink receiving (level, message). Null = discard.</summary>
    public static Action<string, string>? Sink { get; set; }

    public static void Debug(string message) => Sink?.Invoke("DEBUG", message);

    public static void Info(string message) => Sink?.Invoke("INFO", message);

    public static void Warning(string message) => Sink?.Invoke("WARNING", message);

    public static void Error(string message) => Sink?.Invoke("ERROR", message);
}
