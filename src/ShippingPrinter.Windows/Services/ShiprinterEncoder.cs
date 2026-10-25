using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows.Media.Imaging;
using ShippingPrinter.Windows.Models;

namespace ShippingPrinter.Windows.Services;

/// <summary>
/// Encodes OFFNOVA's BY426BT compressed image format using zlib and black-one raster bits.
/// No firmware acceptance or physical output is implied by successful encoding.
/// </summary>
public static class ShiprinterEncoder
{
    public static byte[] Encode(BitmapSource bitmap, PrintSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        OffnovaPrinterProfile.Validate(settings);
        var raster = TsplEncoder.CreateRaster(bitmap, settings, blackIsOne: true);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(raster);
        byte[] payload = compressed.ToArray();
        static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        int x = (int)Math.Round(settings.OffsetXMm * settings.Dpi / 25.4);
        int y = (int)Math.Round(settings.OffsetYMm * settings.Dpi / 25.4);
        using var job = new MemoryStream();
        // BY426BT.wakeup emits twelve NUL bytes, not an undocumented status query.
        job.Write(new byte[12]);
        string header = $"SIZE {N(settings.WidthMm)} mm,{N(settings.HeightMm)} mm\r\n" +
                        "DIRECTION 0,0\r\nSET CUTTER 1\r\n" +
                        $"GAP {N(settings.GapMm)} mm,0 mm\r\nSPEED {settings.Speed}\r\nDENSITY {settings.Density}\r\nCLS\r\n" +
                        $"BITMAP {x},{y},{(bitmap.PixelWidth + 7) / 8},{bitmap.PixelHeight},3,{payload.Length},";
        job.Write(Encoding.ASCII.GetBytes(header));
        job.Write(payload);
        job.Write(Encoding.ASCII.GetBytes($"\r\nPRINT 1,{settings.Copies}\r\n"));
        return job.ToArray();
    }
}
