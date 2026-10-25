using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ShippingPrinter.Windows;
using ShippingPrinter.Windows.Models;
using ShippingPrinter.Windows.Services;
using Windows.Devices.Bluetooth;
using ZXing;

internal static class Program
{
    private static readonly List<CheckResult> Results = [];
    private static string Artifacts = "";

    [STAThread]
    private static int Main(string[] args)
    {
        Artifacts = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts");
        Directory.CreateDirectory(Artifacts);
        var previousDataPath = Environment.GetEnvironmentVariable("SHIPPING_PRINTER_DATA");
        Environment.SetEnvironmentVariable("SHIPPING_PRINTER_DATA", Path.Combine(Artifacts, "settings"));
        // Use WPF's application lifecycle so its rendering and dispatcher resources
        // shut down in the same order as the desktop app, without opening a window.
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int exit = 2;
        application.Startup += async (_, _) =>
        {
            try
            {
                await RunChecksAsync();
                exit = Results.All(r => r.Passed) ? 0 : 1;
            }
            catch (Exception ex)
            {
                Results.Add(new("runner", false, 0, ex.ToString()));
                exit = 2;
            }
            finally
            {
                var report = new
                {
                    CompletedUtc = DateTimeOffset.UtcNow,
                    Scope = "Offline Windows/WPF service and protocol tests; no printer enumeration, network access, radio scans, Bluetooth connections, print jobs, or physical hardware.",
                    Passed = Results.Count(r => r.Passed),
                    Failed = Results.Count(r => !r.Passed),
                    Results
                };
                File.WriteAllText(Path.Combine(Artifacts, "checks.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"{report.Passed} passed; {report.Failed} failed. Report: {Path.Combine(Artifacts, "checks.json")}");
                Environment.SetEnvironmentVariable("SHIPPING_PRINTER_DATA", previousDataPath);
                application.Shutdown(exit);
            }
        };
        int actualExit = application.Run();
        Console.WriteLine($"WPF application shutdown completed; returning exit code {actualExit}.");
        return actualExit;
    }

    private static async Task RunChecksAsync()
    {
        await Check("QR payload round trip, including Unicode", () =>
        {
            const string content = "Shipping Printer | café | Label 002194";
            var bitmap = ImageService.Barcode(content, BarcodeFormat.QR_CODE, true);
            Equal(content, ImageService.DecodeBarcode(bitmap));
            Require(bitmap.IsFrozen, "Generated QR bitmap must be frozen for safe transfer.");
            ImageService.SavePng(bitmap, Path.Combine(Artifacts, "qr.png"));
        });
        await Check("Code 128 payload round trip with caption", () =>
        {
            const string content = "SP-001234567890";
            var bitmap = ImageService.Barcode(content, BarcodeFormat.CODE_128, true);
            Equal(content, ImageService.DecodeBarcode(bitmap));
            ImageService.SavePng(bitmap, Path.Combine(Artifacts, "code128.png"));
        });
        await Check("Barcode input validation and blank-image decode", () =>
        {
            Throws<ArgumentException>(() => ImageService.Barcode("  ", BarcodeFormat.QR_CODE, false));
            Throws<ArgumentException>(() => ImageService.Barcode(new string('x', 2001), BarcodeFormat.QR_CODE, false));
            Equal<string?>(null, ImageService.DecodeBarcode(MakeBitmap(200, 200)));
        });
        await Check("Crop preserves selected pixels and validates bounds", () =>
        {
            var original = MakeBitmap(8, 5, (x, y) => ((byte)(x * 20), (byte)(y * 30), (byte)0, (byte)255));
            var crop = ImageService.Crop(original, new Int32Rect(2, 1, 4, 3));
            Equal(4, crop.PixelWidth); Equal(3, crop.PixelHeight);
            var p = Pixels(crop); Equal((byte)40, p[0]); Equal((byte)30, p[1]);
            Throws<ArgumentException>(() => ImageService.Crop(original, new Int32Rect(6, 1, 3, 2)));
            Throws<ArgumentException>(() => ImageService.Crop(original, new Int32Rect(0, 0, 0, 0)));
            Equal(8, original.PixelWidth); Equal(5, original.PixelHeight);
        });
        await Check("Rotation swaps nonsquare dimensions and round trips pixels", () =>
        {
            var source = MakeBitmap(8, 5, (x, y) => ((byte)(x * 20), (byte)(y * 30), (byte)0, (byte)255));
            var rotated = ImageService.Rotate(source, 90);
            Equal(5, rotated.PixelWidth); Equal(8, rotated.PixelHeight);
            var restored = ImageService.Rotate(rotated, -90);
            Require(Pixels(source).SequenceEqual(Pixels(restored)), "Opposite rotations must restore pixel data.");
        });
        await Check("Label render uses physical dimensions, white margin, and binary ink", () =>
        {
            var settings = new PrintSettings { WidthMm = 101.6, HeightMm = 152.4, Dpi = 203, MarginMm = 2, Monochrome = true };
            var rendered = ImageService.RenderLabel(MakeBitmap(400, 600, (_, _) => ((byte)20, (byte)20, (byte)20, (byte)255)), settings);
            Equal(812, rendered.PixelWidth); Equal(1218, rendered.PixelHeight);
            var pixels = Pixels(rendered);
            Equal((byte)255, pixels[0]);
            int middle = (rendered.PixelHeight / 2 * rendered.PixelWidth + rendered.PixelWidth / 2) * 4;
            Equal((byte)0, pixels[middle]);
            for (int i = 0; i < pixels.Length; i += 4)
            {
                Require(pixels[i] is 0 or 255 && pixels[i] == pixels[i + 1] && pixels[i] == pixels[i + 2] && pixels[i + 3] == 255, "Monochrome label contains nonbinary or transparent pixels.");
            }
            ImageService.SavePng(rendered, Path.Combine(Artifacts, "rendered-label.png"));
        });
        await Check("300-DPI color label preserves intermediate tones", () =>
        {
            var settings = new PrintSettings { WidthMm = 50.8, HeightMm = 76.2, Dpi = 300, MarginMm = 0, Monochrome = false };
            var rendered = ImageService.RenderLabel(MakeBitmap(200, 300, (_, _) => ((byte)40, (byte)100, (byte)180, (byte)255)), settings);
            Equal(600, rendered.PixelWidth); Equal(900, rendered.PixelHeight);
            Require(Math.Abs(rendered.DpiX - 300) < 0.01 && Math.Abs(rendered.DpiY - 300) < 0.01, "Color render must retain the selected DPI metadata.");
            var p = Pixels(rendered); int center = (450 * 600 + 300) * 4;
            Equal((byte)40, p[center]); Equal((byte)100, p[center + 1]); Equal((byte)180, p[center + 2]);
        });
        await Check("Settings save and reload all nondefault values", () =>
        {
            var settings = new PrintSettings { WidthMm = 60, HeightMm = 40, Dpi = 300, GapMm = 2.5, Density = 12, Speed = 6, Copies = 3, OffsetXMm = 1.2, OffsetYMm = 2.3, MarginMm = 1, Threshold = 140, Monochrome = false, DarkMode = true, PrinterName = "Offline fixture printer" };
            SettingsStore.Save(settings);
            var loaded = SettingsStore.Load();
            Equal(JsonSerializer.Serialize(settings), JsonSerializer.Serialize(loaded));
            Require(!Directory.EnumerateFiles(SettingsStore.DirectoryPath, "settings.json*.tmp").Any(), "Committed save left a temporary file.");
        });
        await CheckAsync("Concurrent settings saves use independent temporary files and commit complete JSON", async () =>
        {
            await Task.WhenAll(Enumerable.Range(1, 16).Select(copies => Task.Run(() =>
            {
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    SettingsStore.Save(new PrintSettings { Copies = copies, PrinterName = $"Fixture queue {copies}" });
                    var snapshot = SettingsStore.Load();
                    Equal($"Fixture queue {snapshot.Copies}", snapshot.PrinterName);
                }
            })));
            var loaded = SettingsStore.Load();
            Require(loaded.Copies is >= 1 and <= 16, "Concurrent save lost the settings values.");
            Equal($"Fixture queue {loaded.Copies}", loaded.PrinterName);
            Require(!Directory.EnumerateFiles(SettingsStore.DirectoryPath, "settings.json*.tmp").Any(), "Concurrent save left a temporary file.");
        });
        await Check("Corrupt and invalid settings recover to defaults", () =>
        {
            File.WriteAllText(Path.Combine(SettingsStore.DirectoryPath, "settings.json"), "{ incomplete");
            Equal(101.6, SettingsStore.Load().WidthMm);
            File.WriteAllText(Path.Combine(SettingsStore.DirectoryPath, "settings.json"), "{\"WidthMm\": 0}");
            Equal(101.6, SettingsStore.Load().WidthMm);
        });
        await Check("Settings bounds reject unsafe raster and printer values", () =>
        {
            Action<PrintSettings>[] invalid = [s => s.WidthMm = 37.9, s => s.WidthMm = 110.1, s => s.WidthMm = double.NaN,
                s => s.HeightMm = 19.9, s => s.HeightMm = 501, s => s.HeightMm = double.PositiveInfinity,
                s => s.Dpi = 600, s => s.Density = 0, s => s.Density = 16, s => s.Speed = 0, s => s.Speed = 9,
                s => s.Copies = 0, s => s.Copies = 101, s => s.GapMm = -1, s => s.GapMm = 21,
                s => s.MarginMm = 51, s => s.MarginMm = double.NaN, s => s.OffsetXMm = -1,
                s => s.OffsetYMm = s.HeightMm, s => s.Threshold = 0, s => s.Threshold = 255];
            foreach (var mutation in invalid) { var settings = new PrintSettings(); mutation(settings); Throws<ArgumentException>(settings.Validate); }
            new PrintSettings { WidthMm = 38, HeightMm = 20, Density = 1, Speed = 1, Copies = 1, MarginMm = 0, GapMm = 0 }.Validate();
        });
        await CheckAsync("PNG import owns decoded pixels and releases source file", async () =>
        {
            var path = Path.Combine(Artifacts, "source-image.png");
            ImageService.SavePng(MakeBitmap(480, 320, (x, _) => ((byte)(x % 255), (byte)20, (byte)40, (byte)255)), path);
            var images = await ImageService.ImportAsync(path);
            Equal(1, images.Count);
            Equal(480, images[0].Image.PixelWidth); Equal(320, images[0].Image.PixelHeight);
            Require(images[0].Image.IsFrozen, "Imported image is not frozen.");
            Require(Math.Abs((double)images[0].Image.PixelWidth / images[0].Image.PixelHeight - 1.5) < 0.01, "Image import changed aspect ratio.");
            using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            var moved = Path.Combine(Artifacts, "released-source-image.png"); File.Move(path, moved, true);
            Require(Pixels(images[0].Image).Length > 0, "Decoded pixels unavailable after source move.");
        });
        await CheckAsync("Image import caps the dominant axis even for extremely narrow or short images", async () =>
        {
            foreach (var (width, height, expectedWidth, expectedHeight) in new[]
            {
                (1, 10000, 1, 2400), (10000, 1, 2400, 1),
                (200, 4000, 120, 2400), (4000, 200, 2400, 120)
            })
            {
                var path = Path.Combine(Artifacts, $"image-{width}x{height}.png");
                ImageService.SavePng(MakeBitmap(width, height), path);
                var imported = await ImageService.ImportAsync(path);
                Equal(1, imported.Count);
                Equal(expectedWidth, imported[0].Image.PixelWidth);
                Equal(expectedHeight, imported[0].Image.PixelHeight);
                Require(imported[0].Image.IsFrozen, "Downsampled image must own its decoded pixels.");
                using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            }
        });
        await CheckAsync("Two-page PDF imports at measured page sizes with visible contents", async () =>
        {
            var path = Path.Combine(Artifacts, "two-page.pdf");
            WritePdf(path);
            var pages = await ImageService.ImportAsync(path);
            Equal(2, pages.Count);
            Equal(600, pages[0].Image.PixelWidth); Equal(900, pages[0].Image.PixelHeight);
            Equal(1200, pages[1].Image.PixelWidth); Equal(1800, pages[1].Image.PixelHeight);
            Require(pages[0].Detail.Contains("Page 1 of 2") && pages[1].Detail.Contains("Page 2 of 2"), "PDF page order or labels are wrong.");
            foreach (var page in pages)
            {
                var p = Pixels(page.Image); int dark = 0, white = 0;
                for (int i = 0; i < p.Length; i += 4) { if (p[i] < 50 && p[i + 1] < 50 && p[i + 2] < 50) dark++; if (p[i] > 240 && p[i + 1] > 240 && p[i + 2] > 240) white++; }
                Require(dark > 1000 && white > 1000, "PDF page did not render the expected rectangle and whitespace.");
            }
            ImageService.SavePng(pages[0].Image, Path.Combine(Artifacts, "pdf-page-1.png"));
        });
        await CheckAsync("Invalid file and PDF content fail without substitute pages", async () =>
        {
            await ThrowsAsync<FileNotFoundException>(() => ImageService.ImportAsync(Path.Combine(Artifacts, "missing-file.png")));
            var badPdf = Path.Combine(Artifacts, "invalid.pdf"); File.WriteAllText(badPdf, "not a PDF");
            await ThrowsAsync<Exception>(() => ImageService.ImportAsync(badPdf));
        });
        await Check("TSPL physical header, row bits, alpha, padding and copies", () =>
        {
            var settings = new PrintSettings { WidthMm = 38.2, HeightMm = 20, GapMm = 2.5, Dpi = 203, Copies = 3, MarginMm = 0, Threshold = 160 };
            int width = (int)Math.Round(settings.WidthMm * settings.Dpi / 25.4), height = (int)Math.Round(settings.HeightMm * settings.Dpi / 25.4);
            Equal(305, width); Equal(160, height);
            var bitmap = MakeBitmap(width, height, (x, y) => y == 0 && (x == 0 || x == 7 || x == 8 || x == width - 1)
                ? ((byte)0, (byte)0, (byte)0, (byte)255)
                : y == 1 ? ((byte)0, (byte)0, (byte)0, (byte)0) : ((byte)255, (byte)255, (byte)255, (byte)255));
            var encoded = TsplEncoder.Encode(bitmap, settings);
            var header = "SIZE 38.2 mm,20 mm\r\nGAP 2.5 mm,0 mm\r\nDENSITY 8\r\nSPEED 4\r\nDIRECTION 0,0\r\nREFERENCE 0,0\r\nOFFSET 0 mm\r\nCLS\r\nBITMAP 0,0,39,160,0,";
            var headerBytes = Encoding.ASCII.GetBytes(header);
            Require(encoded.AsSpan(0, headerBytes.Length).SequenceEqual(headerBytes), "TSPL header differs from documented physical settings.");
            int start = headerBytes.Length; Equal((byte)0x7e, encoded[start]); Equal((byte)0x7f, encoded[start + 1]);
            Equal((byte)0x7f, encoded[start + 38]);
            for (int i = 39; i < 78; i++) Equal((byte)0xff, encoded[start + i]);
            var footer = Encoding.ASCII.GetBytes("\r\nPRINT 1,3\r\n");
            Equal(start + 39 * 160 + footer.Length, encoded.Length);
            Require(encoded.AsSpan(encoded.Length - footer.Length).SequenceEqual(footer), "TSPL copies footer mismatch.");
            File.WriteAllBytes(Path.Combine(Artifacts, "raster-fixture.tspl"), encoded);
        });
        await Check("TSPL offsets use printer dots and prevent clipped label ink", () =>
        {
            var settings = new PrintSettings { WidthMm = 38.2, HeightMm = 20, OffsetXMm = 1, OffsetYMm = 2, MarginMm = 0 };
            var withMargin = MakeBitmap(305, 160, (x, y) => x >= 20 && x < 200 && y >= 20 && y < 100
                ? ((byte)0, (byte)0, (byte)0, (byte)255) : ((byte)255, (byte)255, (byte)255, (byte)255));
            var job = Encoding.ASCII.GetString(TsplEncoder.Encode(withMargin, settings));
            Require(job.Contains("BITMAP 8,16,39,160,0,", StringComparison.Ordinal), "Millimeter offsets were not converted to printer dots.");
            Throws<ArgumentException>(() => TsplEncoder.Encode(MakeBitmap(305, 160, (_, _) => ((byte)0, (byte)0, (byte)0, (byte)255)), settings));
        });
        await Check("TSPL is locale independent and refuses mismatched raster scale", () =>
        {
            var settings = new PrintSettings { WidthMm = 38.2, HeightMm = 20, GapMm = 2.5, MarginMm = 0 };
            var bitmap = MakeBitmap(305, 160); var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                var text = Encoding.ASCII.GetString(TsplEncoder.Encode(bitmap, settings));
                Require(text.StartsWith("SIZE 38.2 mm,20 mm\r\nGAP 2.5 mm,0 mm", StringComparison.Ordinal), "Decimal formatting followed current locale.");
            }
            finally { CultureInfo.CurrentCulture = previous; }
            Throws<ArgumentException>(() => TsplEncoder.Encode(MakeBitmap(20, 20), settings));
        });
        await Check("Shiprinter compressed frame inflates to black-one pixels with white padding and transparency", () =>
        {
            var settings = new PrintSettings { WidthMm = 38.2, HeightMm = 20, GapMm = 2.5, Dpi = 203, Copies = 3, MarginMm = 0, Threshold = 160 };
            var bitmap = MakeBitmap(305, 160, (x, y) =>
            {
                if (y == 0 && x is 0 or 7 or 8 or 304) return ((byte)0, (byte)0, (byte)0, (byte)255);
                if (y == 1) return ((byte)0, (byte)0, (byte)0, (byte)0);
                if (y == 2 && x == 0) return ((byte)0, (byte)0, (byte)0, (byte)190);
                if (y == 2 && x == 1) return ((byte)0, (byte)0, (byte)0, (byte)64);
                if (y == 2 && x == 2) return ((byte)160, (byte)160, (byte)160, (byte)255);
                if (y == 2 && x == 3) return ((byte)159, (byte)159, (byte)159, (byte)255);
                return ((byte)255, (byte)255, (byte)255, (byte)255);
            });
            byte[] job = ShiprinterEncoder.Encode(bitmap, settings);
            var decoded = DecodeShiprinter(job);
            Equal(39, decoded.RowBytes); Equal(160, decoded.Height); Equal(39 * 160, decoded.Pixels.Length);
            Equal((byte)0x81, decoded.Pixels[0]); Equal((byte)0x80, decoded.Pixels[1]);
            Equal((byte)0x80, decoded.Pixels[38]);
            for (int i = 2; i < 38; i++) Equal((byte)0, decoded.Pixels[i]);
            for (int i = 39; i < 78; i++) Equal((byte)0, decoded.Pixels[i]);
            Equal((byte)0x90, decoded.Pixels[78]);
            for (int i = 79; i < decoded.Pixels.Length; i++) Equal((byte)0, decoded.Pixels[i]);
            Equal("\r\nPRINT 1,3\r\n", decoded.Footer);
            File.WriteAllBytes(Path.Combine(Artifacts, "offnova-compressed-fixture.tspl"), job);
        });
        await Check("OFFNOVA encoding is locale independent and rejects 300 DPI and mismatched scale", () =>
        {
            var settings = new PrintSettings { WidthMm = 38.2, HeightMm = 20, GapMm = 2.5, MarginMm = 0 };
            var bitmap = MakeBitmap(305, 160);
            var baseline = ShiprinterEncoder.Encode(bitmap, settings);
            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                Require(baseline.SequenceEqual(ShiprinterEncoder.Encode(bitmap, settings)), "Locale changed compressed job bytes or numeric command formatting.");
            }
            finally { CultureInfo.CurrentCulture = previous; }
            Throws<ArgumentException>(() => ShiprinterEncoder.Encode(MakeBitmap(20, 20), settings));
            settings.Dpi = 300;
            Throws<ArgumentException>(() => ShiprinterEncoder.Encode(bitmap, settings));
        });
        await Check("Settings preserve BLE and Classic selection identities and derived transport flags", () =>
        {
            var serviceUuid = Guid.Parse("f379b8a8-960d-437f-b1bd-b7bced8bcb15");
            var characteristicUuid = Guid.Parse("097ca624-eb8d-4e7d-980d-de889ffb5223");
            foreach (string mode in new[] { "offnova-ble", "ble", "offnova-classic" })
            {
                var selection = new PrinterSelection
                {
                    Mode = mode, PrinterName = "Offline fixture queue", DeviceName = "OFFNOVA fixture only",
                    DeviceId = mode == "offnova-classic" ? "Bluetooth#OfflineFixture" : "ble:random:AABBCCDDEEFF",
                    ServiceUuid = mode == "offnova-classic" ? Guid.Empty : serviceUuid,
                    CharacteristicUuid = mode == "offnova-classic" ? Guid.Empty : characteristicUuid
                };
                SettingsStore.Save(new PrintSettings { Printer = selection });
                var loaded = SettingsStore.Load().Printer;
                Equal(JsonSerializer.Serialize(selection), JsonSerializer.Serialize(loaded));
                Equal(mode != "offnova-classic", loaded.IsBluetooth);
                Equal(mode == "offnova-classic", loaded.IsClassic);
                Equal(mode != "ble", loaded.IsOffnova);
                Require(loaded.IsWireless, "Reloaded wireless transport was lost.");
                string json = File.ReadAllText(Path.Combine(SettingsStore.DirectoryPath, "settings.json"));
                Require(!json.Contains("IsBluetooth", StringComparison.Ordinal) && !json.Contains("IsWireless", StringComparison.Ordinal), "Derived transport flags should not be persisted independently.");
            }
        });
        await Check("BLE payload sizing respects minimum ATT MTU, negotiated limit, and 512-byte ceiling", () =>
        {
            var shiprinter = new BleTransferOptions(512, 0, true);
            Equal(20, BluetoothPrinterService.GetChunkSize(shiprinter, 23));
            Equal(182, BluetoothPrinterService.GetChunkSize(shiprinter, 185));
            Equal(244, BluetoothPrinterService.GetChunkSize(shiprinter, 247));
            Equal(512, BluetoothPrinterService.GetChunkSize(shiprinter, 517));
            Equal(512, BluetoothPrinterService.GetChunkSize(shiprinter, ushort.MaxValue));
            Equal(20, BluetoothPrinterService.GetChunkSize(shiprinter, 0));
            Equal(20, BluetoothPrinterService.GetChunkSize(new BleTransferOptions(), 517));
            Equal(1, BluetoothPrinterService.GetChunkSize(new BleTransferOptions(1), 23));
            Throws<ArgumentOutOfRangeException>(() => BluetoothPrinterService.GetChunkSize(new BleTransferOptions(0), 23));
            Throws<ArgumentOutOfRangeException>(() => BluetoothPrinterService.GetChunkSize(new BleTransferOptions(513), 517));
            Throws<ArgumentOutOfRangeException>(() => new BleTransferOptions(20, -1).Validate());
            Throws<ArgumentOutOfRangeException>(() => new BleTransferOptions(20, 1001).Validate());
        });
        await Check("BLE identities preserve public versus random addresses and reject malformed input", () =>
        {
            const ulong address = 0xaabbccddeeff;
            foreach (var type in new[] { BluetoothAddressType.Public, BluetoothAddressType.Random })
            {
                string id = BluetoothPrinterService.FormatDeviceId(address, type);
                Require(BluetoothPrinterService.TryParseDeviceId(id, out var parsed, out var parsedType), "Valid BLE identity did not parse.");
                Equal(address, parsed); Equal(type, parsedType);
            }
            Require(BluetoothPrinterService.FormatDeviceId(address, BluetoothAddressType.Public) != BluetoothPrinterService.FormatDeviceId(address, BluetoothAddressType.Random), "Address type must be part of the identity.");
            foreach (string invalid in new[] { "", "ble:public:000000000000", "ble:public:AABBCC", "ble:public:AABBCCDDEEFF0", "ble:random:XXBBCCDDEEFF", "ble:unknown:AABBCCDDEEFF", "ble:public:AABBCCDDEEFF:extra", "Bluetooth#LegacyEndpoint" })
                Require(!BluetoothPrinterService.TryParseDeviceId(invalid, out _, out _), $"Malformed advertisement identity accepted: {invalid}");
            Throws<ArgumentOutOfRangeException>(() => BluetoothPrinterService.FormatDeviceId(0, BluetoothAddressType.Public));
            Throws<ArgumentOutOfRangeException>(() => BluetoothPrinterService.FormatDeviceId(0x1_0000_0000_0000, BluetoothAddressType.Public));
        });
        await Check("OFFNOVA channel choice rejects ambiguous, read-only, and unacknowledged-only services", () =>
        {
            var service = Guid.Parse("f379b8a8-960d-437f-b1bd-b7bced8bcb15");
            var writeUuid = Guid.Parse("097ca624-eb8d-4e7d-980d-de889ffb5223");
            var otherUuid = Guid.Parse("505e367e-9cda-461c-ae22-fd1d3689b749");
            var write = new BleWriteTarget(service, writeUuid, "Fixture write", "Write, WriteWithoutResponse");
            var otherWrite = new BleWriteTarget(service, otherUuid, "Other fixture write", "Write");
            var unacknowledged = new BleWriteTarget(service, otherUuid, "Unacknowledged fixture", "WriteWithoutResponse");
            var readOnly = new BleWriteTarget(service, otherUuid, "Read fixture", "Read, Notify");
            Equal<BleWriteTarget?>(null, OffnovaPrinterProfile.SelectTarget([]));
            Equal<BleWriteTarget?>(null, OffnovaPrinterProfile.SelectTarget([readOnly, unacknowledged]));
            Equal<BleWriteTarget?>(write, OffnovaPrinterProfile.SelectTarget([readOnly, unacknowledged, write]));
            Equal<BleWriteTarget?>(null, OffnovaPrinterProfile.SelectTarget([write, otherWrite]));
            var vendor = new BleWriteTarget(OffnovaPrinterProfile.VendorPrintService,
                OffnovaPrinterProfile.VendorPrintCharacteristic, "Documented print channel fixture", "Write, WriteWithoutResponse");
            Equal<BleWriteTarget?>(vendor, OffnovaPrinterProfile.SelectTarget([write, vendor, otherWrite]));
            Equal<BleWriteTarget?>(null, OffnovaPrinterProfile.SelectTarget([write, otherWrite, vendor with { Properties = "WriteWithoutResponse" }]));
            Equal<BleWriteTarget?>(null, OffnovaPrinterProfile.SelectTarget([write, vendor with { ServiceUuid = service }]));
            Equal<BleWriteTarget?>(write, OffnovaPrinterProfile.SelectTarget([write, write with { DisplayName = "Duplicate discovery" }]));
            var inspection = new BleDeviceInspection(DateTimeOffset.UnixEpoch, "fixture", "Fixture", "Public", "Disconnected", "Success", null,
                [new(service, "Success", null, [new(writeUuid, "Write", true, false), new(otherUuid, "Read", false, false)]),
                 new(Guid.Empty, "AccessDenied", null, [new(otherUuid, "Write", true, false)])]);
            var targets = BluetoothPrinterService.GetWriteTargets(inspection);
            Equal(1, targets.Count); Equal(writeUuid, targets[0].CharacteristicUuid);
            Require(OffnovaPrinterProfile.IsCandidateName("OFFNOVA N-6140_TEST") && OffnovaPrinterProfile.IsCandidateName("N_6140_LE"), "Documented discovery name variants were missed.");
            Require(!OffnovaPrinterProfile.IsCandidateName("Unrelated speaker"), "Non-printer name matched the OFFNOVA discovery hint.");
        });
    }

    // Independent consumer of the framed zlib payload. This never calls a printer or encoder helper.
    private static (int RowBytes, int Height, byte[] Pixels, string Footer) DecodeShiprinter(byte[] job)
    {
        Require(job.Length > 12 && job.AsSpan(0, 12).SequenceEqual(new byte[12]), "Missing twelve-byte wakeup prefix.");
        Require(job[12] != 0, "Wakeup prefix contains extra NUL bytes.");
        int marker = job.AsSpan(12).IndexOf(Encoding.ASCII.GetBytes("BITMAP "));
        Require(marker >= 0, "No BITMAP command in compressed job.");
        marker += 12;
        string setup = Encoding.ASCII.GetString(job, 12, marker - 12);
        Require(setup.Contains("SET CUTTER 1\r\n", StringComparison.Ordinal), "Recovered cutter setup is missing.");
        int offset = marker + "BITMAP ".Length;
        var fields = new int[6];
        for (int i = 0; i < fields.Length; i++)
        {
            int comma = Array.IndexOf(job, (byte)',', offset);
            Require(comma > offset, "Truncated BITMAP metadata.");
            fields[i] = int.Parse(Encoding.ASCII.GetString(job, offset, comma - offset), CultureInfo.InvariantCulture);
            offset = comma + 1;
        }
        Equal(3, fields[4]);
        int length = fields[5];
        Require(length > 0 && length <= job.Length - offset, "Compressed payload length is outside the frame.");
        using var compressed = new MemoryStream(job, offset, length, writable: false);
        using var inflater = new ZLibStream(compressed, CompressionMode.Decompress);
        using var pixels = new MemoryStream();
        inflater.CopyTo(pixels);
        Equal(checked(fields[2] * fields[3]), (int)pixels.Length);
        string footer = Encoding.ASCII.GetString(job, offset + length, job.Length - offset - length);
        Require(footer.StartsWith("\r\nPRINT 1,", StringComparison.Ordinal) && footer.EndsWith("\r\n", StringComparison.Ordinal), "Compressed-length field does not end at the print command.");
        return (fields[2], fields[3], pixels.ToArray(), footer);
    }

    private static async Task CheckAsync(string name, Func<Task> check)
    {
        var timer = Stopwatch.StartNew();
        try { await check(); Results.Add(new(name, true, timer.ElapsedMilliseconds, null)); Console.WriteLine($"PASS {name}"); }
        catch (Exception ex) { Results.Add(new(name, false, timer.ElapsedMilliseconds, ex.ToString())); Console.WriteLine($"FAIL {name}: {ex.Message}"); }
    }
    private static Task Check(string name, Action check) => CheckAsync(name, () => { check(); return Task.CompletedTask; });
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Require(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; got {actual}.");
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
    private static byte[] Pixels(BitmapSource source)
    {
        var bitmap = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(bytes, bitmap.PixelWidth * 4, 0); return bytes;
    }
    private static BitmapSource MakeBitmap(int width, int height, Func<int, int, (byte B, byte G, byte R, byte A)>? pixel = null)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            var p = pixel?.Invoke(x, y) ?? (B: (byte)255, G: (byte)255, R: (byte)255, A: (byte)255);
            int i = (y * width + x) * 4; pixels[i] = p.B; pixels[i + 1] = p.G; pixels[i + 2] = p.R; pixels[i + 3] = p.A;
        }
        var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4); result.Freeze(); return result;
    }
    private static void WritePdf(string path)
    {
        const string contents = "0 0 0 rg\n12 12 60 100 re f\n";
        string[] objects = ["<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 144 216] /Resources << >> /Contents 4 0 R >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(contents)} >>\nstream\n{contents}endstream",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 288 432] /Resources << >> /Contents 6 0 R >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(contents)} >>\nstream\n{contents}endstream"];
        using var stream = File.Create(path);
        void Write(string text) => stream.Write(Encoding.ASCII.GetBytes(text));
        Write("%PDF-1.4\n"); var offsets = new List<long>();
        for (int i = 0; i < objects.Length; i++) { offsets.Add(stream.Position); Write($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        long xref = stream.Position; Write($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (long offset in offsets) Write(offset.ToString("0000000000", CultureInfo.InvariantCulture) + " 00000 n \n");
        Write($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    }
    private sealed record CheckResult(string Name, bool Passed, long DurationMs, string? Error);
}
