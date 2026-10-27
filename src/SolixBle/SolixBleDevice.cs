using System.Text;

namespace SolixBle;

/// <summary>
/// Solix BLE device object (port of SolixBLE <c>device.py</c>). Handles the
/// packet routing, TLV telemetry parsing, fragment reassembly, the encryption
/// negotiation (plain or encrypted outer), encrypted command sending and
/// automatic reconnection.
/// </summary>
public class SolixBleDevice : IAsyncDisposable
{
    /// <summary>
    /// Command codes (hex) that carry telemetry for this device. Subclasses can
    /// override this if their model uses different telemetry command codes
    /// (e.g. the C1000 Gen 2 uses "c421"/"c900" instead of "c402"/"c405").
    /// </summary>
    private static readonly string[] DefaultTelemetryCommands = ["c402", "4300", "c405"];

    private readonly ulong _bluetoothAddress;
    private string? _bleName;

    private IGattLink? _client;
    private Dictionary<string, List<byte[]>> _fragmentBuffers = new();
    private Dictionary<string, byte[]>? _data;
    private DateTimeOffset? _lastDataTimestamp;
    private DateTimeOffset? _lastPacketTimestamp;
    private Dictionary<string, List<TaskCompletionSource<byte[]>>> _packetFutures = new();
    private readonly object _futuresGate = new();
    private readonly object _notificationGate = new();
    private Task _notificationChain = Task.CompletedTask;
    private Task? _autoReconnectTask;
    private CancellationTokenSource? _autoReconnectCts;
    private readonly AsyncEvent _disconnectEvent = new();
    private int _connectionAttempts;
    private HashSet<int>? _telemetryMsgTypes;

    /// <summary>
    /// Latched when the transport reports a lost link for the current client;
    /// cleared per connect attempt. Needed because MaintainConnection lets the
    /// OS silently re-link (flipping Connected back to true) while our GATT
    /// handles from the dead session remain stale.
    /// </summary>
    private volatile bool _linkLost;

    /// <summary>
    /// The latest negotiation. Kept across a drop so <see cref="ConnectAsync"/>
    /// can still tell whether the device answered it; a new connection replaces it.
    /// </summary>
    private NegotiatedSession? _session;

    /// <summary>
    /// The outer protocol to open with: the advertised hint, else plain, then
    /// whichever outer last authorized on this instance.
    /// </summary>
    private NegotiationOuter _outer = NegotiationOuter.Plain;

    /// <summary>Why <see cref="_outer"/> was chosen, for the negotiation log line.</summary>
    private string _outerReason = "default";

    /// <summary>Initialise device object. Does not connect automatically.</summary>
    /// <param name="bluetoothAddress">Bluetooth address of the device.</param>
    /// <param name="name">Bluetooth name of the device, if known.</param>
    public SolixBleDevice(ulong bluetoothAddress, string? name = null)
    {
        _bluetoothAddress = bluetoothAddress;
        _bleName = name;
        SolixLog.Debug($"Initializing Solix device '{Name}' with address '{Address}'");
    }

    /// <summary>
    /// Initialise device object from a scan result. Does not connect
    /// automatically. The advertised capability byte, if any, tells which
    /// negotiation the device accepts; without it plain is tried first and
    /// corrected if refused.
    /// </summary>
    public SolixBleDevice(DiscoveredDevice device) : this(device.Address, device.Name)
    {
        if (device.Capability is not { } capability)
            return;
        _outer = (capability & SolixConst.CapabilityEncryptedEcdh) != 0
            ? NegotiationOuter.Encrypted
            : NegotiationOuter.Plain;
        _outerReason = $"advertised capability {capability:x2}";
    }

    /// <summary>
    /// Raised on state updates. Triggers include changes to pretty much anything,
    /// including battery percentage, output power, solar, connection status, etc.
    /// Subscriber exceptions are caught and logged. May fire on any thread.
    /// </summary>
    public event Action? StateChanged;

    /// <summary>
    /// Connected to device. This does not mean that an encrypted connection has
    /// been established or that any data values have been populated; use
    /// <see cref="Available"/> to determine that.
    /// </summary>
    public bool Connected => _client is { IsConnected: true };

    /// <summary>
    /// Has an encrypted session been successfully negotiated. This does not mean
    /// that any data values have been populated; use <see cref="Available"/> for that.
    /// </summary>
    public bool Negotiated => Connected && !_linkLost && _session is { Authorized: true };

    /// <summary>Connected to device and telemetry data is available.</summary>
    public bool Available => Negotiated && _data is not null;

    /// <summary>The Bluetooth MAC address of the device ("AA:BB:CC:DD:EE:FF").</summary>
    public string Address => SolixScanner.AddressToMac(_bluetoothAddress);

    /// <summary>The Bluetooth name of the device or the default string value.</summary>
    public string Name => string.IsNullOrEmpty(_bleName) ? SolixConst.DefaultMetadataString : _bleName;

    /// <summary>Timestamp of last telemetry data update from device, or null.</summary>
    public DateTimeOffset? LastUpdate => _lastDataTimestamp;

    /// <summary>True when telemetry data has been received (the store is populated).</summary>
    protected bool HasData => _data is not null;

    /// <summary>True when the latest telemetry contains the given parameter key.</summary>
    protected bool HasParameter(string key) => _data?.ContainsKey(key) == true;

    /// <summary>
    /// Optional delay between establishing the BLE connection and sending the
    /// first negotiation request. Some device firmwares need a beat after the
    /// notification subscription before they accept command writes.
    /// </summary>
    public TimeSpan NegotiationStartDelay { get; set; }

    /// <summary>
    /// Whether the first negotiation command is sent as a write-with-response
    /// (default, matching upstream). Some newer firmwares (e.g. C1000 Gen 2)
    /// reject a write-with-response on the command characteristic by dropping
    /// the link; set false to use write-without-response for diagnostics.
    /// </summary>
    public bool NegotiationInitWithResponse { get; set; } = true;

    /// <summary>
    /// Whether to hold a WinRT GattSession with MaintainConnection = true so the
    /// OS keeps the BLE link alive across idle periods. Default true. Diagnostic
    /// toggle: some firmwares may react badly to the OS-driven connection
    /// management this enables. Windows only; BlueZ holds the link until it is
    /// disconnected.
    /// </summary>
    public bool MaintainConnection { get; set; } = true;

    /// <summary>
    /// Command codes (hex) that carry telemetry for this device. Subclasses can
    /// override this if their model uses different telemetry command codes.
    /// Matched on the message type whatever the fragment and encryption flags.
    /// </summary>
    protected virtual IReadOnlyList<string> TelemetryCommands => DefaultTelemetryCommands;

    /// <summary>
    /// Some firmwares advance from stage 0 to stage 1, drop the Windows BLE
    /// link before sending the stage-1 notification, then accept the stage-1
    /// response after reconnecting. When set, the first such drop on the plain
    /// outer reconnects and resumes at x003 before the drop is treated as a
    /// refusal of the plain outer.
    /// </summary>
    protected virtual bool ResumeNegotiationAfterStage0Disconnect => false;

    /// <summary>
    /// Connect to device. This will connect to the device, negotiate an encrypted
    /// session and subscribe to status updates, returning true if successful.
    /// If the device drops the link before answering the plain negotiation
    /// (how hardened firmware refuses it), this reconnects once with the
    /// encrypted negotiation and remembers which one worked.
    /// </summary>
    /// <param name="maxAttempts">Maximum number of attempts to try to connect (default 3).</param>
    /// <param name="runCallbacks">Execute registered callbacks on successful connection (default true).</param>
    /// <param name="ct">Cancellation token aborting the connection attempt.</param>
    public Task<bool> ConnectAsync(int maxAttempts = 3, bool runCallbacks = true, CancellationToken ct = default) =>
        ConnectAsync(maxAttempts, runCallbacks, _outer, _outerReason, fallback: false, ct);

    /// <summary>Connect and negotiate on the given outer protocol.</summary>
    /// <param name="fallback">Whether this is the encrypted reopen after a refused plain outer.</param>
    private async Task<bool> ConnectAsync(
        int maxAttempts,
        bool runCallbacks,
        NegotiationOuter outer,
        string outerReason,
        bool fallback,
        CancellationToken ct)
    {
        _connectionAttempts++;

        try
        {
            // If we have an old client get rid of it.
            if (_client is not null)
                await DisposeOfClientAsync().ConfigureAwait(false);

            // Reset negotiated details but keep any data.
            ResetSession(resetData: false);
            _session = null;

            // Make a new client and connect (resolve device, find characteristics,
            // subscribe to notifications).
            await ConnectTransportAsync(maxAttempts, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SolixLog.Error($"Error establishing initial connection to '{Name}'! {ex}");
        }

        // If we are still not connected then we have failed.
        if (!Connected)
        {
            SolixLog.Error($"Failed to establish initial connection to '{Name}' on attempt {_connectionAttempts}!");
            return false;
        }

        // Devices are often created from a saved address without a name; adopt
        // the OS-known device name once the transport has resolved it.
        if (string.IsNullOrEmpty(_bleName) && _client?.DeviceName is { Length: > 0 } osName)
            _bleName = osName;

        SolixLog.Debug(
            $"Established initial connection to '{Name}' on attempt {_connectionAttempts}" +
            $" (ATT MTU {(_client?.Mtu is > 0 and var mtu ? mtu.ToString() : "unknown")})!");

        // Optional settle delay before the first negotiation write.
        if (NegotiationStartDelay > TimeSpan.Zero)
        {
            SolixLog.Debug($"Waiting {NegotiationStartDelay.TotalMilliseconds:0} ms before starting negotiation...");
            await Task.Delay(NegotiationStartDelay, ct).ConfigureAwait(false);
        }

        // Negotiate.
        var resumed = await NegotiateAsync(outer, outerReason, fallback, ct).ConfigureAwait(false);
        if (resumed is null)
            return await ReopenIfRefusedAsync(maxAttempts, runCallbacks, outer, ct).ConfigureAwait(false);

        // Use the same outer first on the next connection to this device.
        _outer = _session!.Outer;
        _outerReason = "remembered from the last authorized connection";

        // If negotiations succeeded.
        SolixLog.Debug(
            $"Negotiations with '{Name}' succeeded on the {NegotiatedSession.OuterName(_outer)} outer" +
            $"{(resumed.Value ? " after resuming at stage 1" : "")}!");
        _connectionAttempts = 0;

        // Clear disconnect event if set.
        _disconnectEvent.Clear();

        // Run any device-specific post-connect setup (e.g. sending a subscribe
        // command to start telemetry). This runs on every (re)connection. Errors
        // are logged but do not abort the connection; the automatic reconnect
        // task will retry.
        try
        {
            await PostConnectAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SolixLog.Error($"Error running post-connect setup for '{Name}'! {ex}");
        }

        // Start an automatic reconnect task if it's not running already.
        if (_autoReconnectTask is null)
        {
            _autoReconnectCts = new CancellationTokenSource();
            var token = _autoReconnectCts.Token;
            _autoReconnectTask = Task.Run(() => AutoReconnectAsync(token), CancellationToken.None);
        }

        // Execute callbacks if enabled.
        if (runCallbacks)
            RunStateChangedCallbacks();

        return true;
    }

    /// <summary>
    /// Run the negotiation until it succeeds, fails, or the link drops.
    /// </summary>
    /// <param name="fallback">
    /// Give up once the opening goes unanswered for a response timeout, so a
    /// device that ignores the encrypted outer returns to auto-reconnect (and
    /// the plain outer) without waiting out the whole negotiation timeout.
    /// </param>
    /// <returns>
    /// Null if not negotiated, else whether the negotiation was resumed at
    /// stage 1 after a stage-0 disconnect.
    /// </returns>
    private async Task<bool?> NegotiateAsync(
        NegotiationOuter outer, string outerReason, bool fallback, CancellationToken ct)
    {
        SolixLog.Debug(
            $"Negotiating with '{Name}' on the {NegotiatedSession.OuterName(outer)} outer ({outerReason})");

        try
        {
            using var negotiationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var deadline = DateTimeOffset.UtcNow + SolixConst.NegotiationTimeout;
            negotiationCts.CancelAfter(SolixConst.NegotiationTimeout);
            var opened = false;
            var resumed = false;
            var extended = false;

            // While negotiations have not completed.
            while (!Negotiated)
            {
                // The link can die mid-negotiation; bail out instead of
                // spinning against a dead client until the overall timeout.
                // Connected alone is not enough: MaintainConnection makes the
                // OS silently re-link, but our characteristic handles from the
                // dead session stay stale; a fresh ConnectAsync is required.
                if (_linkLost || !Connected)
                {
                    if (ResumeNegotiationAfterStage0Disconnect
                        && opened
                        && !resumed
                        && outer == NegotiationOuter.Plain)
                    {
                        SolixLog.Warning(
                            $"Connection to '{Name}' was lost after negotiation stage 0" +
                            $" (device {(_session is { Replied: true } ? "had" : "had not")} replied);" +
                            " reconnecting to resume at stage 1...");
                        resumed = true;

                        await DisposeOfClientAsync().ConfigureAwait(false);
                        ResetSession(resetData: false);
                        try
                        {
                            await ConnectTransportAsync(maxAttempts: 1, negotiationCts.Token).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            SolixLog.Debug($"Reconnect to '{Name}' for negotiation resume failed: {ex.Message}");
                        }

                        if (!Connected)
                        {
                            SolixLog.Error($"Failed to reconnect to '{Name}' for negotiation resume!");
                            return null;
                        }

                        SolixLog.Debug($"Sending negotiation stage 1 resume request (0003) to '{Name}'...");
                        _session = NewSession(NegotiationOuter.Plain);
                        try
                        {
                            await _session.SendCapabilityRequestAsync().ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            SolixLog.Warning($"Failed to send negotiation resume request to '{Name}': {ex.Message}");
                        }

                        _lastPacketTimestamp = DateTimeOffset.UtcNow;
                        continue;
                    }

                    SolixLog.Error($"Connection to '{Name}' was lost during negotiation!");
                    return null;
                }

                // If we have not received any packet from the device in any
                // stage then restart negotiations from the start.
                if (NegotiationShouldRestart())
                {
                    if (fallback && opened && _session is { Replied: false })
                    {
                        SolixLog.Warning($"'{Name}' did not answer the encrypted negotiation either");
                        return null;
                    }

                    SolixLog.Debug($"Sending negotiation initiation request to '{Name}'...");
                    try
                    {
                        await InitiateNegotiationsAsync(outer).ConfigureAwait(false);
                        opened = true;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // A failed write usually means the link just dropped;
                        // the Connected check above ends the loop next pass.
                        SolixLog.Warning($"Failed to send negotiation request to '{Name}': {ex.Message}");
                    }
                }

                // Wait to see if we get any response to our initial request.
                // This weird layout allows us to exit immediately when
                // negotiation occurs.
                for (var i = 0; i < (int)SolixConst.NegotiationResponseTimeout.TotalSeconds; i++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), negotiationCts.Token).ConfigureAwait(false);

                    // A device waiting for its button gets its first
                    // confirmation window before the negotiation times out.
                    if (!extended
                        && _session?.ConfirmationDeadline is { } until
                        && until + SolixConst.NegotiationResponseTimeout > deadline)
                    {
                        extended = true;
                        deadline = until + SolixConst.NegotiationResponseTimeout;
                        negotiationCts.CancelAfter(deadline - DateTimeOffset.UtcNow);
                        SolixLog.Debug($"Extended the negotiation with '{Name}' to the end of its confirmation window");
                    }

                    if (Negotiated || _linkLost || !Connected)
                        break;
                }
            }

            return resumed;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            SolixLog.Error($"Timed out attempting to negotiate with '{Name}'!");
            return null;
        }
    }

    /// <summary>
    /// Whether the opening request should be sent (again): the device has sent
    /// nothing, or nothing for <see cref="SolixConst.NegotiationResponseTimeout"/>,
    /// and is not waiting for its button.
    /// </summary>
    private bool NegotiationShouldRestart()
    {
        if (_session?.ConfirmationDeadline is { } until && DateTimeOffset.UtcNow < until)
            return false;
        var lastPacket = _lastPacketTimestamp;
        return lastPacket is null || DateTimeOffset.UtcNow - lastPacket.Value > SolixConst.NegotiationResponseTimeout;
    }

    /// <summary>
    /// Reconnect with the encrypted outer if the device refused the plain one.
    /// Dropping the link before any reply to the plain opening (or to the
    /// stage-1 resume) is how hardened firmware refuses it. The outer changes
    /// at most once per connect and never steps down from encrypted. The
    /// encrypted outer is only remembered once it authorizes, so a failed
    /// reopen leaves the next connect on the outer that last worked.
    /// </summary>
    /// <param name="outer">The outer the failed negotiation ran on.</param>
    /// <returns>The reconnection's result, or false if nothing was refused.</returns>
    private async Task<bool> ReopenIfRefusedAsync(
        int maxAttempts, bool runCallbacks, NegotiationOuter outer, CancellationToken ct)
    {
        var replied = _session is { Replied: true };
        if ((!_linkLost && Connected) || replied || outer != NegotiationOuter.Plain)
            return false;

        SolixLog.Info($"'{Name}' refused the plain negotiation, reopening with the encrypted one...");
        return await ConnectAsync(
            maxAttempts, runCallbacks, NegotiationOuter.Encrypted, "the plain outer was refused", fallback: true, ct)
            .ConfigureAwait(false);
    }

    private async Task ConnectTransportAsync(int maxAttempts, CancellationToken ct)
    {
        var attempts = Math.Max(1, maxAttempts);
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            var client = BleTransport.CreateLink(_bluetoothAddress, MaintainConnection);
            client.NotificationReceived += OnNotificationReceived;
            client.Disconnected += OnClientDisconnected;
            _client = client;
            _linkLost = false;
            try
            {
                await client.ConnectAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                SolixLog.Debug($"Connection attempt {attempt}/{attempts} to '{Name}' failed: {ex.Message}");
                await DisposeOfClientAsync().ConfigureAwait(false);
                if (attempt >= attempts)
                    throw;
            }
        }
    }

    /// <summary>
    /// Run device-specific setup after a negotiated connection is established.
    /// Called by <see cref="ConnectAsync"/> once the encrypted session has been
    /// negotiated (so <see cref="SendCommandAsync"/> may be used) and on every
    /// automatic reconnect. The default implementation does nothing.
    /// </summary>
    protected virtual Task PostConnectAsync() => Task.CompletedTask;

    /// <summary>
    /// Disconnect from device and reset internal state, including connection
    /// attempts. Cancels the automatic reconnection task and will not execute
    /// state changed callbacks.
    /// </summary>
    public async Task DisconnectAsync()
    {
        // Cancel the automatic reconnection task.
        if (_autoReconnectCts is not null)
        {
            _autoReconnectCts.Cancel();
            _autoReconnectCts.Dispose();
            _autoReconnectCts = null;
            _autoReconnectTask = null;
        }

        // If there is a client disconnect and throw it away.
        if (_client is not null)
            await DisposeOfClientAsync().ConfigureAwait(false);

        // Reset session.
        _connectionAttempts = 0;
        ResetSession();
        _session = null;
    }

    /// <inheritdoc cref="DisconnectAsync"/>
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Parse an integer at the specified key in the telemetry data.
    /// Slicing follows Python semantics (indices clamped, null = open end);
    /// an empty slice decodes to 0.
    /// </summary>
    /// <param name="key">Key of parameter the int is in (e.g. "a1", "a2", ...).</param>
    /// <param name="begin">Slice bytes from this index.</param>
    /// <param name="end">Slice bytes to this index.</param>
    /// <param name="signed">If the integer is signed.</param>
    /// <returns>Integer or default int value if no data.</returns>
    /// <exception cref="KeyNotFoundException">If key does not exist.</exception>
    protected int ParseInt(string key, int? begin = null, int? end = null, bool signed = false)
    {
        if (_data is null)
            return SolixConst.DefaultMetadataInt;
        var intBytes = Slice(_data[key], begin, end);
        return (int)IntFromBytesLittleEndian(intBytes, signed);
    }

    /// <summary>
    /// Parse ASCII text at the specified key in the telemetry data.
    /// </summary>
    /// <param name="key">Key of parameter the string is in (e.g. "a1", "a2", ...).</param>
    /// <param name="begin">Slice bytes from this index.</param>
    /// <param name="end">Slice bytes to this index.</param>
    /// <returns>String of parsed data from telemetry or default string if no data.</returns>
    /// <exception cref="KeyNotFoundException">If key does not exist.</exception>
    protected string ParseString(string key, int? begin = null, int? end = null)
    {
        if (_data is null || _data.Count == 0)
            return SolixConst.DefaultMetadataString;
        return Encoding.ASCII.GetString(Slice(_data[key], begin, end));
    }

    /// <summary>Slice a byte array with Python slice semantics (clamped, null = open end).</summary>
    private static byte[] Slice(byte[] data, int? begin, int? end)
    {
        var length = data.Length;
        var start = begin ?? 0;
        var stop = end ?? length;
        if (start < 0)
            start += length;
        if (stop < 0)
            stop += length;
        start = Math.Clamp(start, 0, length);
        stop = Math.Clamp(stop, 0, length);
        return stop <= start ? [] : data[start..stop];
    }

    /// <summary>
    /// Little-endian integer decode of arbitrary length (1-8 bytes in practice).
    /// An empty input decodes to 0.
    /// </summary>
    private static long IntFromBytesLittleEndian(ReadOnlySpan<byte> bytes, bool signed)
    {
        long result = 0;
        var count = Math.Min(bytes.Length, 8);
        for (var i = count - 1; i >= 0; i--)
            result = (result << 8) | bytes[i];
        if (signed && count > 0 && count < 8 && (bytes[count - 1] & 0x80) != 0)
            result -= 1L << (count * 8);
        return result;
    }

    /// <summary>
    /// Parse payload bytes into parameters. Payloads contain an optional reply
    /// status byte and a list of parameters with a format of:
    /// &lt;id 1B&gt; &lt;len 1B&gt; &lt;data nB&gt;. A truncated parameter prevents all
    /// further parameters from being parsed and is logged, but the successfully
    /// parsed parameters (if any) are returned.
    /// </summary>
    /// <param name="payload">Plain-text payload to parse into parameters.</param>
    /// <returns>Dictionary mapping parameter ids ("a1", "a2", ...) to data.</returns>
    protected IReadOnlyDictionary<string, byte[]> ParsePayload(byte[] payload) =>
        SolixFrame.ParseParameters(payload, out _);

    private static string ParametersToStr(IReadOnlyDictionary<string, byte[]> parameters, bool types = false)
    {
        if (!types)
            return SolixFrame.FormatParameters(parameters);

        var sb = new StringBuilder("{\n");
        foreach (var (key, value) in parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var body = Slice(value, 1, null);
            sb.Append(
                $"    \"{key}\": {{ \"hex\": \"{Convert.ToHexStringLower(value)}\"," +
                $" \"uint\": {IntFromBytesLittleEndian(body, false)}," +
                $" \"int\": {IntFromBytesLittleEndian(body, true)} }},\n");
        }

        sb.Append('}');
        return sb.ToString();
    }

    /// <summary>Log any differences between parameters.</summary>
    private static void LogDiff(
        IReadOnlyDictionary<string, byte[]> old, IReadOnlyDictionary<string, byte[]> updated)
    {
        var sb = new StringBuilder("Parameter changes:\n");
        foreach (var key in old.Keys.Intersect(updated.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            var oldValue = old[key];
            var newValue = updated[key];
            if (oldValue.AsSpan().SequenceEqual(newValue))
                continue;
            var oldBody = Slice(oldValue, 1, null);
            var newBody = Slice(newValue, 1, null);
            sb.Append(
                $"    \"{key}\": hex: {Convert.ToHexStringLower(oldValue)} -> {Convert.ToHexStringLower(newValue)}," +
                $" uint: {IntFromBytesLittleEndian(oldBody, false)} -> {IntFromBytesLittleEndian(newBody, false)}," +
                $" int: {IntFromBytesLittleEndian(oldBody, true)} -> {IntFromBytesLittleEndian(newBody, true)}\n");
        }

        SolixLog.Debug(sb.ToString());
    }

    /// <summary>
    /// Re-assemble a fragmented payload. Each fragment's first byte is
    /// index &lt;&lt; 4 | total; fragments must arrive in order (an out-of-order
    /// one clears the buffer).
    /// </summary>
    /// <returns>Payload bytes once all fragments are available, else null.</returns>
    private byte[]? Reassemble(byte[] pattern, byte[] cmd, byte[] payload)
    {
        var key = Convert.ToHexStringLower(pattern) + Convert.ToHexStringLower(cmd);
        if (payload.Length == 0)
        {
            SolixLog.Debug($"Dropping empty fragment for {key}");
            return null;
        }

        var fragmentIndex = (payload[0] >> 4) & 0x0F;
        var fragmentTotal = payload[0] & 0x0F;
        SolixLog.Debug($"Received fragment {fragmentIndex}/{fragmentTotal} for {key}, {payload.Length - 1} bytes");

        // Get existing fragments or create the list if one does not exist.
        if (!_fragmentBuffers.TryGetValue(key, out var fragments))
        {
            fragments = [];
            _fragmentBuffers[key] = fragments;
        }

        fragments.Add(payload[1..]);

        // If out of order then ignore and clear buffers.
        if (fragmentIndex != fragments.Count)
        {
            SolixLog.Debug("Fragment is out of order, ignoring and clearing buffers!");
            fragments.Clear();
            return null;
        }

        // If not all fragments available return.
        if (fragmentTotal != fragments.Count)
        {
            SolixLog.Debug("Waiting for remaining fragments...");
            return null;
        }

        // Assemble fragment payloads in order.
        var complete = fragments.SelectMany(fragment => fragment).ToArray();
        fragments.Clear();
        SolixLog.Debug($"Reassembled payload: {complete.Length} bytes");
        return complete;
    }

    /// <summary>Process telemetry data from the device.</summary>
    private void ProcessTelemetry(Dictionary<string, byte[]> parameters)
    {
        var stateChanged = _data is null || !ParametersEqual(_data, parameters);

        if (SolixLog.Sink is not null)
        {
            SolixLog.Debug($"Telemetry parameters: {ParametersToStr(parameters)}");

            // Print state update if changes.
            if (stateChanged)
            {
                // If we have previous data to compare against log the diff.
                if (_data is not null)
                {
                    SolixLog.Debug("Parameters have changed since previous update!");
                    LogDiff(_data, parameters);
                }

                // Else log the parameters but with the types.
                else
                {
                    SolixLog.Debug($"Telemetry parameters: {ParametersToStr(parameters, types: true)}");
                }
            }
        }

        // Update internal parameters.
        _data = parameters;
        _lastDataTimestamp = DateTimeOffset.Now;

        // Run callbacks if state changed.
        if (stateChanged)
            RunStateChangedCallbacks();
    }

    private static bool ParametersEqual(
        Dictionary<string, byte[]> left, Dictionary<string, byte[]> right)
    {
        if (left.Count != right.Count)
            return false;
        foreach (var (key, value) in left)
        {
            if (!right.TryGetValue(key, out var other) || !value.AsSpan().SequenceEqual(other))
                return false;
        }

        return true;
    }

    private void OnNotificationReceived(IGattLink client, byte[] data)
    {
        // Serialize notification processing while preserving arrival order.
        lock (_notificationGate)
        {
            var previous = _notificationChain;
            _notificationChain = ProcessNotificationSerializedAsync(previous, client, data);
        }
    }

    private async Task ProcessNotificationSerializedAsync(Task previous, IGattLink client, byte[] data)
    {
        await previous.ConfigureAwait(false);
        try
        {
            await ProcessNotificationAsync(client, data).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SolixLog.Error($"Exception processing notification from '{Name}'! {ex}");
        }
    }

    /// <summary>
    /// Process a notification from the device. Frames are routed by their
    /// header: the pattern's channel picks negotiation (01) or session (0f/11)
    /// handling, the cmd's 0x80 flag reassembles fragments and its 0x40 flag
    /// decrypts.
    /// </summary>
    private async Task ProcessNotificationAsync(IGattLink client, byte[] data)
    {
        if (!ReferenceEquals(client, _client))
        {
            SolixLog.Debug("Ignoring notification from old client");
            return;
        }

        SolixLog.Debug(
            $"Received notification from '{Name}'. length: {data.Length}, packet: '{Convert.ToHexStringLower(data)}'");
        _lastPacketTimestamp = DateTimeOffset.UtcNow;

        // Split packet into pattern, command and payload; drop invalid packets.
        byte[] pattern;
        byte[] cmd;
        byte[] payload;
        try
        {
            (pattern, cmd, payload) = SolixFrame.SplitPacket(data);
        }
        catch (FormatException ex)
        {
            SolixLog.Warning($"Dropping invalid packet from '{Name}': {ex.Message}");
            return;
        }

        var composer = pattern[1];
        var channel = pattern[2];
        var command = PacketCommand.Parse(cmd);
        SolixLog.Debug(
            $"Pattern: {Convert.ToHexStringLower(pattern)} (composer {composer:x2}, channel {channel:x2})," +
            $" CMD: {Convert.ToHexStringLower(cmd)} (msgtype {command.MsgType:x3}," +
            $" fragmented {command.Fragmented}, encrypted {command.Encrypted})," +
            $" payload length: {payload.Length}");

        // Fragments are handed to the re-assembler which returns the payload
        // once all fragments are available.
        if (command.Fragmented)
        {
            var reassembled = Reassemble(pattern, cmd, payload);
            if (reassembled is null)
                return;
            payload = reassembled;
        }

        // Negotiation messages (requests, replies and the grant the device
        // pushes after its button is pressed) decrypt themselves.
        if (channel == SolixConst.ChannelNegotiation)
        {
            SolixLog.Debug("Received negotiation message!");
            await ProcessNegotiationAsync(pattern, cmd, payload).ConfigureAwait(false);
            return;
        }

        if (channel is not (SolixConst.ChannelSession or SolixConst.ChannelApp))
        {
            SolixLog.Debug($"Unhandled channel {channel:x2} (composer {composer:x2}), cmd {Convert.ToHexStringLower(cmd)}");
            return;
        }

        // Session messages are decrypted once, if the frame says so.
        if (command.Encrypted)
        {
            var session = _session;
            if (session is not { Keyed: true })
            {
                SolixLog.Debug($"Dropping encrypted session message {Convert.ToHexStringLower(cmd)} received before the session keys");
                return;
            }

            payload = session.Decrypt(payload);
            SolixLog.Debug($"Plain-text payload: {Convert.ToHexStringLower(payload)}");
        }

        // If the packet has a future registered then we just trigger that
        // future instead of processing it here.
        var futureKey = Convert.ToHexStringLower(pattern) + Convert.ToHexStringLower(cmd);
        List<TaskCompletionSource<byte[]>>? futures = null;
        lock (_futuresGate)
        {
            if (_packetFutures.TryGetValue(futureKey, out var registered))
                futures = [.. registered];
        }

        if (futures is not null)
        {
            SolixLog.Debug("Packet has future(s) registered. Triggering future(s) and ignoring packet...");
            foreach (var future in futures)
                future.TrySetResult(payload);
            return;
        }

        ProcessSession(command, cmd, payload);
    }

    /// <summary>Process a plain-text session message from the device.</summary>
    private void ProcessSession(PacketCommand command, byte[] cmd, byte[] payload)
    {
        // Telemetry messages, matched on the message type whatever the
        // fragment and encryption flags (a clear 8405 is a c405).
        _telemetryMsgTypes ??= TelemetryCommands.Select(c => PacketCommand.Parse(c).MsgType).ToHashSet();
        if (command.MsgType == SolixConst.TelemetryMsgType || _telemetryMsgTypes.Contains(command.MsgType))
        {
            SolixLog.Debug(command.Encrypted
                ? "Received encrypted telemetry message!"
                : "Received non-encrypted telemetry message!");
            var parameters = SolixFrame.ParseParameters(payload, out _);

            // A session push the client could decrypt proves it is authorized.
            if (command.Encrypted)
                _session?.OnSessionPush();

            ProcessTelemetry(parameters);
            return;
        }

        // Unknown messages.
        SolixLog.Debug(
            $"Received unknown message of type: {Convert.ToHexStringLower(cmd)}," +
            $" payload: {Convert.ToHexStringLower(payload)}");
    }

    /// <summary>Start a negotiation on the current outer protocol.</summary>
    private Task InitiateNegotiationsAsync(NegotiationOuter outer)
    {
        _session = NewSession(outer);
        return _session.OpenAsync(NegotiationInitWithResponse);
    }

    private NegotiatedSession NewSession(NegotiationOuter outer) => new(outer, WriteNegotiationPacketAsync, Name);

    private Task WriteNegotiationPacketAsync(byte[] packet, bool withResponse)
    {
        var client = _client ?? throw new InvalidOperationException("Not connected to device");
        return client.WriteCommandAsync(packet, withResponse, CancellationToken.None);
    }

    /// <summary>Hand a negotiation frame to the session negotiating with the device.</summary>
    private async Task ProcessNegotiationAsync(byte[] pattern, byte[] cmd, byte[] payload)
    {
        var session = _session;
        if (session is null)
        {
            SolixLog.Warning(
                $"Received negotiation message from '{Name}' before negotiating!" +
                $" cmd: '{Convert.ToHexStringLower(cmd)}', payload: '{Convert.ToHexStringLower(payload)}'");
            return;
        }

        await session.OnReplyAsync(pattern, cmd, payload).ConfigureAwait(false);
    }

    /// <summary>
    /// Send a command to the device. Commands end with the current unix time
    /// (fe 05 03 &lt;timestamp 4B&gt;) to prevent replay attacks.
    /// </summary>
    /// <param name="cmd">2 bytes containing the command type.</param>
    /// <param name="payload">Variable number of bytes containing arguments.</param>
    /// <exception cref="InvalidOperationException">If not connected/negotiated to device.</exception>
    protected async Task SendCommandAsync(byte[] cmd, byte[] payload)
    {
        var session = _session;
        if (!Negotiated || session is null)
            throw new InvalidOperationException("Not connected to device");

        byte[] newPayload = [.. payload, 0xFE, 0x05, 0x03, .. SolixFrame.Timestamp()];
        SolixLog.Debug(
            $"Building packet with cmd: {Convert.ToHexStringLower(cmd)} and payload: {Convert.ToHexStringLower(newPayload)}");

        var packet = SolixFrame.BuildPacket(SolixConst.CommandPattern, cmd, session.Encrypt(newPayload));
        SolixLog.Debug($"Sending encrypted packet: {Convert.ToHexStringLower(packet)}");

        // Send packet.
        var client = _client ?? throw new InvalidOperationException("Not connected to device");
        await client.WriteCommandAsync(packet, withResponse: false, CancellationToken.None).ConfigureAwait(false);
    }

    private void RegisterFuture(TaskCompletionSource<byte[]> future, string key)
    {
        lock (_futuresGate)
        {
            if (!_packetFutures.TryGetValue(key, out var futures))
            {
                futures = [];
                _packetFutures[key] = futures;
            }

            futures.Add(future);
        }
    }

    private void DeregisterFuture(TaskCompletionSource<byte[]> future, string key)
    {
        lock (_futuresGate)
        {
            if (!_packetFutures.TryGetValue(key, out var futures))
                return;
            if (!futures.Remove(future))
                return;
            if (futures.Count == 0)
                _packetFutures.Remove(key);
        }
    }

    /// <summary>
    /// Wait for a session message and return its reassembled, decrypted payload.
    /// This blocks until a matching packet is received or the timeout is
    /// reached (default 10 s), returning null on timeout. Note that this
    /// overrides any built-in parsing of the packet (i.e. if you listen for a
    /// regular telemetry packet that packet will not be used to automatically
    /// populate device attributes).
    /// </summary>
    /// <param name="pattern">3-byte pattern (e.g. 03010f).</param>
    /// <param name="cmd">2-byte command (e.g. c402).</param>
    /// <param name="timeout">Maximum time to wait for a matching response.</param>
    /// <returns>Payload bytes if a response was found, else null.</returns>
    protected async Task<byte[]?> ListenForPacketAsync(byte[] pattern, byte[] cmd, TimeSpan? timeout = null)
    {
        var future = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var key = Convert.ToHexStringLower(pattern) + Convert.ToHexStringLower(cmd);
        RegisterFuture(future, key);
        try
        {
            return await future.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            DeregisterFuture(future, key);
        }
    }

    /// <summary>Execute all registered callbacks for a state change.</summary>
    private void RunStateChangedCallbacks()
    {
        var handlers = StateChanged;
        if (handlers is null)
            return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action)handler)();
            }
            catch (Exception ex)
            {
                SolixLog.Error($"Exception raised by a registered state change callback '{handler.Method}'! {ex}");
            }
        }
    }

    /// <summary>
    /// Background task to automatically reconnect. Started on the first
    /// successful connection; waits for a disconnect event and attempts to
    /// re-connect. Cancelled when <see cref="DisconnectAsync"/> is called.
    /// </summary>
    private async Task AutoReconnectAsync(CancellationToken ct)
    {
        static bool CanRetry(int connectionAttempts) =>
            connectionAttempts < SolixConst.ReconnectAttemptsMax || SolixConst.ReconnectAttemptsMax == -1;

        try
        {
            // If callbacks need to be run on reconnection, we silently reconnect
            // if the timeout has not been exceeded, else we run callbacks to let
            // subscribers know we were disconnected.
            var runCallbacksOnReconnect = false;

            while (CanRetry(_connectionAttempts))
            {
                // If we are already connected and negotiated then wait for disconnection.
                if (Negotiated)
                {
                    SolixLog.Debug(
                        $"Automatic reconnect task ready and waiting for disconnect event from '{Name}'!");
                    await _disconnectEvent.WaitAsync(ct).ConfigureAwait(false);
                    SolixLog.Debug($"Disconnection event signalled by '{Name}', starting reconnection...");
                }
                else
                {
                    SolixLog.Debug($"We are still not connected to '{Name}', starting reconnection...");
                }

                // If we have reached this stage we are not connected.
                try
                {
                    // Limit on the amount of time we can stay disconnected before
                    // we have to trigger callbacks to let subscribers know we are
                    // disconnected.
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(SolixConst.DisconnectTimeout);

                    while (CanRetry(_connectionAttempts))
                    {
                        await Task.Delay(SolixConst.ReconnectDelay, timeoutCts.Token).ConfigureAwait(false);

                        try
                        {
                            var attemptNumber = _connectionAttempts;
                            if (await ConnectAsync(runCallbacks: runCallbacksOnReconnect, ct: timeoutCts.Token)
                                    .ConfigureAwait(false))
                            {
                                SolixLog.Debug(
                                    $"Successfully reconnected to '{Name}'" +
                                    $" {(runCallbacksOnReconnect ? "" : "silently ")}on attempt {attemptNumber}!");

                                // Reset back to false on successful connection.
                                runCallbacksOnReconnect = false;

                                // Break out of this loop back to waiting for a disconnect event.
                                break;
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            SolixLog.Error(
                                $"Exception raised attempting to" +
                                $" {(runCallbacksOnReconnect ? "" : "silently ")}reconnect to '{Name}'! {ex}");
                        }
                    }
                }

                // If the disconnect timeout was exceeded.
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    SolixLog.Warning(
                        $"Timed out attempting to silently reconnect to '{Name}'," +
                        $" callbacks will be triggered due to disconnect!");
                    ResetSession(resetData: true);
                    RunStateChangedCallbacks();

                    // If we ran callbacks due to a disconnect we will need to
                    // run them again on reconnect.
                    runCallbacksOnReconnect = true;
                }
            }

            SolixLog.Warning("Maximum reconnect limit exceeded!");
        }
        catch (OperationCanceledException)
        {
            SolixLog.Debug("Automatic reconnect task has been canceled/stopped");
        }
        catch (Exception ex)
        {
            SolixLog.Error($"Unexpected exception in automatic reconnect task! {ex}");
        }
    }

    /// <summary>
    /// Callback executed by the transport when the connection is lost. Clears
    /// the session state (which is now invalid) but not the cached data; that
    /// is only cleared if the re-connection fails. Also triggers the
    /// disconnection event so the automatic reconnection task attempts to reconnect.
    /// </summary>
    private void OnClientDisconnected(IGattLink client)
    {
        // Ignore disconnect callbacks from old clients.
        if (!ReferenceEquals(client, _client))
        {
            SolixLog.Debug($"Disconnect of '{Name}' came from other client. Ignoring...");
            return;
        }

        SolixLog.Debug($"Connection lost to '{Name}'!");
        _linkLost = true;

        // Reset session-specific state variables but keep the cached data.
        ResetSession(resetData: false);

        // Trigger disconnection event.
        _disconnectEvent.Set();
    }

    /// <summary>Dispose of the current transport client.</summary>
    private async Task DisposeOfClientAsync()
    {
        var client = _client;
        _client = null;
        if (client is null)
            return;

        try
        {
            client.NotificationReceived -= OnNotificationReceived;
            client.Disconnected -= OnClientDisconnected;
            await client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SolixLog.Error($"Exception raised when disposing of client! {ex}");
        }
    }

    /// <summary>
    /// Reset per-connection variables and (optionally) data. The last
    /// negotiation is kept so <see cref="ConnectAsync"/> can still tell after a
    /// drop whether the device answered it; a new connection replaces it.
    /// </summary>
    private void ResetSession(bool resetData = true)
    {
        if (resetData)
        {
            _data = null;
            _lastDataTimestamp = null;
        }

        _fragmentBuffers = new Dictionary<string, List<byte[]>>();
        _lastPacketTimestamp = null;
        lock (_futuresGate)
        {
            _packetFutures = new Dictionary<string, List<TaskCompletionSource<byte[]>>>();
        }
    }

    /// <summary>Async manual-reset event (equivalent of asyncio.Event).</summary>
    private sealed class AsyncEvent
    {
        private readonly object _gate = new();
        private TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Set()
        {
            lock (_gate)
            {
                _tcs.TrySetResult();
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                if (_tcs.Task.IsCompleted)
                    _tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public Task WaitAsync(CancellationToken ct)
        {
            Task task;
            lock (_gate)
            {
                task = _tcs.Task;
            }

            return task.WaitAsync(ct);
        }
    }
}
