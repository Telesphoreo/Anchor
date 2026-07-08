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

    /// <summary>F3800.</summary>
    F3800,
}

/// <summary>Creates the model-specific device class for a Bluetooth address.</summary>
public static class SolixDeviceFactory
{
    /// <summary>Create a device instance of the requested model.</summary>
    public static SolixBleDevice Create(SolixDeviceModel model, ulong address, string? name = null) => model switch
    {
        SolixDeviceModel.Generic => new Generic(address, name),
        SolixDeviceModel.C1000 => new C1000(address, name),
        SolixDeviceModel.C1000G2 => new C1000G2(address, name),
        SolixDeviceModel.C300 => new C300(address, name),
        SolixDeviceModel.C800 => new C800(address, name),
        SolixDeviceModel.F2000 => new F2000(address, name),
        SolixDeviceModel.F3800 => new F3800(address, name),
        _ => throw new ArgumentOutOfRangeException(nameof(model), model, "Unsupported Solix device model"),
    };
}
