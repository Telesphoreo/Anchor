using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SolixBle;

/// <summary>
/// How the negotiation travels and which cipher the session uses (port of
/// SolixBLE <c>protocols/outer.py</c>).
/// </summary>
internal enum NegotiationOuter
{
    /// <summary>0xxx negotiation in clear; AES-CBC once the ECDH keys exist. Authorizes at x821.</summary>
    Plain,

    /// <summary>
    /// 4xxx negotiation under a static AES-GCM key; AES-GCM under the ECDH keys
    /// once they exist. Authorizes at x827 status 00, the grant pushed on
    /// pattern 030101, or the first session push the client can decrypt.
    /// </summary>
    Encrypted,
}

/// <summary>
/// One negotiation on one connection: the shared opening, the capability
/// exchange and the ECDH key establishment, on either outer protocol (port of
/// SolixBLE <c>protocols/session.py</c> and <c>protocols/ecdh.py</c>).
/// </summary>
/// <remarks>
/// Request flow (plain outer; the encrypted outer flags every cmd with 0x40
/// and omits the client id a2):
/// 0001 (a1 time, a2 id) → 0801; 0003 (a1, a2, a3 encrypt, a4 MTU) → 0803;
/// 0029 (a1, a2) → 0829; 0005 (a1, a2, a3, a4, a5 method[, a6 auth]) → 0805;
/// 0021 (a1 public key) → 0821 (device key; keys installed); 4022 (a1, a2,
/// a3 UTC offset, a5 POSIX TZ) → 4822; on the encrypted outer only,
/// 4027 (a1, a2 client token) → 4827 (registration status).
/// </remarks>
internal sealed class NegotiatedSession
{
    /// <summary>The client's a3 in x003 and x005; the device logs it and ignores it.</summary>
    private const byte ClientEncrypt = 0x20;

    /// <summary>The client's MTU ceiling in x003 and the plain 0005 (61 440 = no limit).</summary>
    private const ushort ClientMtu = 0xF000;

    /// <summary>
    /// The method the client tells the device it will use (x005 a5), as the app
    /// sends it per outer. The firmware tests a5 &amp; 0x44, so both select ECDH.
    /// </summary>
    private const byte EcdhMethodPlain = 0x40;

    /// <inheritdoc cref="EcdhMethodPlain"/>
    private const byte EcdhMethodEncrypted = 0x44;

    /// <summary>Status byte of an accepted reply.</summary>
    private const byte StatusOk = 0x00;

    /// <summary>x827 status: the device waits for its button to confirm this client.</summary>
    private const byte StatusConfirm = 0x09;

    private static readonly byte[] StaticKey = Convert.FromHexString(SolixConst.NegotiationKey);
    private static readonly byte[] StaticNonce = Convert.FromHexString(SolixConst.NegotiationNonce);
    private static readonly byte[] Aad = Convert.FromHexString(SolixConst.NegotiationAad);
    private static readonly byte[] ClientIdBytes = Encoding.ASCII.GetBytes(SolixConst.ClientId);

    private readonly Func<byte[], bool, Task> _write;
    private readonly string _deviceName;
    private ECDiffieHellman? _key;
    private byte[]? _sessionKey;
    private byte[]? _sessionIv;
    private bool _capabilityExchanged;
    private int? _mtu;
    private byte? _authMethod;
    private bool _pathAuthorized;
    private bool _pushed;

    /// <param name="outer">The outer protocol to negotiate on.</param>
    /// <param name="write">Writes one packet to the device (packet, write-with-response).</param>
    /// <param name="deviceName">Device name for log lines.</param>
    public NegotiatedSession(NegotiationOuter outer, Func<byte[], bool, Task> write, string deviceName)
    {
        Outer = outer;
        _write = write;
        _deviceName = deviceName;
    }

    /// <summary>The outer protocol this session runs on.</summary>
    public NegotiationOuter Outer { get; }

    /// <summary>Whether the device answered any negotiation request.</summary>
    public bool Replied { get; private set; }

    /// <summary>Whether the device accepts session commands.</summary>
    public bool Authorized => _pushed || _pathAuthorized;

    /// <summary>Whether the ECDH keys have been installed.</summary>
    public bool Keyed => _sessionKey is not null;

    /// <summary>
    /// When the device's confirmation window (x827 status 09) closes, or null
    /// if the device is not waiting for its button.
    /// </summary>
    public DateTimeOffset? ConfirmationDeadline { get; private set; }

    /// <summary>Send the outer's opening x001.</summary>
    public Task OpenAsync(bool withResponse) =>
        SendAsync(0x001, [(0xA1, SolixFrame.Timestamp())], clientId: true, withResponse);

    /// <summary>Send x003, the request that answers x801.</summary>
    public Task SendCapabilityRequestAsync() =>
        SendAsync(
            0x003,
            [(0xA1, SolixFrame.Timestamp()), (0xA3, [ClientEncrypt]), (0xA4, U16(ClientMtu))],
            clientId: true);

    /// <summary>Record a session push, which proves authorization.</summary>
    public void OnSessionPush() => _pushed = true;

    /// <summary>
    /// Handle one negotiation frame, decrypting it if its 0x40 flag is set:
    /// clear 08xx replies on the plain outer, static-GCM 48xx before keys, CBC
    /// or GCM once keys are installed.
    /// </summary>
    public async Task OnReplyAsync(byte[] pattern, byte[] cmd, byte[] payload)
    {
        var command = PacketCommand.Parse(cmd);
        var plaintext = command.Encrypted ? Decrypt(payload) : payload;
        Replied = true;

        var parameters = SolixFrame.ParseParameters(plaintext, out var status);
        SolixLog.Debug(
            $"Negotiation reply {command.MsgType:x3} (pattern {Convert.ToHexStringLower(pattern)}," +
            $" status {(status is { } s ? s.ToString("x2") : "none")}) on the {OuterName(Outer)} outer:" +
            $" {SolixFrame.FormatParameters(parameters)}");

        switch (command.MsgType)
        {
            case 0x801:
                await SendCapabilityRequestAsync().ConfigureAwait(false);
                return;

            case 0x803:
                RecordCapability(parameters);
                _capabilityExchanged = true;
                await SendAsync(0x029, [(0xA1, SolixFrame.Timestamp())], clientId: true).ConfigureAwait(false);
                return;

            case var _ when _capabilityExchanged:
                await OnEcdhStageAsync(command.MsgType, status, parameters).ConfigureAwait(false);
                return;

            default:
                SolixLog.Warning(
                    $"Negotiation reply {command.MsgType:x3} from '{_deviceName}' before the capability exchange");
                return;
        }
    }

    /// <summary>Run x829 to x827: method, key exchange, clock and registration.</summary>
    private async Task OnEcdhStageAsync(int msgType, byte? status, Dictionary<string, byte[]> parameters)
    {
        switch (msgType)
        {
            case 0x829:
            {
                if (parameters.TryGetValue("a4", out var serial))
                    SolixLog.Debug($"Device serial: {Encoding.ASCII.GetString(serial)}");
                if (parameters.TryGetValue("a5", out var mac))
                    SolixLog.Debug($"Device MAC: {Convert.ToHexStringLower(mac)}");

                // ECDH sets the device's mode 4 and opens x021. The outer
                // supplies a4 and a6 the way the app sends them on it.
                List<(byte, byte[])> tags =
                [
                    (0xA1, SolixFrame.Timestamp()),
                    (0xA3, [ClientEncrypt]),
                    (0xA4, U16(Outer == NegotiationOuter.Encrypted && _mtu is > 0 ? _mtu.Value : ClientMtu)),
                    (0xA5, [Outer == NegotiationOuter.Encrypted ? EcdhMethodEncrypted : EcdhMethodPlain]),
                ];
                if (Outer == NegotiationOuter.Encrypted && _authMethod is { } authMethod)
                    tags.Add((0xA6, [authMethod]));
                await SendAsync(0x005, tags, clientId: true).ConfigureAwait(false);
                return;
            }

            case 0x805:
            {
                _key?.Dispose();
                _key = SolixCrypto.GenerateEcdhKey();
                await SendAsync(0x021, [(0xA1, SolixCrypto.EcdhPublicBytes(_key))], clientId: false)
                    .ConfigureAwait(false);
                return;
            }

            case 0x821:
            {
                var key = _key ?? throw new InvalidOperationException("x821 before the client key was generated");
                var secret = SolixCrypto.EcdhSharedSecret(key, parameters["a1"]);
                key.Dispose();
                _key = null;

                // The first half of the shared secret is the key and the second
                // half the IV (CBC), whose first 12 bytes are the nonce (GCM).
                _sessionKey = secret[..16];
                _sessionIv = secret[16..32];
                SolixLog.Debug($"Installed session keys on the {OuterName(Outer)} outer");

                // The outer adds a2 (the app sends it on the plain outer only).
                try
                {
                    await SendAsync(
                        0x022,
                        [
                            (0xA1, SolixFrame.Timestamp()),
                            (0xA3, PosixTimeZone.OffsetSecondsWest()),
                            (0xA5, Encoding.ASCII.GetBytes(PosixTimeZone.Current())),
                        ],
                        clientId: true).ConfigureAwait(false);
                }
                finally
                {
                    // The plain outer authorizes at the key exchange; set after
                    // the x022 write so no session command overtakes it.
                    if (Outer == NegotiationOuter.Plain)
                        _pathAuthorized = true;
                }

                return;
            }

            case 0x822:
            {
                if (!Authorized)
                {
                    await SendAsync(
                        0x027,
                        [(0xA1, SolixFrame.Timestamp()), (0xA2, ClientIdBytes)],
                        clientId: false).ConfigureAwait(false);
                }

                return;
            }

            case 0x827:
                Register(status, parameters);
                return;

            default:
                SolixLog.Warning(
                    $"Received unexpected negotiation reply {msgType:x3} from '{_deviceName}'!" +
                    $" Parameters: {SolixFrame.FormatParameters(parameters)}");
                return;
        }
    }

    /// <summary>Record the capability bits, MTU and auth method of x803.</summary>
    private void RecordCapability(Dictionary<string, byte[]> parameters)
    {
        _mtu = parameters.TryGetValue("a2", out var mtu) && mtu.Length >= 2
            ? BinaryPrimitives.ReadUInt16LittleEndian(mtu)
            : null;
        _authMethod = parameters.TryGetValue("a5", out var auth) && auth.Length >= 1 ? auth[0] : null;
        var baseMethod = parameters.TryGetValue("a1", out var b) && b.Length >= 1 ? $"{b[0]:x2}" : "none";
        var encryptMethod = parameters.TryGetValue("a3", out var e) && e.Length >= 1 ? $"{e[0]:x2}" : "none";
        SolixLog.Debug(
            $"'{_deviceName}' announced base_method={baseMethod}, encrypt_method={encryptMethod}," +
            $" auth_method={(_authMethod is { } a ? a.ToString("x2") : "none")}, mtu={_mtu?.ToString() ?? "none"}");
    }

    /// <summary>
    /// Record an x827; 00 authorizes, 09 opens a confirmation window. The reply
    /// to 4027 and the grant pushed on 030101 after a button press are the same
    /// message; either authorizes.
    /// </summary>
    private void Register(byte? status, Dictionary<string, byte[]> parameters)
    {
        if (status == StatusOk)
        {
            ConfirmationDeadline = null;
            _pathAuthorized = true;
            SolixLog.Debug($"'{_deviceName}' registered this client");
            return;
        }

        if (status == StatusConfirm)
        {
            var window = parameters.TryGetValue("a1", out var a1) && a1.Length >= 2
                ? BinaryPrimitives.ReadUInt16LittleEndian(a1)
                : (int?)null;
            if (window is { } seconds)
                ConfirmationDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(seconds);
            SolixLog.Warning(
                $"'{_deviceName}' is waiting to confirm this computer: press the Bluetooth/IoT button on the" +
                $" power station{(window is { } w ? $" within {w} seconds" : "")} to allow the connection.");
            return;
        }

        SolixLog.Info(
            $"'{_deviceName}' client registration status {(status is { } s ? s.ToString("x2") : "none")}; not authorized");
    }

    /// <summary>Send a negotiation request, flagged and wrapped for the current state.</summary>
    /// <param name="msgType">The 12-bit message type.</param>
    /// <param name="parameters">Parameters in order; a1 first.</param>
    /// <param name="clientId">Insert the client id as a2 if the outer sends it.</param>
    /// <param name="withResponse">Write with response (the opening request only).</param>
    private async Task SendAsync(
        int msgType, List<(byte Tag, byte[] Value)> parameters, bool clientId, bool withResponse = false)
    {
        if (clientId && Outer == NegotiationOuter.Plain)
            parameters.Insert(1, (0xA2, ClientIdBytes));

        var encrypted = Outer == NegotiationOuter.Encrypted || Keyed;
        var cmd = new PacketCommand(Fragmented: false, encrypted, msgType).ToBytes();
        var payload = SolixFrame.BuildParameters(parameters);
        SolixLog.Debug(
            $"Sending negotiation {Convert.ToHexStringLower(cmd)} on the {OuterName(Outer)} outer," +
            $" payload: {Convert.ToHexStringLower(payload)}");

        var packet = SolixFrame.BuildPacket(SolixConst.NegotiationPattern, cmd, Encrypt(payload));
        SolixLog.Debug($"Sending negotiation packet: {Convert.ToHexStringLower(packet)}");
        await _write(packet, withResponse).ConfigureAwait(false);
    }

    /// <summary>Seal a payload with the outer, under the session keys if installed.</summary>
    public byte[] Encrypt(byte[] payload) => Outer switch
    {
        NegotiationOuter.Plain => _sessionKey is null
            ? payload
            : SolixCrypto.CbcEncrypt(_sessionKey, _sessionIv!, payload),
        _ => _sessionKey is null
            ? SolixCrypto.GcmEncrypt(StaticKey, StaticNonce, Aad, payload)
            : SolixCrypto.GcmEncrypt(_sessionKey, _sessionIv![..12], Aad, payload),
    };

    /// <summary>Open a payload with the outer, under the session keys if installed.</summary>
    public byte[] Decrypt(byte[] payload) => Outer switch
    {
        NegotiationOuter.Plain => _sessionKey is null
            ? payload
            : SolixCrypto.CbcDecrypt(_sessionKey, _sessionIv!, payload),
        _ => _sessionKey is null
            ? SolixCrypto.GcmDecrypt(StaticKey, StaticNonce, Aad, payload)
            : SolixCrypto.GcmDecrypt(_sessionKey, _sessionIv![..12], Aad, payload),
    };

    /// <summary>Lower-case name of an outer for log lines.</summary>
    public static string OuterName(NegotiationOuter outer) => outer == NegotiationOuter.Plain ? "plain" : "encrypted";

    private static byte[] U16(int value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)value);
        return bytes;
    }
}
