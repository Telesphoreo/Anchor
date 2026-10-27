using Tmds.DBus.Protocol;

namespace SolixBle;

/// <summary>Properties of one D-Bus object, by interface name.</summary>
internal sealed class InterfaceProperties : Dictionary<string, Dictionary<string, VariantValue>>;

/// <summary>A PropertiesChanged signal: the object, the changed values and the invalidated names.</summary>
internal readonly record struct PropertiesChange(
    string Path, Dictionary<string, VariantValue> Changed, string[] Invalidated);

/// <summary>
/// The BlueZ calls and signals the Linux transport uses, over the system bus.
/// See BlueZ's doc/org.bluez.Adapter.rst, org.bluez.Device.rst and
/// org.bluez.GattCharacteristic.rst.
/// </summary>
internal static class BlueZ
{
    public const string Service = "org.bluez";
    public const string AdapterInterface = "org.bluez.Adapter1";
    public const string DeviceInterface = "org.bluez.Device1";
    public const string CharacteristicInterface = "org.bluez.GattCharacteristic1";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";
    private const string ObjectManagerInterface = "org.freedesktop.DBus.ObjectManager";

    private static DBusConnection Bus => DBusConnection.System;

    /// <summary>Every BlueZ object with its interfaces and properties.</summary>
    public static Task<Dictionary<string, InterfaceProperties>> GetManagedObjectsAsync()
    {
        return Bus.CallMethodAsync(
            CreateCall("/", ObjectManagerInterface, "GetManagedObjects"),
            static (message, _) =>
            {
                var reader = message.GetBodyReader();
                var objects = new Dictionary<string, InterfaceProperties>();
                var end = reader.ReadDictionaryStart();
                while (reader.HasNext(end))
                {
                    var path = reader.ReadObjectPathAsString();
                    objects[path] = ReadInterfaces(ref reader);
                }

                return objects;
            });
    }

    /// <summary>The default adapter: hci0, else the first one BlueZ lists, else null.</summary>
    public static string? FindAdapter(Dictionary<string, InterfaceProperties> objects)
    {
        var adapters = objects
            .Where(o => o.Value.ContainsKey(AdapterInterface))
            .Select(o => o.Key)
            .Order(StringComparer.Ordinal)
            .ToList();
        return adapters.Contains("/org/bluez/hci0") ? "/org/bluez/hci0" : adapters.FirstOrDefault();
    }

    /// <summary>The object path BlueZ gives a device on an adapter.</summary>
    public static string DevicePath(string adapterPath, ulong bluetoothAddress) =>
        $"{adapterPath}/dev_{SolixScanner.AddressToMac(bluetoothAddress).Replace(':', '_')}";

    /// <summary>Call a BlueZ method that takes no arguments and returns nothing.</summary>
    public static Task CallAsync(string path, string @interface, string member) =>
        Bus.CallMethodAsync(CreateCall(path, @interface, member));

    /// <summary>
    /// Restrict this client's discovery on the adapter to LE, with duplicate
    /// detection off so every advertisement re-emits ManufacturerData.
    /// </summary>
    public static Task SetLeDiscoveryFilterAsync(string adapterPath)
    {
        using var writer = Bus.GetMessageWriter();
        writer.WriteMethodCallHeader(Service, adapterPath, AdapterInterface, "SetDiscoveryFilter", "a{sv}");
        var start = writer.WriteDictionaryStart();
        writer.WriteDictionaryEntryStart();
        writer.WriteString("Transport");
        writer.WriteVariantString("le");
        writer.WriteDictionaryEntryStart();
        writer.WriteString("DuplicateData");
        writer.WriteVariantBool(true);
        writer.WriteDictionaryEnd(start);
        return Bus.CallMethodAsync(writer.CreateMessage());
    }

    /// <summary>Write a characteristic value; <paramref name="type"/> is "request" or "command".</summary>
    public static Task WriteValueAsync(string characteristicPath, byte[] value, string type)
    {
        using var writer = Bus.GetMessageWriter();
        writer.WriteMethodCallHeader(Service, characteristicPath, CharacteristicInterface, "WriteValue", "aya{sv}");
        writer.WriteArray(value);
        var start = writer.WriteDictionaryStart();
        writer.WriteDictionaryEntryStart();
        writer.WriteString("type");
        writer.WriteVariantString(type);
        writer.WriteDictionaryEnd(start);
        return Bus.CallMethodAsync(writer.CreateMessage());
    }

    /// <summary>Read one property.</summary>
    public static Task<VariantValue> GetPropertyAsync(string path, string @interface, string name)
    {
        using var writer = Bus.GetMessageWriter();
        writer.WriteMethodCallHeader(Service, path, PropertiesInterface, "Get", "ss");
        writer.WriteString(@interface);
        writer.WriteString(name);
        return Bus.CallMethodAsync(
            writer.CreateMessage(),
            static (message, _) => message.GetBodyReader().ReadVariantValue());
    }

    /// <summary>
    /// Watch PropertiesChanged of <paramref name="interface"/> on one object
    /// (<paramref name="path"/>) or on every object under <paramref name="pathNamespace"/>.
    /// <paramref name="onClosed"/>, if given, runs once if the system bus connection closes.
    /// </summary>
    public static ValueTask<IDisposable> WatchPropertiesAsync(
        string? path,
        string? pathNamespace,
        string @interface,
        Action<PropertiesChange> onChange,
        Action<Exception>? onClosed = null)
    {
        var rule = new MatchRule
        {
            Type = MessageType.Signal,
            Sender = Service,
            Path = path,
            PathNamespace = pathNamespace,
            Interface = PropertiesInterface,
            Member = "PropertiesChanged",
            Arg0 = @interface,
        };
        return Bus.AddMatchAsync(
            rule,
            static (message, _) =>
            {
                var reader = message.GetBodyReader();
                reader.ReadString();
                var changed = reader.ReadDictionaryOfStringToVariantValue();
                var invalidated = reader.ReadArrayOfString();
                return new PropertiesChange(message.PathAsString ?? "", changed, invalidated);
            },
            (Notification<PropertiesChange> notification) => Dispatch(notification, onChange, onClosed),
            emitOnCapturedContext: false,
            flags: onClosed is null ? ObserverFlags.None : ObserverFlags.EmitOnConnectionClosed);
    }

    /// <summary>Watch BlueZ objects (or interfaces on them) appearing.</summary>
    public static ValueTask<IDisposable> WatchInterfacesAddedAsync(Action<(string Path, InterfaceProperties Interfaces)> onAdded)
    {
        var rule = new MatchRule
        {
            Type = MessageType.Signal,
            Sender = Service,
            Path = "/",
            Interface = ObjectManagerInterface,
            Member = "InterfacesAdded",
        };
        return Bus.AddMatchAsync(
            rule,
            static (message, _) =>
            {
                var reader = message.GetBodyReader();
                var path = reader.ReadObjectPathAsString();
                return (path, ReadInterfaces(ref reader));
            },
            (Notification<(string Path, InterfaceProperties Interfaces)> notification) =>
                Dispatch(notification, onAdded, onClosed: null),
            emitOnCapturedContext: false);
    }

    /// <summary>Watch BlueZ objects (or interfaces on them) disappearing.</summary>
    public static ValueTask<IDisposable> WatchInterfacesRemovedAsync(Action<(string Path, string[] Interfaces)> onRemoved)
    {
        var rule = new MatchRule
        {
            Type = MessageType.Signal,
            Sender = Service,
            Path = "/",
            Interface = ObjectManagerInterface,
            Member = "InterfacesRemoved",
        };
        return Bus.AddMatchAsync(
            rule,
            static (message, _) =>
            {
                var reader = message.GetBodyReader();
                return (reader.ReadObjectPathAsString(), reader.ReadArrayOfString());
            },
            (Notification<(string Path, string[] Interfaces)> notification) =>
                Dispatch(notification, onRemoved, onClosed: null),
            emitOnCapturedContext: false);
    }

    /// <summary>Read a property as a string, or null if absent or of another type.</summary>
    public static string? GetString(this Dictionary<string, VariantValue> properties, string name) =>
        properties.TryGetValue(name, out var value) && value.Type == VariantValueType.String ? value.GetString() : null;

    /// <summary>Read a property as a bool, or null if absent or of another type.</summary>
    public static bool? GetBool(this Dictionary<string, VariantValue> properties, string name) =>
        properties.TryGetValue(name, out var value) && value.Type == VariantValueType.Bool ? value.GetBool() : null;

    /// <summary>Read a property as a string array, or null if absent or of another type.</summary>
    public static string[]? GetStrings(this Dictionary<string, VariantValue> properties, string name) =>
        properties.TryGetValue(name, out var value)
        && value.Type == VariantValueType.Array
        && value.ItemType == VariantValueType.String
            ? value.GetArray<string>()
            : null;

    /// <summary>
    /// A Device1's Name, else its Alias. BlueZ fills Alias with the address
    /// (dash-separated) when the device has no name; that is not a name.
    /// </summary>
    public static string? GetDeviceName(this Dictionary<string, VariantValue> device)
    {
        if (device.GetString("Name") is { } name)
            return name;
        var alias = device.GetString("Alias");
        return alias != device.GetString("Address")?.Replace(':', '-') ? alias : null;
    }

    /// <summary>Read a Device1 ManufacturerData (a{qv} of byte arrays) property, or null if absent.</summary>
    public static List<BleManufacturerData>? GetManufacturerData(this Dictionary<string, VariantValue> properties)
    {
        if (!properties.TryGetValue("ManufacturerData", out var value)
            || value.Type != VariantValueType.Dictionary
            || value.KeyType != VariantValueType.UInt16)
            return null;

        var records = new List<BleManufacturerData>();
        for (var i = 0; i < value.Count; i++)
        {
            var (key, data) = value.GetDictionaryEntry(i);
            if (data.Type == VariantValueType.Array && data.ItemType == VariantValueType.Byte)
                records.Add(new BleManufacturerData(key.GetUInt16(), data.GetArray<byte>()));
        }

        return records;
    }

    /// <summary>Whether <paramref name="ex"/> is the D-Bus error reply <paramref name="errorName"/>.</summary>
    public static bool IsError(Exception ex, string errorName) =>
        ex is DBusErrorReplyException reply && reply.ErrorName == errorName;

    /// <summary>A D-Bus failure as one line, with the error name when the bus returned one.</summary>
    public static string Describe(Exception ex) =>
        ex is DBusErrorReplyException reply ? $"{reply.ErrorName}: {reply.ErrorMessage}" : ex.Message;

    private static MessageBuffer CreateCall(string path, string @interface, string member)
    {
        using var writer = Bus.GetMessageWriter();
        writer.WriteMethodCallHeader(Service, path, @interface, member);
        return writer.CreateMessage();
    }

    private static InterfaceProperties ReadInterfaces(ref Reader reader)
    {
        var interfaces = new InterfaceProperties();
        var end = reader.ReadDictionaryStart();
        while (reader.HasNext(end))
        {
            var name = reader.ReadString();
            interfaces[name] = reader.ReadDictionaryOfStringToVariantValue();
        }

        return interfaces;
    }

    private static void Dispatch<T>(Notification<T> notification, Action<T> onValue, Action<Exception>? onClosed)
    {
        try
        {
            if (notification.HasValue)
                onValue(notification.Value);
            else if (notification.IsCompletion)
                onClosed?.Invoke(notification.Exception);
        }
        catch (Exception ex)
        {
            SolixLog.Error($"Exception handling a BlueZ signal! {ex}");
        }
    }
}
