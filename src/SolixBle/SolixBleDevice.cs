using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SolixBle;

/// <summary>
/// Solix BLE device object (port of SolixBLE <c>device.py</c>). Handles the
/// packet framing, TLV telemetry parsing, fragment reassembly, the six-stage
/// encryption negotiation, encrypted command sending and automatic reconnection.
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
    private readonly string? _bleName;

    private GattClient? _client;
    private Dictionary<string, Dictionary<int, byte[]>> _fragmentBuffers = new();
    private Dictionary<string, int> _fragmentTotals = new();
    private Dictionary<string, byte[]>? _data;
    private DateTimeOffset? _lastDataTimestamp;
    private DateTimeOffset? _lastPacketTimestamp;
    private DateTimeOffset? _negotiationTimestamp;
    private Dictionary<string, List<TaskCompletionSource<byte[]>>> _packetFutures = new();
    private readonly object _futuresGate = new();
    private readonly object _notificationGate = new();
    private Task _notificationChain = Task.CompletedTask;
    private Task? _autoReconnectTask;
    private CancellationTokenSource? _autoReconnectCts;
    private readonly AsyncEvent _disconnectEvent = new();
    private int _connectionAttempts;

    /// <summary>
    /// Latched when the transport reports a lost link for the current client;
    /// cleared per connect attempt. Needed because MaintainConnection lets the
    /// OS silently re-link (flipping Connected back to true) while our GATT
    /// handles from the dead session remain stale.
    /// </summary>
    private volatile bool _linkLost;
    private byte[]? _sharedSecret;

    /// <summary>Initialise device object. Does not connect automatically.</summary>
    /// <param name="bluetoothAddress">Bluetooth address of the device.</param>
    /// <param name="name">Bluetooth name of the device, if known.</param>
    public SolixBleDevice(ulong bluetoothAddress, string? name = null)
    {
        _bluetoothAddress = bluetoothAddress;
        _bleName = name;
        SolixLog.Debug($"Initializing Solix device '{Name}' with address '{Address}'");
    }

    /// <summary>Initialise device object from a scan result. Does not connect automatically.</summary>
    public SolixBleDevice(DiscoveredDevice device) : this(device.Address, device.Name)
    {
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
    public bool Negotiated => Connected && _sharedSecret is not null;

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
    /// management this enables.
    /// </summary>
    public bool MaintainConnection { get; set; } = true;

    /// <summary>
    /// Command codes (hex) that carry telemetry for this device. Subclasses can
    /// override this if their model uses different telemetry command codes.
    /// </summary>
    protected virtual IReadOnlyList<string> TelemetryCommands => DefaultTelemetryCommands;

    /// <summary>
    /// Some firmwares advance from stage 0 to stage 1, drop the Windows BLE
    /// link before sending the stage-1 notification, then accept the stage-1
    /// response after reconnecting.
    /// </summary>
    protected virtual bool ResumeNegotiationAfterStage0Disconnect => false;

    /// <summary>
    /// Connect to device. This will connect to the device, negotiate an encrypted
    /// session and subscribe to status updates, returning true if successful.
    /// </summary>
    /// <param name="maxAttempts">Maximum number of attempts to try to connect (default 3).</param>
    /// <param name="runCallbacks">Execute registered callbacks on successful connection (default true).</param>
    /// <param name="ct">Cancellation token aborting the connection attempt.</param>
    public async Task<bool> ConnectAsync(int maxAttempts = 3, bool runCallbacks = true, CancellationToken ct = default)
    {
        _connectionAttempts++;

        try
        {
            // If we have an old client get rid of it.
            if (_client is not null)
                await DisposeOfClientAsync().ConfigureAwait(false);

            // Reset negotiated details but keep any data.
            ResetSession(resetData: false);

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

        SolixLog.Debug($"Established initial connection to '{Name}' on attempt {_connectionAttempts}!");

        // Optional settle delay before the first negotiation write.
        if (NegotiationStartDelay > TimeSpan.Zero)
        {
            SolixLog.Debug($"Waiting {NegotiationStartDelay.TotalMilliseconds:0} ms before starting negotiation...");
            await Task.Delay(NegotiationStartDelay, ct).ConfigureAwait(false);
        }

        // Negotiate.
        try
        {
            using var negotiationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            negotiationCts.CancelAfter(SolixConst.NegotiationTimeout);
            var stage0Sent = false;
            var stage1ResumeAttempted = false;

            // While negotiations have not completed.
            while (!Negotiated)
            {
                // The link can die mid-negotiation; bail out instead of
                // spinning against a dead client until the overall timeout.
                // Connected alone is not enough: MaintainConnection makes the
                // OS silently re-link, but our characteristic handles from the
                // dead session stay stale — a fresh ConnectAsync is required.
                if (_linkLost || !Connected)
                {
                    if (ResumeNegotiationAfterStage0Disconnect
                        && stage0Sent
                        && !stage1ResumeAttempted
                        && _lastPacketTimestamp is null)
                    {
                        SolixLog.Warning(
                            $"Connection to '{Name}' was lost after negotiation stage 0; reconnecting to resume at stage 1...");
                        stage1ResumeAttempted = true;

                        await DisposeOfClientAsync().ConfigureAwait(false);
                        ResetSession(resetData: false);
                        await ConnectTransportAsync(maxAttempts: 1, negotiationCts.Token).ConfigureAwait(false);

                        if (!Connected)
                        {
                            SolixLog.Error($"Failed to reconnect to '{Name}' for negotiation resume!");
                            return false;
                        }

                        SolixLog.Debug($"Sending negotiation stage 1 resume request to '{Name}'...");
                        await WriteNegotiationCommandAsync(SolixConst.NegotiationCommand1).ConfigureAwait(false);
                        _lastPacketTimestamp = DateTimeOffset.UtcNow;
                        continue;
                    }

                    SolixLog.Error($"Connection to '{Name}' was lost during negotiation!");
                    return false;
                }

                // If we have not received any packet from the device in any
                // stage then restart negotiations from the start.
                var lastPacket = _lastPacketTimestamp;
                if (lastPacket is null ||
                    DateTimeOffset.UtcNow - lastPacket.Value > SolixConst.NegotiationResponseTimeout)
                {
                    SolixLog.Debug($"Sending negotiation initiation request to '{Name}'...");
                    try
                    {
                        await InitiateNegotiationsAsync(negotiationCts.Token).ConfigureAwait(false);
                        stage0Sent = true;
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
                    if (Negotiated || _linkLost || !Connected)
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            SolixLog.Error($"Timed out attempting to negotiate with '{Name}'!");
            return false;
        }

        // If negotiations succeeded.
        SolixLog.Debug($"Negotiations with '{Name}' succeeded!");
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

    private async Task ConnectTransportAsync(int maxAttempts, CancellationToken ct)
    {
        var attempts = Math.Max(1, maxAttempts);
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            var client = new GattClient(_bluetoothAddress, MaintainConnection);
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

    /// <summary>Validate packet and split into pattern, command and payload bytes.</summary>
    private static (byte[] Pattern, byte[] Cmd, byte[] Payload) SplitPacket(byte[] packet)
    {
        // Header(2) + length(2) + pattern(3) + cmd(2) + checksum(1).
        if (packet.Length < 10)
            throw new FormatException($"Packet is too short ({packet.Length} bytes)!");

        // Validate header is correct.
        if (packet[0] != 0xFF || packet[1] != 0x09)
            throw new FormatException("Packet does not start with FF09!");

        // Validate encoded length is correct (counts the WHOLE packet).
        int encodedLength = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2));
        if (encodedLength != packet.Length)
            throw new FormatException(
                $"Packet length is encoded as {encodedLength} but its length was {packet.Length}!");

        // Validate checksum is correct (XOR of all preceding bytes).
        var actualChecksum = Checksum(packet.AsSpan(0, packet.Length - 1));
        if (packet[^1] != actualChecksum)
            throw new FormatException(
                $"Packet checksum is encoded as {packet[^1]:x2} but it is actually {actualChecksum:x2}!");

        return (packet[4..7], packet[7..9], packet[9..^1]);
    }

    /// <summary>
    /// Parse payload bytes into parameters. Payloads contain a list of parameters
    /// with a format of: &lt;id 1B&gt; &lt;len 1B&gt; &lt;data nB&gt;. A truncated parameter
    /// prevents all further parameters from being parsed and is logged, but the
    /// successfully parsed parameters (if any) are returned.
    /// </summary>
    /// <param name="payload">Payload to parse into parameters.</param>
    /// <returns>Dictionary mapping parameter ids ("a1", "a2", ...) to data.</returns>
    protected IReadOnlyDictionary<string, byte[]> ParsePayload(byte[] payload) => ParsePayloadCore(payload);

    private Dictionary<string, byte[]> ParsePayloadCore(byte[] payload)
    {
        var parsed = new Dictionary<string, byte[]>();
        var pos = 0;

        // Payloads sometimes start with 00 and we must strip that.
        if (payload.Length > 0 && payload[0] == 0x00)
        {
            SolixLog.Debug("Stripped 00 from start of payload");
            pos = 1;
        }

        while (pos < payload.Length)
        {
            // Extract param id (e.g. a1, a2, ...).
            var paramId = payload[pos].ToString("x2");
            pos++;

            // Sometimes there is just a param_id with no length or values.
            if (pos == payload.Length)
            {
                parsed[paramId] = [];
                break;
            }

            // Extract encoded length of parameter.
            int paramLen = payload[pos];
            pos++;

            // Extract data/body from parameter. A truncated parameter stops parsing.
            if (pos + paramLen > payload.Length)
            {
                SolixLog.Error(
                    $"Unexpected end of packet! Data may be missing or invalid!" +
                    $" Error extracting param_data (id={paramId}, len={paramLen})" +
                    $" with only {payload.Length - pos} bytes remaining." +
                    $" Extracted so far: '{ParametersToStr(parsed)}'." +
                    $" Payload: '{Convert.ToHexStringLower(payload)}'");
                break;
            }

            parsed[paramId] = payload[pos..(pos + paramLen)];
            pos += paramLen;
        }

        return parsed;
    }

    private static string ParametersToStr(IReadOnlyDictionary<string, byte[]> parameters, bool types = false)
    {
        if (!types)
            return "{" + string.Join(", ",
                parameters.Select(p => $"'{p.Key}': '{Convert.ToHexStringLower(p.Value)}'")) + "}";

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

    /// <summary>Decrypt a telemetry payload using the negotiated shared secret and IV.</summary>
    protected byte[] DecryptPayload(byte[] payload)
    {
        var secret = _sharedSecret
            ?? throw new InvalidOperationException("No shared secret has been negotiated");
        using var aes = Aes.Create();
        aes.Key = secret[..16];
        return aes.DecryptCbc(payload, secret[16..], PaddingMode.PKCS7);
    }

    /// <summary>Encrypt a payload using the negotiated shared secret and IV.</summary>
    private byte[] EncryptPayload(byte[] payload)
    {
        var secret = _sharedSecret
            ?? throw new InvalidOperationException("No shared secret has been negotiated");
        using var aes = Aes.Create();
        aes.Key = secret[..16];
        return aes.EncryptCbc(payload, secret[16..], PaddingMode.PKCS7);
    }

    /// <summary>
    /// Process an encrypted telemetry packet from the device. Telemetry payloads
    /// may be spread across multiple packets (fragments).
    /// </summary>
    private void ProcessTelemetryPacket(byte[] payload, byte[] cmd)
    {
        // First byte encodes fragment info (high nibble = index, low = total).
        var fragmentIndex = (payload[0] >> 4) & 0x0F;
        var fragmentTotal = payload[0] & 0x0F;

        // Multi-part message.
        if (fragmentTotal > 1)
        {
            var fragmentData = payload[1..];
            var cmdKey = Convert.ToHexStringLower(cmd);
            SolixLog.Debug(
                $"Fragment {fragmentIndex}/{fragmentTotal} for cmd {cmdKey}, {fragmentData.Length} bytes");

            // Store fragment (reset the buffer on first sight or index 1).
            if (!_fragmentBuffers.ContainsKey(cmdKey) || fragmentIndex == 1)
            {
                _fragmentBuffers[cmdKey] = new Dictionary<int, byte[]>();
                _fragmentTotals[cmdKey] = fragmentTotal;
            }

            _fragmentBuffers[cmdKey][fragmentIndex] = fragmentData;

            // Wait until all fragments have arrived.
            if (_fragmentBuffers[cmdKey].Count < fragmentTotal)
            {
                SolixLog.Debug("Waiting for remaining fragments...");
                return;
            }

            // Reassemble in order.
            payload = _fragmentBuffers[cmdKey]
                .OrderBy(fragment => fragment.Key)
                .SelectMany(fragment => fragment.Value)
                .ToArray();
            _fragmentBuffers.Remove(cmdKey);
            _fragmentTotals.Remove(cmdKey);
            SolixLog.Debug($"Reassembled payload: {payload.Length} bytes");
        }
        else
        {
            // Strip fragment info.
            payload = payload[1..];
        }

        var decryptedPayload = DecryptPayload(payload);
        SolixLog.Debug($"Decrypted payload: {Convert.ToHexStringLower(decryptedPayload)}");
        var parameters = ParsePayloadCore(decryptedPayload);
        ProcessTelemetry(parameters);
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

    private void OnNotificationReceived(GattClient client, byte[] data)
    {
        // Serialize notification processing while preserving arrival order.
        lock (_notificationGate)
        {
            var previous = _notificationChain;
            _notificationChain = ProcessNotificationSerializedAsync(previous, client, data);
        }
    }

    private async Task ProcessNotificationSerializedAsync(Task previous, GattClient client, byte[] data)
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

    /// <summary>Process a notification from the device.</summary>
    private async Task ProcessNotificationAsync(GattClient client, byte[] data)
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
            (pattern, cmd, payload) = SplitPacket(data);
        }
        catch (FormatException ex)
        {
            SolixLog.Warning($"Dropping invalid packet from '{Name}': {ex.Message}");
            return;
        }

        SolixLog.Debug($"Pattern: {Convert.ToHexStringLower(pattern)}");
        SolixLog.Debug($"CMD: {Convert.ToHexStringLower(cmd)}");
        SolixLog.Debug($"Payload: {Convert.ToHexStringLower(payload)}");
        SolixLog.Debug($"Payload length: {payload.Length}");

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

        // Match against common message types.
        switch (Convert.ToHexStringLower(pattern))
        {
            // Negotiation messages.
            case "030001":
            {
                SolixLog.Debug("Received negotiation message!");
                await ProcessNegotiationAsync(cmd, payload).ConfigureAwait(false);
                return;
            }

            // Session messages.
            case "03010f":
            case "030111":
            {
                var cmdHex = Convert.ToHexStringLower(cmd);

                // Non-encrypted telemetry messages.
                if (cmdHex == "0300")
                {
                    SolixLog.Debug("Received non-encrypted telemetry message!");
                    var parameters = ParsePayloadCore(payload);
                    ProcessTelemetry(parameters);
                    return;
                }

                // Encrypted telemetry messages.
                if (TelemetryCommands.Contains(cmdHex))
                {
                    SolixLog.Debug("Received encrypted telemetry message!");
                    ProcessTelemetryPacket(payload, cmd);
                    return;
                }

                // Unknown messages.
                SolixLog.Debug($"Received unknown message of type: {cmdHex}");
                try
                {
                    var unknownPayload = payload;

                    // If the payload is one byte too short for AES-CBC then try
                    // putting the last byte of the cmd in front of it.
                    if (unknownPayload.Length % 16 == 15)
                    {
                        SolixLog.Debug("Using special trick of embedded part of CMD in payload...");
                        unknownPayload = new byte[payload.Length + 1];
                        unknownPayload[0] = cmd[1];
                        payload.CopyTo(unknownPayload, 1);
                    }

                    var decryptedPayload = DecryptPayload(unknownPayload);
                    SolixLog.Debug($"Decrypted payload: {Convert.ToHexStringLower(decryptedPayload)}");
                    var parameters = ParsePayloadCore(decryptedPayload);
                    SolixLog.Debug($"Parameters: {ParametersToStr(parameters, types: true)}");
                }
                catch (Exception ex)
                {
                    SolixLog.Error($"Exception decrypting unknown message type: {ex}");
                }

                return;
            }

            default:
            {
                SolixLog.Warning(
                    $"Unexpected packet type '{Convert.ToHexStringLower(pattern)}' sent by device!" +
                    $" Packet: {Convert.ToHexStringLower(data)}");
                return;
            }
        }
    }

    /// <summary>Send the negotiation initiation command.</summary>
    private Task InitiateNegotiationsAsync(CancellationToken ct)
    {
        var client = _client ?? throw new InvalidOperationException("Not connected to device");
        return client.WriteCommandAsync(
            Convert.FromHexString(SolixConst.NegotiationCommand0), NegotiationInitWithResponse, ct);
    }

    private Task WriteNegotiationCommandAsync(string hexCommand)
    {
        var client = _client ?? throw new InvalidOperationException("Not connected to device");
        return client.WriteCommandAsync(Convert.FromHexString(hexCommand), withResponse: false, CancellationToken.None);
    }

    /// <summary>Negotiate encryption with the device, driven by received cmd codes.</summary>
    private async Task ProcessNegotiationAsync(byte[] cmd, byte[] payload)
    {
        // There is a "stage 0" in which we automatically send a negotiation
        // request as soon as we establish the initial connection. That should
        // lead to the power station sending a response landing us in stage 1.
        switch (Convert.ToHexStringLower(cmd))
        {
            // Negotiation stage 1.
            case "0801":
            {
                SolixLog.Debug("Entered negotiation stage 1 due to response from device!");
                var parameters = ParsePayloadCore(payload);
                SolixLog.Debug($"Parameters: {ParametersToStr(parameters)}");
                SolixLog.Debug("Sending stage 1 response message...");
                await WriteNegotiationCommandAsync(SolixConst.NegotiationCommand1).ConfigureAwait(false);
                return;
            }

            // Negotiation stage 2.
            case "0803":
            {
                SolixLog.Debug("Entered negotiation stage 2 due to response from device!");
                var parameters = ParsePayloadCore(payload);
                SolixLog.Debug($"Parameters: {ParametersToStr(parameters)}");
                SolixLog.Debug("Sending stage 2 response message...");
                await WriteNegotiationCommandAsync(SolixConst.NegotiationCommand2).ConfigureAwait(false);
                return;
            }

            // Negotiation stage 3.
            case "0829":
            {
                SolixLog.Debug("Entered negotiation stage 3 due to response from device!");
                var parameters = ParsePayloadCore(payload);
                SolixLog.Debug($"Parameters: {ParametersToStr(parameters)}");
                _negotiationTimestamp = DateTimeOffset.UtcNow;
                SolixLog.Debug("Sending stage 3 response message...");
                await WriteNegotiationCommandAsync(SolixConst.NegotiationCommand3).ConfigureAwait(false);
                return;
            }

            // Negotiation stage 4.
            case "0805":
            {
                SolixLog.Debug("Entered negotiation stage 4 due to response from device!");
                var parameters = ParsePayloadCore(payload);
                SolixLog.Debug($"Parameters: {ParametersToStr(parameters)}");
                SolixLog.Debug("Sending stage 4 response message...");
                await WriteNegotiationCommandAsync(SolixConst.NegotiationCommand4).ConfigureAwait(false);
                return;
            }

            // Negotiation stage 5.
            case "0821":
            {
                SolixLog.Debug("Entered negotiation stage 5 due to response from device!");
                var parameters = ParsePayloadCore(payload);
                SolixLog.Debug($"Parameters: {ParametersToStr(parameters)}");

                // Extract public key of device from payload.
                var deviceKeyBody = parameters["a1"];
                var devicePublicKeyBytes = new byte[1 + deviceKeyBody.Length];
                devicePublicKeyBytes[0] = 0x04;
                deviceKeyBody.CopyTo(devicePublicKeyBytes, 1);
                SolixLog.Debug($"Public key of device: {Convert.ToHexStringLower(devicePublicKeyBytes)}");

                // Calculate the shared secret. The first half of the shared
                // secret is the encryption key and the second half is the IV.
                _sharedSecret = ComputeSharedSecret(devicePublicKeyBytes);
                SolixLog.Debug($"Shared secret: {Convert.ToHexStringLower(_sharedSecret)}");

                SolixLog.Debug("Sending stage 5 response message...");
                await WriteNegotiationCommandAsync(SolixConst.NegotiationCommand5).ConfigureAwait(false);
                return;
            }

            // Negotiation stage 6 (optional). Some devices (e.g. C300X)
            // sometimes send an extra message after stage 5 but others
            // (e.g. C1000) do not. No response is needed but it does not
            // hurt to decrypt it anyway.
            case "4822":
            {
                SolixLog.Debug("Entered negotiation stage 6 (optional) due to response from device!");
                var decryptedPayload = DecryptPayload(payload);
                var parameters = ParsePayloadCore(decryptedPayload);
                SolixLog.Debug($"Parameters: {ParametersToStr(parameters)}");
                return;
            }

            default:
            {
                SolixLog.Warning(
                    $"Received unexpected negotiation request response from device!" +
                    $" cmd: '{Convert.ToHexStringLower(cmd)}', payload: '{Convert.ToHexStringLower(payload)}'");
                return;
            }
        }
    }

    /// <summary>ECDH P-256 key agreement returning the raw 32-byte shared secret (X coordinate).</summary>
    private static byte[] ComputeSharedSecret(byte[] devicePublicKeyUncompressed)
    {
        using var privateKey = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = Convert.FromHexString(SolixConst.PrivateKey),
            Q = new ECPoint
            {
                X = Convert.FromHexString(SolixConst.ClientPublicQx),
                Y = Convert.FromHexString(SolixConst.ClientPublicQy),
            },
        });

        // devicePublicKeyUncompressed = 0x04 || X(32) || Y(32).
        using var devicePublic = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = devicePublicKeyUncompressed[1..33],
                Y = devicePublicKeyUncompressed[33..65],
            },
        });
        using var devicePublicKey = devicePublic.PublicKey;

        return privateKey.DeriveRawSecretAgreement(devicePublicKey);
    }

    /// <summary>Calculate the checksum byte for a packet (XOR of all bytes).</summary>
    private static byte Checksum(ReadOnlySpan<byte> packet)
    {
        byte checksumValue = 0;
        foreach (var b in packet)
            checksumValue ^= b;
        return checksumValue;
    }

    /// <summary>
    /// Send a command to the device. Commands include a timestamp in the payload
    /// to prevent replay attacks; that timestamp is agreed during negotiations.
    /// </summary>
    /// <param name="cmd">2 bytes containing the command type.</param>
    /// <param name="payload">Variable number of bytes containing arguments.</param>
    /// <exception cref="InvalidOperationException">If not connected/negotiated to device.</exception>
    protected async Task SendCommandAsync(byte[] cmd, byte[] payload)
    {
        if (!Negotiated)
            throw new InvalidOperationException("Not connected to device");

        var negotiationTimestamp = _negotiationTimestamp
            ?? throw new InvalidOperationException("Negotiation timestamp has not been set");

        // Commands include a timestamp in the payload to prevent replay attacks
        // and that timestamp is set during negotiations.
        var timePassed = (uint)(DateTimeOffset.UtcNow - negotiationTimestamp).TotalSeconds;
        var baseTimestamp = BinaryPrimitives.ReadUInt32LittleEndian(Convert.FromHexString(SolixConst.BaseTimestamp));

        var newPayload = new byte[payload.Length + 7];
        payload.CopyTo(newPayload, 0);
        newPayload[payload.Length] = 0xFE;
        newPayload[payload.Length + 1] = 0x05;
        newPayload[payload.Length + 2] = 0x03;
        BinaryPrimitives.WriteUInt32LittleEndian(
            newPayload.AsSpan(payload.Length + 3), unchecked(baseTimestamp + timePassed));

        await SendEncryptedPacketAsync(cmd, newPayload).ConfigureAwait(false);
    }

    /// <summary>
    /// Build a packet to be sent to a device.
    /// Packet format: &lt;HEADER 2B&gt; &lt;LENGTH 2B&gt; &lt;PATTERN 3B&gt; &lt;CMD 2B&gt; &lt;PAYLOAD nB&gt; &lt;CHECKSUM 1B&gt;.
    /// </summary>
    private static byte[] BuildPacket(byte[] pattern, byte[] cmd, byte[] payload)
    {
        // Calculate length of message (counts the whole packet).
        var length = 2 + 2 + 3 + 2 + payload.Length + 1;

        // Build packet.
        var packet = new byte[length];
        packet[0] = 0xFF;
        packet[1] = 0x09;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), (ushort)length);
        pattern.CopyTo(packet, 4);
        cmd.CopyTo(packet, 7);
        payload.CopyTo(packet, 9);
        packet[^1] = Checksum(packet.AsSpan(0, length - 1));
        return packet;
    }

    /// <summary>Send an encrypted packet using the negotiated shared secret and IV.</summary>
    private async Task SendEncryptedPacketAsync(byte[] cmd, byte[] payload)
    {
        SolixLog.Debug(
            $"Building packet with cmd: {Convert.ToHexStringLower(cmd)} and payload: {Convert.ToHexStringLower(payload)}");
        var encryptedPayload = EncryptPayload(payload);

        var packet = BuildPacket(Convert.FromHexString("03000f"), cmd, encryptedPayload);
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
    /// Wait for a response and return its payload bytes. This blocks until a
    /// matching packet is received or the timeout is reached (default 10 s),
    /// returning null on timeout. Note that this overrides any built-in parsing
    /// of the packet (i.e. if you listen for a regular telemetry packet that
    /// packet will not be used to automatically populate device attributes).
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
    /// the negotiated values (which are now invalid) but not the cached data;
    /// that is only cleared if the re-connection fails. Also triggers the
    /// disconnection event so the automatic reconnection task attempts to reconnect.
    /// </summary>
    private void OnClientDisconnected(GattClient client)
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

    /// <summary>Reset negotiated variables and (optionally) data and futures.</summary>
    private void ResetSession(bool resetData = true)
    {
        if (resetData)
        {
            _data = null;
            _lastDataTimestamp = null;
        }

        _fragmentBuffers = new Dictionary<string, Dictionary<int, byte[]>>();
        _fragmentTotals = new Dictionary<string, int>();
        _sharedSecret = null;
        _lastPacketTimestamp = null;
        _negotiationTimestamp = null;
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
