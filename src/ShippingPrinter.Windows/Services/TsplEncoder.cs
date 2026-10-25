using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ShippingPrinter.Windows.Models;

namespace ShippingPrinter.Windows.Services;

/// <summary>Builds one uncompressed TSPL label job. Does not contact a printer.</summary>
public static class TsplEncoder
{
    public static byte[] Encode(BitmapSource bitmap, PrintSettings settings)
    {
        var raster = CreateRaster(bitmap, settings, blackIsOne: false);
        static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        int xOffset = (int)Math.Round(settings.OffsetXMm * settings.Dpi / 25.4);
        int yOffset = (int)Math.Round(settings.OffsetYMm * settings.Dpi / 25.4);
        var header = $"SIZE {N(settings.WidthMm)} mm,{N(settings.HeightMm)} mm\r\n" +
                     $"GAP {N(settings.GapMm)} mm,0 mm\r\n" +
                     $"DENSITY {settings.Density}\r\nSPEED {settings.Speed}\r\n" +
                     $"DIRECTION 0,0\r\nREFERENCE 0,0\r\nOFFSET 0 mm\r\nCLS\r\n" +
                     $"BITMAP {xOffset},{yOffset},{(bitmap.PixelWidth + 7) / 8},{bitmap.PixelHeight},0,";
        using var output = new MemoryStream();
        output.Write(Encoding.ASCII.GetBytes(header));
        output.Write(raster);
        output.Write(Encoding.ASCII.GetBytes($"\r\nPRINT 1,{settings.Copies}\r\n"));
        return output.ToArray();
    }

    internal static byte[] CreateRaster(BitmapSource bitmap, PrintSettings settings, bool blackIsOne)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        int width = bitmap.PixelWidth, height = bitmap.PixelHeight;
        if (width <= 0 || height <= 0 || (long)width * height > 32_000_000)
            throw new ArgumentException("The label image is too large to encode.");

        // The caller supplies the final paper-sized image, including its margin.
        // Allow tiny rounding differences, but never silently print a bitmap at a different scale.
        int targetWidth = (int)Math.Round(settings.WidthMm * settings.Dpi / 25.4);
        int targetHeight = (int)Math.Round(settings.HeightMm * settings.Dpi / 25.4);
        if (Math.Abs(width - targetWidth) > 1 || Math.Abs(height - targetHeight) > 1)
            throw new ArgumentException("Render the label at the selected paper size and DPI before encoding it.");

        BitmapSource source = bitmap.Format == PixelFormats.Bgra32
            ? bitmap
            : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        int stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        source.CopyPixels(pixels, stride, 0);
        EnsurePlacement(pixels, width, height, settings);
        int rowBytes = (width + 7) / 8;
        var raster = new byte[checked(rowBytes * height)];
        // Generic TSPL mode 0 and Shiprinter's compressed mode 3 use opposite polarity.
        // Each format's right-edge padding must remain white.
        if (!blackIsOne) Array.Fill(raster, (byte)0xff);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = y * stride + x * 4;
                int luminance = (pixels[i + 2] * 299 + pixels[i + 1] * 587 + pixels[i] * 114 + 500) / 1000;
                int alpha = pixels[i + 3];
                luminance = (luminance * alpha + 255 * (255 - alpha) + 127) / 255;
                if (luminance < settings.Threshold)
                {
                    byte mask = (byte)(0x80 >> (x % 8));
                    if (blackIsOne) raster[y * rowBytes + x / 8] |= mask;
                    else raster[y * rowBytes + x / 8] &= (byte)~mask;
                }
            }
        }

        return raster;
    }

    internal static void EnsurePlacement(BitmapSource bitmap, PrintSettings settings)
    {
        int width = bitmap.PixelWidth, height = bitmap.PixelHeight;
        if ((long)width * height > 32_000_000) throw new ArgumentException("The label image is too large to print.");
        BitmapSource source = bitmap.Format == PixelFormats.Bgra32
            ? bitmap : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[checked(width * height * 4)];
        source.CopyPixels(pixels, width * 4, 0);
        EnsurePlacement(pixels, width, height, settings);
    }

    private static void EnsurePlacement(byte[] pixels, int width, int height, PrintSettings settings)
    {
        int xOffset = (int)Math.Round(settings.OffsetXMm / settings.WidthMm * width);
        int yOffset = (int)Math.Round(settings.OffsetYMm / settings.HeightMm * height);
        if (xOffset == 0 && yOffset == 0) return;
        int clippedX = Math.Max(0, width - xOffset), clippedY = Math.Max(0, height - yOffset);
        for (int y = 0; y < height; y++)
        {
            int startX = y >= clippedY ? 0 : clippedX;
            for (int x = startX; x < width; x++)
            {
                int i = (y * width + x) * 4;
                int alpha = pixels[i + 3];
                int darkest = Math.Min(pixels[i], Math.Min(pixels[i + 1], pixels[i + 2]));
                int blended = (darkest * alpha + 255 * (255 - alpha) + 127) / 255;
                if (blended < 250)
                    throw new ArgumentException("The print offset would cut off label content. Reduce the X/Y offset or increase the label margin.");
            }
        }
    }
}
