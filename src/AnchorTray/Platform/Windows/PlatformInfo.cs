namespace Anchor;

/// <summary>Windows-specific text and release assets.</summary>
internal static class PlatformInfo
{
    /// <summary>Release asset the updater downloads and runs.</summary>
    public static string? InstallerAssetSuffix => "-setup.exe";

    public const string BluetoothOffMessage = "Bluetooth is turned off. Enable it in Windows settings.";
}
