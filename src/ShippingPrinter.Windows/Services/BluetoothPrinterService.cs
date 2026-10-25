using System.Globalization;
using System.IO;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace ShippingPrinter.Windows.Services;

public sealed record BluetoothPrinterDevice(string Id, string Name)
{
    public short? SignalStrengthDbm { get; init; }
    public override string ToString() => Name;
}

public sealed record BleWriteTarget(Guid ServiceUuid, Guid CharacteristicUuid, string DisplayName,
    string Properties = "")
{
    public override string ToString() => DisplayName;
}

public sealed record BleTransferOptions(int MaxChunkBytes = 20, int InterChunkDelayMs = 5, bool RequireWriteWithResponse = false)
{
    public void Validate()
    {
        if (MaxChunkBytes is < 1 or > 512) throw new ArgumentOutOfRangeException(nameof(MaxChunkBytes), "Bluetooth chunks must be between 1 and 512 bytes.");
        if (InterChunkDelayMs is < 0 or > 1000) throw new ArgumentOutOfRangeException(nameof(InterChunkDelayMs), "Bluetooth pacing must be between 0 and 1000 milliseconds.");
    }
}

public sealed record BleCharacteristicInspection(Guid Uuid, string Properties, bool CanWrite, bool CanWriteWithoutResponse);
public sealed record BleServiceInspection(Guid Uuid, string Status, byte? ProtocolError,
    IReadOnlyList<BleCharacteristicInspection> Characteristics);
public sealed record BleDeviceInspection(DateTimeOffset CapturedUtc, string DeviceId, string Name,
    string AddressType, string ConnectionStatus, string ServiceDiscoveryStatus, byte? ProtocolError,
    IReadOnlyList<BleServiceInspection> Services)
{
    public string Scope => "Live GATT service and characteristic enumeration only. No characteristic values were read or written, no print job was sent, and this does not verify printer compatibility.";
}

/// <summary>
/// Windows BLE transport. Advertisement discovery includes unpaired peripherals;
/// discovery and writable properties alone do not establish printer compatibility.
/// Invoke from the UI thread because Windows can request device-access consent.
/// </summary>
public static class BluetoothPrinterService
{
    private static readonly SemaphoreSlim TransferGate = new(1, 1);

    public static async Task<IReadOnlyList<BluetoothPrinterDevice>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var adapter = await BluetoothAdapter.GetDefaultAsync().AsTask(cancellationToken);
        if (adapter is null || !adapter.IsLowEnergySupported)
            throw new InvalidOperationException("No Bluetooth Low Energy adapter is available on this computer.");

        var found = new Dictionary<string, BluetoothPrinterDevice>(StringComparer.Ordinal);
        var sync = new object();
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        BluetoothError? scanError = null;
        TypedEventHandler<BluetoothLEAdvertisementWatcher, BluetoothLEAdvertisementReceivedEventArgs> received = (_, args) =>
        {
            if (args.BluetoothAddress == 0 || args.BluetoothAddressType is not (BluetoothAddressType.Public or BluetoothAddressType.Random)) return;
            // Preserve the address type: random addresses cannot safely be opened as public addresses.
            string id = FormatDeviceId(args.BluetoothAddress, args.BluetoothAddressType);
            string localName = args.Advertisement.LocalName?.Trim() ?? "";
            lock (sync)
            {
                found.TryGetValue(id, out var existing);
                string name = localName.Length > 0 ? localName : existing?.Name ?? $"Unnamed BLE device ({args.BluetoothAddress:X12})";
                found[id] = new BluetoothPrinterDevice(id, name) { SignalStrengthDbm = args.RawSignalStrengthInDBm };
            }
        };
        TypedEventHandler<BluetoothLEAdvertisementWatcher, BluetoothLEAdvertisementWatcherStoppedEventArgs> stopped = (_, args) =>
        {
            lock (sync) scanError = args.Error;
        };
        watcher.Received += received;
        watcher.Stopped += stopped;
        try
        {
            watcher.Start();
            await Task.Delay(TimeSpan.FromSeconds(8), cancellationToken);
            lock (sync)
            {
                if (watcher.Status == BluetoothLEAdvertisementWatcherStatus.Aborted ||
                    scanError is not null and not BluetoothError.Success)
                    throw new IOException($"Bluetooth discovery stopped ({scanError?.ToString() ?? "Aborted"}). Turn Bluetooth on in Windows and try again.");
                return found.Values.OrderBy(device => device.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }
        finally
        {
            watcher.Received -= received;
            watcher.Stopped -= stopped;
            if (watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started) watcher.Stop();
        }
    }

    /// <summary>Queries live service metadata, without reading values, subscribing, or writing commands.</summary>
    public static async Task<BleDeviceInspection> InspectAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var token = timeout.Token;
        try
        {
            using var device = await OpenDeviceAsync(deviceId, token);
            var result = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask(token);
            var services = new List<BleServiceInspection>();
            try
            {
                foreach (var service in result.Services)
                {
                    token.ThrowIfCancellationRequested();
                    var characteristics = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(token);
                    services.Add(new BleServiceInspection(service.Uuid, characteristics.Status.ToString(), characteristics.ProtocolError,
                        characteristics.Characteristics.Select(characteristic => new BleCharacteristicInspection(
                            characteristic.Uuid, characteristic.CharacteristicProperties.ToString(),
                            characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write),
                            characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse))).ToArray()));
                }
                return new BleDeviceInspection(DateTimeOffset.UtcNow, deviceId, device.Name,
                    device.BluetoothAddressType.ToString(), device.ConnectionStatus.ToString(), result.Status.ToString(),
                    result.ProtocolError, services);
            }
            finally
            {
                foreach (var service in result.Services) service.Dispose();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("Bluetooth inspection timed out. Keep the printer nearby and disconnect it from Shipping Printer on your phone, then scan again.");
        }
    }

    public static async Task<IReadOnlyList<BleWriteTarget>> GetCharacteristicsAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var inspection = await InspectAsync(deviceId, cancellationToken);
        if (inspection.ServiceDiscoveryStatus != GattCommunicationStatus.Success.ToString())
            throw new IOException($"Discover Bluetooth services failed ({inspection.ServiceDiscoveryStatus}). Check printer power, disconnect the phone app, and scan again.");
        var targets = GetWriteTargets(inspection);
        if (targets.Count == 0)
            throw new IOException("No accessible writable BLE characteristic was found. This device may require pairing or a different transport. Save Bluetooth details to inspect its reported services.");
        return targets;
    }

    /// <summary>Builds UI choices from an existing inspection without contacting the device again.</summary>
    public static IReadOnlyList<BleWriteTarget> GetWriteTargets(BleDeviceInspection inspection) =>
        inspection.Services.Where(service => service.Status == GattCommunicationStatus.Success.ToString())
            .SelectMany(service => service.Characteristics.Where(characteristic => characteristic.CanWrite || characteristic.CanWriteWithoutResponse)
                .Select(characteristic => new BleWriteTarget(service.Uuid, characteristic.Uuid,
                    $"{ShortUuid(service.Uuid)} / {ShortUuid(characteristic.Uuid)} · {(characteristic.CanWrite ? "write" : "write without response")}",
                    characteristic.Properties))).ToArray();

    public static async Task SendAsync(string deviceId, Guid serviceUuid, Guid characteristicUuid, byte[] data,
        CancellationToken cancellationToken = default, IProgress<double>? progress = null, BleTransferOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentNullException.ThrowIfNull(data);
        if (serviceUuid == Guid.Empty || characteristicUuid == Guid.Empty)
            throw new ArgumentException("Select the printer's writable Bluetooth characteristic first.");
        if (data.Length == 0 || data.Length > 16 * 1024 * 1024)
            throw new ArgumentException("The Bluetooth print job must contain between 1 byte and 16 MB.");
        options ??= new BleTransferOptions();
        options.Validate();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var token = timeout.Token;
        await TransferGate.WaitAsync(token);
        int accepted = 0;
        bool attemptedWrite = false;
        try
        {
            using var device = await OpenDeviceAsync(deviceId, token);
            var services = await device.GetGattServicesForUuidAsync(serviceUuid, BluetoothCacheMode.Uncached).AsTask(token);
            try
            {
                EnsureSuccess(services.Status, "Open printer service", services.ProtocolError);
                var service = services.Services.FirstOrDefault()
                    ?? throw new IOException("The selected printer service is no longer available. Discover the printer again.");
                var characteristics = await service.GetCharacteristicsForUuidAsync(characteristicUuid, BluetoothCacheMode.Uncached).AsTask(token);
                EnsureSuccess(characteristics.Status, "Open printer characteristic", characteristics.ProtocolError);
                var characteristic = characteristics.Characteristics.FirstOrDefault()
                    ?? throw new IOException("The selected printer characteristic is no longer available.");
                var properties = characteristic.CharacteristicProperties;
                if (options.RequireWriteWithResponse && !properties.HasFlag(GattCharacteristicProperties.Write))
                    throw new IOException("This printer profile requires acknowledged Bluetooth writes, but the selected characteristic does not support them. Select a characteristic with the Write property.");
                var option = properties.HasFlag(GattCharacteristicProperties.Write)
                    ? GattWriteOption.WriteWithResponse
                    : properties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)
                        ? GattWriteOption.WriteWithoutResponse
                        : throw new IOException("The selected characteristic does not accept writes.");
                using var session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId).AsTask(token);

                progress?.Report(0);
                for (int offset = 0; offset < data.Length;)
                {
                    token.ThrowIfCancellationRequested();
                    // Never exceed the current Windows ATT payload, even when an app profile permits 512 bytes.
                    int count = Math.Min(GetChunkSize(options, session?.MaxPduSize ?? 23), data.Length - offset);
                    using var writer = new DataWriter();
                    writer.WriteBytes(data.AsSpan(offset, count).ToArray());
                    attemptedWrite = true;
                    var result = await characteristic.WriteValueWithResultAsync(writer.DetachBuffer(), option).AsTask(token);
                    EnsureSuccess(result.Status, "Send printer data", result.ProtocolError);
                    offset += count;
                    accepted = offset;
                    progress?.Report((double)offset / data.Length);
                    if (offset < data.Length && options.InterChunkDelayMs > 0) await Task.Delay(options.InterChunkDelayMs, token);
                }
            }
            finally
            {
                foreach (var service in services.Services) service.Dispose();
            }
        }
        catch (OperationCanceledException) when (attemptedWrite)
        {
            string reason = cancellationToken.IsCancellationRequested ? "cancelled" : "timed out";
            throw new OperationCanceledException($"Bluetooth transfer {reason} after Windows accepted {accepted:N0} bytes. An in-flight write may also have reached the printer. Clear its buffer or restart it before retrying.", token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("Bluetooth connection timed out before any print data was sent. Disconnect the phone app and scan again.");
        }
        catch (Exception ex) when (attemptedWrite && ex is not OperationCanceledException)
        {
            throw new IOException($"Bluetooth transfer failed after Windows accepted {accepted:N0} bytes. The printer may contain a partial job; clear its buffer or restart it before retrying. {ex.Message}", ex);
        }
        finally { TransferGate.Release(); }
    }

    public static int GetChunkSize(BleTransferOptions options, ushort maxPduSize)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        // An unavailable/invalid MTU uses BLE's mandatory minimum instead of attempting an oversized write.
        int attPayload = maxPduSize >= 23 ? maxPduSize - 3 : 20;
        return Math.Min(options.MaxChunkBytes, Math.Min(512, attPayload));
    }

    public static string FormatDeviceId(ulong address, BluetoothAddressType addressType)
    {
        if (address == 0 || address > 0xffffffffffff) throw new ArgumentOutOfRangeException(nameof(address));
        if (addressType is not (BluetoothAddressType.Public or BluetoothAddressType.Random)) throw new ArgumentOutOfRangeException(nameof(addressType));
        return $"ble:{(addressType == BluetoothAddressType.Random ? "random" : "public")}:{address:X12}";
    }

    public static bool TryParseDeviceId(string deviceId, out ulong address, out BluetoothAddressType addressType)
    {
        address = 0; addressType = BluetoothAddressType.Public;
        var parts = deviceId.Split(':');
        if (parts.Length != 3 || parts[0] != "ble" || parts[1] is not ("public" or "random") || parts[2].Length != 12 ||
            !ulong.TryParse(parts[2], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out address) || address == 0)
            return false;
        addressType = parts[1] == "random" ? BluetoothAddressType.Random : BluetoothAddressType.Public;
        return true;
    }

    private static async Task<BluetoothLEDevice> OpenDeviceAsync(string deviceId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BluetoothLEDevice? device;
        if (deviceId.StartsWith("ble:", StringComparison.Ordinal))
        {
            if (!TryParseDeviceId(deviceId, out var address, out var addressType))
                throw new ArgumentException("The saved Bluetooth address is invalid. Scan for the printer again.", nameof(deviceId));
            device = await BluetoothLEDevice.FromBluetoothAddressAsync(address, addressType).AsTask(cancellationToken);
        }
        else
        {
            // Keep compatibility with selections made by the earlier Windows association-endpoint scanner.
            device = await BluetoothLEDevice.FromIdAsync(deviceId).AsTask(cancellationToken);
        }
        return device ?? throw new IOException("The Bluetooth device could not be opened. Turn it on, disconnect it from your phone, and scan again. Pair in Windows only if the printer requires it.");
    }

    private static string ShortUuid(Guid uuid)
    {
        string text = uuid.ToString();
        return text.EndsWith("-0000-1000-8000-00805f9b34fb", StringComparison.OrdinalIgnoreCase) ? text[..8] : text;
    }

    private static void EnsureSuccess(GattCommunicationStatus status, string operation, byte? protocolError = null)
    {
        if (status != GattCommunicationStatus.Success)
            throw new IOException($"{operation} failed ({status}{(protocolError is byte code ? $", ATT 0x{code:X2}" : "")}). Check the printer's power, Bluetooth connection, and Windows permissions.");
    }
}
