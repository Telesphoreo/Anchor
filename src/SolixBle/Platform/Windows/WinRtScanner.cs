using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Radios;
using Windows.Storage.Streams;

namespace SolixBle;

/// <summary>WinRT adapter state and active LE scanning.</summary>
internal sealed class WinRtScanner : IBleScanner
{
    public async Task<BluetoothStatus> GetStatusAsync()
    {
        var adapter = await BluetoothAdapter.GetDefaultAsync().AsTask().ConfigureAwait(false);
        if (adapter is null)
            return BluetoothStatus.NoAdapter;

        var radio = await adapter.GetRadioAsync().AsTask().ConfigureAwait(false);
        return radio?.State == RadioState.On ? BluetoothStatus.Ready : BluetoothStatus.RadioOff;
    }

    public async Task ScanAsync(TimeSpan duration, Action<BleAdvertisement> onAdvertisement, CancellationToken ct)
    {
        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active,
        };

        watcher.Received += (_, args) => onAdvertisement(new BleAdvertisement(
            args.BluetoothAddress,
            args.Advertisement.LocalName,
            args.RawSignalStrengthInDBm,
            args.Advertisement.ServiceUuids,
            ReadManufacturerData(args.Advertisement)));

        watcher.Start();
        try
        {
            await Task.Delay(duration, ct).ConfigureAwait(false);
        }
        finally
        {
            watcher.Stop();
        }
    }

    private static List<BleManufacturerData> ReadManufacturerData(BluetoothLEAdvertisement advertisement)
    {
        var records = new List<BleManufacturerData>();
        foreach (var manufacturerData in advertisement.ManufacturerData)
        {
            var data = new byte[manufacturerData.Data.Length];
            using (var reader = DataReader.FromBuffer(manufacturerData.Data))
            {
                reader.ReadBytes(data);
            }

            records.Add(new BleManufacturerData(manufacturerData.CompanyId, data));
        }

        return records;
    }
}
