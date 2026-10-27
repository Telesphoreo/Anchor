namespace Anchor;

/// <summary>Linux-specific text and release assets.</summary>
internal static class PlatformInfo
{
    /// <summary>
    /// None: the package manager owns the installed files, so an update opens
    /// the release page instead of downloading anything.
    /// </summary>
    public static string? InstallerAssetSuffix => null;

    public const string BluetoothOffMessage =
        "Bluetooth is turned off. Turn it on in the system settings or with 'bluetoothctl power on'.";
}
