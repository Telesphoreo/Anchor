using System.Buffers.Binary;

namespace SolixBle;

/// <summary>
/// Command field of a packet (port of SolixBLE <c>constructs.PacketCommand</c>):
/// link flags in the high nibble and a 12-bit message type. 0x80 marks a
/// fragment (the payload's first byte is index &lt;&lt; 4 | total) and 0x40 an
/// encrypted payload; a response is the request's message type with 0x800 set.
/// </summary>
internal readonly record struct PacketCommand(bool Fragmented, bool Encrypted, int MsgType)
{
    public static PacketCommand Parse(ReadOnlySpan<byte> cmd) =>
        new((cmd[0] & 0x80) != 0, (cmd[0] & 0x40) != 0, ((cmd[0] & 0x0F) << 8) | cmd[1]);

    public static PacketCommand Parse(string cmdHex) => Parse(Convert.FromHexString(cmdHex));

    public byte[] ToBytes() =>
    [
        (byte)((Fragmented ? 0x80 : 0) | (Encrypted ? 0x40 : 0) | ((MsgType >> 8) & 0x0F)),
        (byte)(MsgType & 0xFF),
    ];
}

/// <summary>
/// Packet framing shared by the device and the negotiation (port of SolixBLE
/// <c>constructs.py</c>).
/// Packet format: &lt;HEADER 2B&gt; &lt;LENGTH 2B&gt; &lt;PATTERN 3B&gt; &lt;CMD 2B&gt; &lt;PAYLOAD nB&gt; &lt;CHECKSUM 1B&gt;.
/// The pattern is 03 &lt;composer&gt; &lt;channel&gt;: the channel is the dispatch key
/// and the composer is 01 on frames the device sends on its own, 00 on replies.
/// </summary>
internal static class SolixFrame
{
    /// <summary>Calculate the checksum byte for a packet (XOR of all bytes).</summary>
    public static byte Checksum(ReadOnlySpan<byte> packet)
    {
        byte checksumValue = 0;
        foreach (var b in packet)
            checksumValue ^= b;
        return checksumValue;
    }

    /// <summary>Build a packet to be sent to a device.</summary>
    public static byte[] BuildPacket(byte[] pattern, byte[] cmd, byte[] payload)
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

    /// <summary>Validate packet and split into pattern, command and payload bytes.</summary>
    public static (byte[] Pattern, byte[] Cmd, byte[] Payload) SplitPacket(byte[] packet)
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
    /// Parse payload bytes into parameters. Payloads contain an optional status
    /// byte (replies only; any first byte below 0xa1) followed by a list of
    /// parameters with a format of: &lt;id 1B&gt; &lt;len 1B&gt; &lt;data nB&gt;. A truncated
    /// parameter prevents all further parameters from being parsed and is
    /// logged, but the successfully parsed parameters (if any) are returned.
    /// </summary>
    /// <param name="payload">Payload to parse into parameters.</param>
    /// <param name="status">The reply's status byte, or null for a push.</param>
    /// <returns>Dictionary mapping parameter ids ("a1", "a2", ...) to data.</returns>
    public static Dictionary<string, byte[]> ParseParameters(byte[] payload, out byte? status)
    {
        var parsed = new Dictionary<string, byte[]>();
        var pos = 0;
        status = null;

        // Replies start with a status byte and we must strip that.
        if (payload.Length > 0 && payload[0] < SolixConst.StatusLimit)
        {
            status = payload[0];
            SolixLog.Debug($"Stripped status byte {payload[0]:x2} from start of payload");
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
                    $" Extracted so far: '{FormatParameters(parsed)}'." +
                    $" Payload: '{Convert.ToHexStringLower(payload)}'");
                break;
            }

            parsed[paramId] = payload[pos..(pos + paramLen)];
            pos += paramLen;
        }

        return parsed;
    }

    /// <summary>Build a payload from parameters as &lt;id 1B&gt; &lt;len 1B&gt; &lt;data nB&gt;, without a type byte.</summary>
    public static byte[] BuildParameters(IEnumerable<(byte Tag, byte[] Value)> parameters)
    {
        var payload = new List<byte>();
        foreach (var (tag, value) in parameters)
        {
            payload.Add(tag);
            payload.Add(checked((byte)value.Length));
            payload.AddRange(value);
        }

        return [.. payload];
    }

    /// <summary>Format parameters as {'a1': 'hex', ...} for logging.</summary>
    public static string FormatParameters(IReadOnlyDictionary<string, byte[]> parameters) =>
        "{" + string.Join(", ", parameters.Select(p => $"'{p.Key}': '{Convert.ToHexStringLower(p.Value)}'")) + "}";

    /// <summary>The current unix time as the 4-byte little-endian timestamp value.</summary>
    public static byte[] Timestamp()
    {
        var timestamp = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(timestamp, (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        return timestamp;
    }
}
