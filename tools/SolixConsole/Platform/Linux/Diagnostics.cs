namespace SolixConsole;

/// <summary>
/// The GATT dump, pairing and raw probes drive WinRT directly and exist only on
/// Windows. On Linux, bluetoothctl covers the GATT dump.
/// </summary>
internal static class Diagnostics
{
    public static Task DumpGattAsync(ulong address)
    {
        Console.WriteLine("--gatt is Windows-only. On Linux, use bluetoothctl: connect <MAC>, then menu gatt, list-attributes.");
        return Task.CompletedTask;
    }

    public static Task<bool> TryPairAsync(ulong address)
    {
        Console.WriteLine("--pair is Windows-only. On Linux the Solix protocol needs no BlueZ pairing.");
        return Task.FromResult(false);
    }

    public static Task UnpairAsync(ulong address)
    {
        Console.WriteLine("--unpair is Windows-only. On Linux, use bluetoothctl remove <MAC>.");
        return Task.CompletedTask;
    }

    public static Task RunRawProbeAsync(ulong address, string probe, string? rawHex, bool noMaintain, bool initNoResp)
    {
        Console.WriteLine("--probe is Windows-only.");
        return Task.CompletedTask;
    }
}
