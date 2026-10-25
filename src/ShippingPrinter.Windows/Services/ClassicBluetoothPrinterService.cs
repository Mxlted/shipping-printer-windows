using System.IO;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace ShippingPrinter.Windows.Services;

public sealed record ClassicPrinterDevice(string Id, string Name, bool IsPaired)
{
    public override string ToString() => Name;
}

public sealed record ClassicServiceInspection(Guid Uuid, string ConnectionServiceName,
    string ProtectionLevel, string MaxProtectionLevel);

public sealed record ClassicDeviceInspection(DateTimeOffset CapturedUtc, string DeviceId, string Name,
    bool IsPaired, string ConnectionStatus, string ServiceDiscoveryStatus,
    IReadOnlyList<ClassicServiceInspection> Services)
{
    public int ServiceCount => Services.Count;
    public bool RequiresPairing => !IsPaired;
    public string Scope => "Live Bluetooth Classic Serial Port service discovery only. No pairing, socket connection, or print data was sent. Service availability does not establish TSPL compatibility or printed output.";
}

/// <summary>
/// Sends existing TSPL bytes through a paired printer's Bluetooth Classic Serial Port service.
/// Call from the UI thread: Windows may need device-access consent. Pairing is an explicit
/// Windows Settings action; this class never initiates pairing or retries a partial job.
/// WinRT cancellation is cooperative and does not guarantee a hard connection deadline.
/// </summary>
public static class ClassicBluetoothPrinterService
{
    private static readonly SemaphoreSlim TransferGate = new(1, 1);
    private const int ChunkBytes = 512;

    public static async Task<IReadOnlyList<ClassicPrinterDevice>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var adapter = await BluetoothAdapter.GetDefaultAsync().AsTask(cancellationToken);
        if (adapter is null || !adapter.IsClassicSupported)
            throw new InvalidOperationException("No Bluetooth Classic adapter is available on this computer.");

        var found = new Dictionary<string, DeviceInformation>(StringComparer.Ordinal);
        var sync = new object();
        // Explicitly include both populations. An unpaired-only scan hides an already paired printer.
        string selector = $"({BluetoothDevice.GetDeviceSelectorFromPairingState(true)}) OR ({BluetoothDevice.GetDeviceSelectorFromPairingState(false)})";
        var watcher = DeviceInformation.CreateWatcher(selector, null, DeviceInformationKind.AssociationEndpoint);
        Exception? discoveryError = null;
        TypedEventHandler<DeviceWatcher, DeviceInformation> added = (_, item) =>
        {
            lock (sync) found[item.Id] = item;
        };
        TypedEventHandler<DeviceWatcher, DeviceInformationUpdate> updated = (_, item) =>
        {
            lock (sync)
            {
                if (!found.TryGetValue(item.Id, out var device)) return;
                try { device.Update(item); }
                catch (Exception ex) { discoveryError = ex; }
            }
        };
        TypedEventHandler<DeviceWatcher, DeviceInformationUpdate> removed = (_, item) =>
        {
            lock (sync) found.Remove(item.Id);
        };
        watcher.Added += added;
        watcher.Updated += updated;
        watcher.Removed += removed;
        try
        {
            watcher.Start();
            await Task.Delay(TimeSpan.FromSeconds(12), cancellationToken);
            lock (sync)
            {
                if (watcher.Status == DeviceWatcherStatus.Aborted)
                    throw new IOException("Bluetooth Classic discovery stopped. Turn Bluetooth on in Windows and scan again.");
                if (discoveryError is not null)
                    throw new IOException("Windows could not update the Bluetooth device list. Scan again.", discoveryError);
                return found.Values.Select(device => new ClassicPrinterDevice(device.Id,
                        string.IsNullOrWhiteSpace(device.Name) ? "Unnamed Bluetooth Classic device" : device.Name,
                        device.Pairing.IsPaired))
                    .OrderBy(device => device.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }
        finally
        {
            watcher.Added -= added;
            watcher.Updated -= updated;
            watcher.Removed -= removed;
            if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) watcher.Stop();
        }
    }

    /// <summary>Queries Serial Port service metadata without pairing or opening a data socket.</summary>
    public static async Task<ClassicDeviceInspection> InspectAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var token = timeout.Token;
        try
        {
            using var device = await OpenDeviceAsync(deviceId, token);
            var result = await device.GetRfcommServicesForIdAsync(RfcommServiceId.SerialPort, BluetoothCacheMode.Uncached).AsTask(token);
            try
            {
                token.ThrowIfCancellationRequested();
                return new ClassicDeviceInspection(DateTimeOffset.UtcNow, deviceId, device.Name,
                    device.DeviceInformation.Pairing.IsPaired, device.ConnectionStatus.ToString(), result.Error.ToString(),
                    result.Services.Select(service => new ClassicServiceInspection(service.ServiceId.Uuid,
                        service.ConnectionServiceName, service.ProtectionLevel.ToString(), service.MaxProtectionLevel.ToString())).ToArray());
            }
            finally
            {
                foreach (var service in result.Services) service.Dispose();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("Bluetooth Classic inspection timed out. Keep the printer nearby, disconnect the phone app, and try again.");
        }
    }

    public static async Task SendAsync(string deviceId, byte[] data, CancellationToken cancellationToken = default,
        IProgress<double>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length == 0 || data.Length > 16 * 1024 * 1024)
            throw new ArgumentException("The Bluetooth print job must contain between 1 byte and 16 MB.", nameof(data));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var token = timeout.Token;
        await TransferGate.WaitAsync(token);
        int accepted = 0;
        bool attemptedWrite = false;
        try
        {
            using var device = await OpenDeviceAsync(deviceId, token);
            token.ThrowIfCancellationRequested();
            if (!device.DeviceInformation.Pairing.IsPaired)
                throw new IOException("Pair this printer in Windows Bluetooth settings first. For OFFNOVA N-6140, select the name without _LE and use PIN 0000 if prompted, then scan again. No print data was sent.");

            var result = await device.GetRfcommServicesForIdAsync(RfcommServiceId.SerialPort, BluetoothCacheMode.Uncached).AsTask(token);
            try
            {
                token.ThrowIfCancellationRequested();
                if (result.Error != BluetoothError.Success)
                    throw new IOException($"Bluetooth Serial Port discovery failed ({result.Error}). Check printer power, pairing, and distance; disconnect the phone app before retrying.");
                var services = result.Services.Where(service => service.ServiceId.Uuid == RfcommServiceId.SerialPort.Uuid).ToArray();
                if (services.Length == 0)
                    throw new IOException("This device does not expose an accessible Bluetooth Serial Port service. Pair its ordinary Bluetooth entry in Windows, or use the printer's BLE or installed-driver route.");
                if (services.Length != 1)
                    throw new IOException("This device exposes multiple Bluetooth Serial Port endpoints. Automatic endpoint selection would be ambiguous; use the documented outgoing COM port with the OFFNOVA Windows driver.");
                var service = services[0];

                using var socket = new StreamSocket();
                // Microsoft's RFCOMM client pattern requests encryption without requiring MITM authentication.
                // https://learn.microsoft.com/windows/uwp/devices-sensors/send-or-receive-files-with-rfcomm
                await socket.ConnectAsync(service.ConnectionHostName, service.ConnectionServiceName,
                    SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication).AsTask(token);
                using var writer = new DataWriter(socket.OutputStream);
                progress?.Report(0);
                for (int offset = 0; offset < data.Length;)
                {
                    token.ThrowIfCancellationRequested();
                    int count = Math.Min(ChunkBytes, data.Length - offset);
                    writer.WriteBytes(data.AsSpan(offset, count).ToArray());
                    attemptedWrite = true;
                    uint stored = await writer.StoreAsync().AsTask(token);
                    accepted += (int)Math.Min(stored, (uint)count);
                    if (stored != count)
                        throw new IOException($"Windows stored {stored} of {count} bytes in this Bluetooth chunk. The job was stopped without retrying.");
                    offset += count;
                    progress?.Report((double)offset / data.Length);
                }
                token.ThrowIfCancellationRequested();
                if (!await writer.FlushAsync().AsTask(token))
                    throw new IOException("Windows could not flush the Bluetooth output stream.");
                // Completion means stream acceptance only; there is no physical print acknowledgement.
            }
            finally
            {
                foreach (var service in result.Services) service.Dispose();
            }
        }
        catch (OperationCanceledException) when (attemptedWrite)
        {
            string reason = cancellationToken.IsCancellationRequested ? "cancelled" : "timed out";
            throw new OperationCanceledException($"Bluetooth Classic transfer {reason} after Windows accepted {accepted:N0} bytes. An in-flight write may also have reached the printer. Clear its buffer or restart it before retrying.", token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("Bluetooth Classic connection timed out before any print data was sent. Check pairing, move the printer closer, and disconnect the phone app.");
        }
        catch (Exception ex) when (attemptedWrite && ex is not OperationCanceledException)
        {
            throw new IOException($"Bluetooth Classic transfer failed after Windows accepted {accepted:N0} bytes. The printer may contain a partial job; clear its buffer or restart it before retrying. {ex.Message}", ex);
        }
        finally { TransferGate.Release(); }
    }

    private static async Task<BluetoothDevice> OpenDeviceAsync(string deviceId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return await BluetoothDevice.FromIdAsync(deviceId).AsTask(token)
            ?? throw new IOException("The Bluetooth Classic device could not be opened. Turn it on, move it nearby, and scan again. Pair it in Windows Bluetooth settings before printing.");
    }
}
