using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ShippingPrinter.Windows.Models;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;
using ZXing;

namespace ShippingPrinter.Windows.Services;

public static class ImageService
{
    public static async Task<List<LabelItem>> ImportAsync(string path, CancellationToken token = default)
    {
        var result = new List<LabelItem>();
        if (new FileInfo(path).Length > 100 * 1024 * 1024) throw new ArgumentException("Files must be smaller than 100 MB.");
        if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)).AsTask(token);
            var pdf = await PdfDocument.LoadFromFileAsync(file).AsTask(token);
            if (pdf.PageCount > 100) throw new ArgumentException("Import PDFs with at most 100 pages. Split larger files first.");
            long totalPixels = 0;
            for (uint i = 0; i < pdf.PageCount; i++)
            {
                token.ThrowIfCancellationRequested();
                using var page = pdf.GetPage(i);
                using var stream = new InMemoryRandomAccessStream();
                var scale = Math.Min(300d / 96, Math.Sqrt(4_000_000d / (page.Size.Width * page.Size.Height)));
                totalPixels += (long)(page.Size.Width * scale) * (long)(page.Size.Height * scale);
                if (totalPixels > 48_000_000) throw new ArgumentException("This PDF is too large for one workspace. Split it into smaller files before importing.");
                await page.RenderToStreamAsync(stream, new PdfPageRenderOptions { DestinationWidth = (uint)Math.Max(1, page.Size.Width * scale), DestinationHeight = (uint)Math.Max(1, page.Size.Height * scale) }).AsTask(token);
                stream.Seek(0);
                using var read = stream.AsStreamForRead();
                var image = Decode(read);
                result.Add(new() { Name = $"{Path.GetFileNameWithoutExtension(path)} · {i + 1}", Detail = $"PDF · Page {i + 1} of {pdf.PageCount}", Original = image, Image = image });
            }
        }
        else
        {
            using var stream = File.OpenRead(path);
            // Read metadata on demand, then downsample both dimensions before materializing pixels.
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            stream.Position = 0;
            // Limit the longest axis directly. Rounding a very narrow image's width
            // up to one pixel can otherwise leave its height almost unbounded.
            bool landscape = frame.PixelWidth >= frame.PixelHeight;
            var image = Decode(stream,
                width: landscape && frame.PixelWidth > 2400 ? 2400 : 0,
                height: !landscape && frame.PixelHeight > 2400 ? 2400 : 0);
            result.Add(new() { Name = Path.GetFileName(path), Detail = $"Image · {image.PixelWidth:N0} × {image.PixelHeight:N0} px", Original = image, Image = image });
        }
        return result;
    }
    public static BitmapSource Decode(Stream stream, int width = 0, int height = 0)
    {
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        if (width > 0) image.DecodePixelWidth = width;
        if (height > 0) image.DecodePixelHeight = height;
        image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
    }
    public static BitmapSource Rotate(BitmapSource source, int degrees)
    {
        var result = new TransformedBitmap(source, new RotateTransform(degrees)); result.Freeze(); return result;
    }
    public static BitmapSource Crop(BitmapSource source, Int32Rect rect)
    {
        if (rect.Width < 1 || rect.Height < 1 || rect.X < 0 || rect.Y < 0 || rect.X + rect.Width > source.PixelWidth || rect.Y + rect.Height > source.PixelHeight) throw new ArgumentException("Select a crop inside the image.");
        var image = new CroppedBitmap(source, rect); image.Freeze(); return image;
    }
    public static BitmapSource RenderLabel(BitmapSource source, PrintSettings settings)
    {
        settings.Validate();
        var width = (int)Math.Round(settings.WidthMm * settings.Dpi / 25.4);
        var height = (int)Math.Round(settings.HeightMm * settings.Dpi / 25.4);
        var margin = settings.MarginMm * settings.Dpi / 25.4;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            var ratio = Math.Min((width - 2 * margin) / source.PixelWidth, (height - 2 * margin) / source.PixelHeight);
            var w = source.PixelWidth * ratio; var h = source.PixelHeight * ratio;
            dc.DrawImage(source, new Rect((width - w) / 2, (height - h) / 2, w, h));
        }
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        var output = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); output.Render(visual); output.Freeze();
        var pixels = new byte[width * height * 4]; output.CopyPixels(pixels, width * 4, 0);
        for (var p = 0; settings.Monochrome && p < pixels.Length; p += 4)
        {
            byte v = (pixels[p] * 114 + pixels[p + 1] * 587 + pixels[p + 2] * 299) / 1000 < settings.Threshold ? (byte)0 : (byte)255;
            pixels[p] = pixels[p + 1] = pixels[p + 2] = v; pixels[p + 3] = 255;
        }
        var mono = BitmapSource.Create(width, height, settings.Dpi, settings.Dpi, PixelFormats.Bgra32, null, pixels, width * 4); mono.Freeze(); return mono;
    }
    public static BitmapSource Barcode(string text, BarcodeFormat format, bool caption)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Enter the content to encode.");
        if (text.Length > 2000) throw new ArgumentException("Use at most 2,000 characters.");
        var square = format is BarcodeFormat.QR_CODE or BarcodeFormat.DATA_MATRIX or BarcodeFormat.AZTEC;
        var matrix = new MultiFormatWriter().encode(text, format, square ? 800 : 1200, square ? 800 : 360, new Dictionary<EncodeHintType, object> { [EncodeHintType.MARGIN] = 20, [EncodeHintType.CHARACTER_SET] = "UTF-8" });
        var height = matrix.Height + (caption ? 76 : 0);
        var pixels = new byte[matrix.Width * matrix.Height * 4];
        for (var y = 0; y < matrix.Height; y++) for (var x = 0; x < matrix.Width; x++)
        { var p = (y * matrix.Width + x) * 4; var v = matrix[x, y] ? (byte)0 : (byte)255; pixels[p] = pixels[p + 1] = pixels[p + 2] = v; pixels[p + 3] = 255; }
        var bitmap = BitmapSource.Create(matrix.Width, matrix.Height, 96, 96, PixelFormats.Bgra32, null, pixels, matrix.Width * 4);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, matrix.Width, height)); dc.DrawImage(bitmap, new Rect(0, 0, matrix.Width, matrix.Height));
            if (caption)
            {
                var label = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Consolas"), 32, Brushes.Black, 1) { MaxTextWidth = matrix.Width - 40, MaxTextHeight = 60, Trimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center };
                dc.DrawText(label, new Point(20, matrix.Height + 8));
            }
        }
        var result = new RenderTargetBitmap(matrix.Width, height, 96, 96, PixelFormats.Pbgra32); result.Render(visual); result.Freeze(); return result;
    }
    public static string? DecodeBarcode(BitmapSource bitmap)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var bytes = new byte[converted.PixelWidth * converted.PixelHeight * 4]; converted.CopyPixels(bytes, converted.PixelWidth * 4, 0);
        var source = new RGBLuminanceSource(bytes, converted.PixelWidth, converted.PixelHeight, RGBLuminanceSource.BitmapFormat.BGRA32);
        var reader = new BarcodeReaderGeneric { AutoRotate = true, Options = new ZXing.Common.DecodingOptions { TryHarder = true } };
        return reader.Decode(source)?.Text;
    }
    public static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
