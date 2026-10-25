using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ShippingPrinter.Windows.Models;
namespace ShippingPrinter.Windows.Services;

public static class SettingsStore
{
    public static string DirectoryPath => Environment.GetEnvironmentVariable("SHIPPING_PRINTER_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShippingPrinter.Windows");
    public static PrintSettings Load()
    {
        try
        {
            var path = Path.Combine(Path.GetFullPath(DirectoryPath), "settings.json");
            PrintSettings value = new();
            WithLock(path, () =>
            {
                if (!File.Exists(path)) return;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                value = JsonSerializer.Deserialize<PrintSettings>(stream) ?? new();
                value.Validate();
            });
            return value;
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or UnauthorizedAccessException) { return new(); }
    }
    public static void Save(PrintSettings settings)
    {
        settings.Validate();
        var directory = Path.GetFullPath(DirectoryPath);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        WithLock(path, () =>
        {
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporary, path, true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        });
    }

    private static void WithLock(string path, Action action)
    {
        // Serialize readers and writers across app instances. A Windows rename can
        // fail while the previous file is still held open by a concurrent reader.
        var pathHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
        using var gate = new Mutex(false, @"Local\ShippingPrinter.Settings." + pathHash);
        bool acquired = false;
        try
        {
            try { acquired = gate.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("Another Shipping Printer window is saving settings. Try saving again.");
            action();
        }
        finally
        {
            if (acquired) gate.ReleaseMutex();
        }
    }
}
