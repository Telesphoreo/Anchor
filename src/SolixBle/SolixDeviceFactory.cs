using SolixBle.Devices;

namespace SolixBle;

/// <summary>Supported Solix power station models.</summary>
public enum SolixDeviceModel
{
    /// <summary>Generic Solix device (telemetry parameters only, no model-specific mapping).</summary>
    Generic,

    /// <summary>C1000(X) / A1761.</summary>
    C1000,

    /// <summary>C1000(X) Gen 2 / A1763.</summary>
    C1000G2,

    /// <summary>C300.</summary>
    C300,

    /// <summary>C800.</summary>
    C800,

    /// <summary>F2000 / PowerHouse 767.</summary>
    F2000,

    /// <summary>F2600 / A1781.</summary>
    F2600,

    /// <summary>F3800.</summary>
    F3800,
}

/// <summary>
/// Creates the model-specific device class for a Bluetooth address, and
/// identifies the model from a scan result (port of SolixBLE <c>factory.py</c>).
/// </summary>
public static class SolixDeviceFactory
{
    /// <summary>Model by the advertised product type (hex).</summary>
    private static readonly Dictionary<string, SolixDeviceModel> ModelsByProductType = new()
    {
        ["b118"] = SolixDeviceModel.C1000G2, // A1763
        ["b119"] = SolixDeviceModel.C1000G2, // A1765, the same display-board build as the A1763
        ["b106"] = SolixDeviceModel.F3800, // A1790
        ["b112"] = SolixDeviceModel.F3800, // A1790P, the same command and telemetry map as the A1790
        ["b103"] = SolixDeviceModel.C800, // A1753 / A1754 / A1755 share one map
    };

    /// <summary>Model by the advertised local name.</summary>
    private static readonly Dictionary<string, SolixDeviceModel> ModelsByName = new()
    {
        ["SOLIX C1000 Gen 2"] = SolixDeviceModel.C1000G2,
        ["SOLIX C1000 Plus"] = SolixDeviceModel.C1000G2,
        ["SOLIX C1000X Gen 2"] = SolixDeviceModel.C1000G2,
        ["Anker SOLIX C1000"] = SolixDeviceModel.C1000,
        ["Anker SOLIX F3800"] = SolixDeviceModel.F3800,
        ["Anker SOLIX F3800 Plus"] = SolixDeviceModel.F3800,
    };

    /// <summary>Create a device instance of the requested model.</summary>
    public static SolixBleDevice Create(SolixDeviceModel model, ulong address, string? name = null) => model switch
    {
        SolixDeviceModel.Generic => new Generic(address, name),
        SolixDeviceModel.C1000 => new C1000(address, name),
        SolixDeviceModel.C1000G2 => new C1000G2(address, name),
        SolixDeviceModel.C300 => new C300(address, name),
        SolixDeviceModel.C800 => new C800(address, name),
        SolixDeviceModel.F2000 => new F2000(address, name),
        SolixDeviceModel.F2600 => new F2600(address, name),
        SolixDeviceModel.F3800 => new F3800(address, name),
        _ => throw new ArgumentOutOfRangeException(nameof(model), model, "Unsupported Solix device model"),
    };

    /// <summary>
    /// Create a device instance of the requested model from a scan result,
    /// carrying its advertised capability (which negotiation to open with).
    /// </summary>
    public static SolixBleDevice Create(SolixDeviceModel model, DiscoveredDevice device) => model switch
    {
        SolixDeviceModel.Generic => new Generic(device),
        SolixDeviceModel.C1000 => new C1000(device),
        SolixDeviceModel.C1000G2 => new C1000G2(device),
        SolixDeviceModel.C300 => new C300(device),
        SolixDeviceModel.C800 => new C800(device),
        SolixDeviceModel.F2000 => new F2000(device),
        SolixDeviceModel.F2600 => new F2600(device),
        SolixDeviceModel.F3800 => new F3800(device),
        _ => throw new ArgumentOutOfRangeException(nameof(model), model, "Unsupported Solix device model"),
    };

    /// <summary>
    /// The model a scan result identifies: by the advertised product type, else
    /// by the advertised local name. Some firmware advertises the serial as the
    /// name, so the product type is the key to trust.
    /// </summary>
    /// <returns>The model, or null if neither is known.</returns>
    public static SolixDeviceModel? SuggestModel(string? productType, string? name)
    {
        if (productType is not null && ModelsByProductType.TryGetValue(productType, out var byProduct))
            return byProduct;
        if (!string.IsNullOrEmpty(name) && ModelsByName.TryGetValue(name, out var byName))
            return byName;
        return null;
    }
}
