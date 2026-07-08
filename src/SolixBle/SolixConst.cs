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

    /// <summary>Command used to initiate negotiations.</summary>
    public const string NegotiationCommand0 =
        "ff0936000300010001a10442ad8c69a22462326463306231372d623735642d346162662d626136652d656337633939376332336537b9";

    /// <summary>Response to receiving 1st negotiation message.</summary>
    public const string NegotiationCommand1 =
        "ff093d000300010003a10442ad8c69a22462326463306231372d623735642d346162662d626136652d656337633939376332336537a30120a40200f064";

    /// <summary>Response to receiving 2nd negotiation message.</summary>
    public const string NegotiationCommand2 =
        "ff0936000300010029a10442ad8c69a22462326463306231372d623735642d346162662d626136652d65633763393937633233653791";

    /// <summary>Response to receiving 3rd negotiation message.</summary>
    public const string NegotiationCommand3 =
        "ff0940000300010005a10443ad8c69a22462326463306231372d623735642d346162662d626136652d656337633939376332336537a30120a40200f0a50140fa";

    /// <summary>Response to receiving 4th negotiation message.</summary>
    public const string NegotiationCommand4 =
        "ff094c000300010021a140060ea168f232aedb37fb2d120c49180329ac72ab5ec3eb8fd30a2f252dc5e151dabccd9b1dc1e288704ca760a0d8c918e5c94823a1f609a4bf07fb4c33ee219085";

    /// <summary>Response to receiving 5th negotiation message.</summary>
    public const string NegotiationCommand5 =
        "ff095a000300014022580bc0532a53c739adf3da7b994a7b5f221bcc16bab6392c215cb4faaf41d9d58e2c81c016e474c78eed5569147cb74a1f22ca2b3fad2e209dbbcfbdaca352034a6c479f055f68581b5f1e22348809f526";

    /// <summary>
    /// The unix timestamp (hex, little-endian) that is agreed upon in the negotiations.
    /// Used to protect against replay attacks: commands must contain the current encrypted time.
    /// </summary>
    public const string BaseTimestamp = "42ad8c69";

    /// <summary>
    /// The private key used to perform the ECDH negotiation to get a shared secret
    /// which is then used as an AES key/IV for encrypting communications.
    /// </summary>
    public const string PrivateKey = "7dfbea61cd95cee49c458ad7419e817f1ade9a66136de3c7d5787af1458e39f4";

    /// <summary>X coordinate of the public key matching <see cref="PrivateKey"/> (as sent in negotiation command 4).</summary>
    public const string ClientPublicQx = "060ea168f232aedb37fb2d120c49180329ac72ab5ec3eb8fd30a2f252dc5e151";

    /// <summary>Y coordinate of the public key matching <see cref="PrivateKey"/> (as sent in negotiation command 4).</summary>
    public const string ClientPublicQy = "dabccd9b1dc1e288704ca760a0d8c918e5c94823a1f609a4bf07fb4c33ee2190";
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
