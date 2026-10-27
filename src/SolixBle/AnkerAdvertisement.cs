using System.Text;

namespace SolixBle;

/// <summary>
/// Anker manufacturer advertisement record (port of SolixBLE <c>advertisement.py</c>).
/// Anker devices publish a fixed-shape record under BLE company identifier
/// 0xffff: version(1) | mac(6) | bind_type(1) | product_type(2) |
/// sku(3 if version 1, 4 if version 2) | capability(0-1). All of it is
/// readable before connecting.
/// </summary>
/// <param name="Version">Record version (1 or 2).</param>
/// <param name="Mac">Device MAC as hex.</param>
/// <param name="BindType">Bind type byte.</param>
/// <param name="ProductType">Model key as hex (e.g. "b118").</param>
/// <param name="Sku">SKU text.</param>
/// <param name="Capability">Capability byte (newer firmware only); bit 0x04 = encrypted negotiation.</param>
internal sealed record AnkerAdvertisement(
    byte Version, string Mac, byte BindType, string ProductType, string Sku, byte? Capability)
{
    /// <summary>Decode an Anker 0xffff manufacturer record (without the company id), or null if malformed.</summary>
    public static AnkerAdvertisement? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 1)
            return null;
        var version = data[0];
        int skuLength = version switch
        {
            1 => 3,
            2 => 4,
            _ => 0,
        };
        const int skuOffset = 1 + 6 + 1 + 2;
        if (skuLength == 0 || data.Length < skuOffset + skuLength)
            return null;

        var capabilityOffset = skuOffset + skuLength;
        return new AnkerAdvertisement(
            version,
            Convert.ToHexStringLower(data[1..7]),
            data[7],
            Convert.ToHexStringLower(data[8..10]),
            Encoding.ASCII.GetString(data.Slice(skuOffset, skuLength)).TrimEnd('\0'),
            data.Length > capabilityOffset ? data[capabilityOffset] : null);
    }
}
