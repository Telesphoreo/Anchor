using Tmds.DBus.Protocol;

namespace SolixBle;

/// <summary>BlueZ adapter state and LE discovery over D-Bus.</summary>
internal sealed class BlueZScanner : IBleScanner
{
    public async Task<BluetoothStatus> GetStatusAsync()
    {
        var objects = await BlueZ.GetManagedObjectsAsync().ConfigureAwait(false);
        if (BlueZ.FindAdapter(objects) is not { } adapterPath)
            return BluetoothStatus.NoAdapter;

        return objects[adapterPath][BlueZ.AdapterInterface].GetBool("Powered") == true
            ? BluetoothStatus.Ready
            : BluetoothStatus.RadioOff;
    }

    /// <summary>
    /// Run an LE discovery on the default adapter. BlueZ keeps device objects
    /// after they go quiet, so a device is reported only once its RSSI or
    /// ManufacturerData updates during this discovery; after that, every
    /// update to it is reported.
    /// </summary>
    public async Task ScanAsync(TimeSpan duration, Action<BleAdvertisement> onAdvertisement, CancellationToken ct)
    {
        var gate = new object();
        var devices = new Dictionary<string, DeviceState>();
        var adapterPath = "";
        var watches = new List<IDisposable>();

        void OnDeviceProperties(string path, Dictionary<string, VariantValue> changed, string[] invalidated)
        {
            if (!path.StartsWith(adapterPath + "/", StringComparison.Ordinal))
                return;

            BleAdvertisement? advertisement;
            lock (gate)
            {
                if (!devices.TryGetValue(path, out var device))
                    devices[path] = device = new DeviceState([]);
                device.Update(changed, invalidated);

                // BlueZ only re-emits RSSI on a change of 8 dBm or more while
                // another client keeps discovery running; with DuplicateData
                // every advertisement re-emits ManufacturerData instead.
                if (changed.ContainsKey("RSSI") || changed.ContainsKey("ManufacturerData"))
                    device.Heard = true;
                advertisement = device.Heard ? device.ToAdvertisement() : null;
            }

            if (advertisement is not null)
                onAdvertisement(advertisement);
        }

        // Like Windows' watcher, a scan that cannot run finds nothing rather than failing.
        try
        {
            var objects = await BlueZ.GetManagedObjectsAsync().ConfigureAwait(false);
            if (BlueZ.FindAdapter(objects) is not { } adapter)
            {
                SolixLog.Warning("No Bluetooth adapter found; nothing to scan with.");
                return;
            }

            adapterPath = adapter;
            lock (gate)
            {
                foreach (var (path, interfaces) in objects)
                {
                    if (path.StartsWith(adapterPath + "/", StringComparison.Ordinal)
                        && interfaces.TryGetValue(BlueZ.DeviceInterface, out var properties))
                        devices[path] = new DeviceState(properties);
                }
            }

            watches.Add(await BlueZ.WatchInterfacesAddedAsync(signal =>
            {
                if (signal.Interfaces.TryGetValue(BlueZ.DeviceInterface, out var properties))
                    OnDeviceProperties(signal.Path, properties, []);
            }).ConfigureAwait(false));
            watches.Add(await BlueZ.WatchPropertiesAsync(
                path: null,
                pathNamespace: adapterPath,
                BlueZ.DeviceInterface,
                change => OnDeviceProperties(change.Path, change.Changed, change.Invalidated))
                .ConfigureAwait(false));

            await BlueZ.SetLeDiscoveryFilterAsync(adapterPath).ConfigureAwait(false);
            await BlueZ.CallAsync(adapterPath, BlueZ.AdapterInterface, "StartDiscovery").ConfigureAwait(false);
        }
        catch (DBusExceptionBase ex)
        {
            SolixLog.Warning($"Could not start Bluetooth discovery: {BlueZ.Describe(ex)}");
            watches.ForEach(w => w.Dispose());
            return;
        }

        try
        {
            await Task.Delay(duration, ct).ConfigureAwait(false);
        }
        finally
        {
            watches.ForEach(w => w.Dispose());
            try
            {
                await BlueZ.CallAsync(adapterPath, BlueZ.AdapterInterface, "StopDiscovery").ConfigureAwait(false);
            }
            catch (DBusExceptionBase ex)
            {
                SolixLog.Debug($"Could not stop Bluetooth discovery on {adapterPath}: {BlueZ.Describe(ex)}");
            }
        }
    }

    /// <summary>What BlueZ last said about one device, and whether it was heard during this scan.</summary>
    private sealed class DeviceState(Dictionary<string, VariantValue> properties)
    {
        private readonly Dictionary<string, VariantValue> _properties = new(properties);

        public bool Heard { get; set; }

        public short? Rssi =>
            _properties.TryGetValue("RSSI", out var value) && value.Type == VariantValueType.Int16
                ? value.GetInt16()
                : null;

        public void Update(Dictionary<string, VariantValue> changed, string[] invalidated)
        {
            foreach (var (name, value) in changed)
                _properties[name] = value;
            foreach (var name in invalidated)
                _properties.Remove(name);
        }

        public BleAdvertisement? ToAdvertisement()
        {
            if (_properties.GetString("Address") is not { } mac || Rssi is not { } rssi)
                return null;

            ulong address;
            try
            {
                address = SolixScanner.MacToAddress(mac);
            }
            catch (FormatException)
            {
                return null;
            }

            var name = _properties.GetDeviceName();
            var uuids = (_properties.GetStrings("UUIDs") ?? [])
                .Select(uuid => Guid.TryParse(uuid, out var guid) ? guid : Guid.Empty)
                .ToList();

            return new BleAdvertisement(address, name, rssi, uuids, _properties.GetManufacturerData() ?? []);
        }
    }
}
