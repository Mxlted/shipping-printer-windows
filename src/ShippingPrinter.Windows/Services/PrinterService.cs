using System.ComponentModel;
using System.IO;
using System.Printing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ShippingPrinter.Windows.Models;

namespace ShippingPrinter.Windows.Services;

public static class PrinterService
{
    public static IReadOnlyList<string> GetPrinters()
    {
        using var server = new LocalPrintServer();
        using var queues = server.GetPrintQueues([EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections]);
        var names = new List<string>();
        foreach (var queue in queues)
        {
            using (queue) names.Add(queue.FullName);
        }
        return names.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Shows the Windows print dialog. True means submitted to spooler, not physically printed.</summary>
    public static bool PrintWithDriver(BitmapSource bitmap, PrintSettings settings, string? printerName = null)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        settings.Validate();
        TsplEncoder.EnsurePlacement(bitmap, settings);
        double width = settings.WidthMm * 96 / 25.4;
        double height = settings.HeightMm * 96 / 25.4;
        var dialog = new PrintDialog();
        using var server = new LocalPrintServer();
        if (!string.IsNullOrWhiteSpace(printerName))
            dialog.PrintQueue = new PrintQueue(server, printerName);
        dialog.PrintTicket = dialog.PrintQueue?.UserPrintTicket?.Clone() ?? new PrintTicket();
        dialog.PrintTicket.PageMediaSize = new PageMediaSize(width, height);
        dialog.PrintTicket.PageOrientation = PageOrientation.Portrait;
        dialog.PrintTicket.CopyCount = settings.Copies;
        dialog.PrintTicket.PageScalingFactor = 100;
        if (dialog.ShowDialog() != true) return false;
        using var selectedQueue = dialog.PrintQueue
            ?? throw new InvalidOperationException("Select an installed Windows printer before printing.");

        // Ask the driver to validate the custom media and preserve the selected copy count.
        // Some queues replace unsupported label media with Letter/A4. Do not silently resize.
        var requested = dialog.PrintTicket.Clone();
        requested.PageMediaSize = new PageMediaSize(width, height);
        requested.PageOrientation = PageOrientation.Portrait;
        requested.PageScalingFactor = 100;
        var validated = selectedQueue.MergeAndValidatePrintTicket(dialog.PrintTicket, requested).ValidatedPrintTicket;
        if (validated.PageMediaSize?.Width is double mediaWidth &&
            validated.PageMediaSize?.Height is double mediaHeight &&
            (Math.Abs(mediaWidth - width) > 2 || Math.Abs(mediaHeight - height) > 2))
            throw new InvalidOperationException("This driver did not accept the selected label size. Add the label size in the printer's Windows preferences, or use direct TSPL printing for a compatible printer.");
        dialog.PrintTicket = validated;

        var document = new FixedDocument();
        document.DocumentPaginator.PageSize = new Size(width, height);
        var page = new FixedPage { Width = width, Height = height, Background = Brushes.White, ClipToBounds = true };
        var label = new Image { Source = bitmap, Width = width, Height = height, Stretch = Stretch.Fill };
        FixedPage.SetLeft(label, settings.OffsetXMm * 96 / 25.4);
        FixedPage.SetTop(label, settings.OffsetYMm * 96 / 25.4);
        page.Children.Add(label);
        var content = new PageContent();
        ((IAddChild)content).AddChild(page);
        document.Pages.Add(content);
        dialog.PrintDocument(document.DocumentPaginator, "Shipping Printer label");
        return true;
    }

    /// <summary>Queues a RAW job. Cancellation/error aborts the incomplete spool job where possible.</summary>
    public static Task SendRawAsync(string printerName, byte[] data, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(printerName);
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length == 0 || data.Length > 16 * 1024 * 1024)
            throw new ArgumentException("The raw print job must contain between 1 byte and 16 MB.", nameof(data));
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!OpenPrinter(printerName, out var printer, IntPtr.Zero)) ThrowLastError("Open printer");
            bool started = false;
            try
            {
                var info = new DocInfo { DocumentName = "Shipping Printer TSPL label", DataType = "RAW" };
                if (StartDocPrinter(printer, 1, ref info) == 0) ThrowLastError("Start print job");
                started = true;
                if (!StartPagePrinter(printer)) ThrowLastError("Start print page");
                var pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
                try
                {
                    int offset = 0;
                    while (offset < data.Length)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int count = Math.Min(64 * 1024, data.Length - offset);
                        if (!WritePrinter(printer, IntPtr.Add(pinned.AddrOfPinnedObject(), offset), count, out int written))
                            ThrowLastError("Write print job");
                        if (written <= 0 || written > count) throw new IOException("The printer spooler did not accept the print data.");
                        offset += written;
                    }
                }
                finally { pinned.Free(); }
                cancellationToken.ThrowIfCancellationRequested();
                if (!EndPagePrinter(printer)) ThrowLastError("End print page");
                if (!EndDocPrinter(printer)) ThrowLastError("Submit print job");
                started = false;
            }
            finally
            {
                if (started) AbortPrinter(printer);
                ClosePrinter(printer);
            }
        }, cancellationToken);
    }

    private static void ThrowLastError(string operation) => throw new Win32Exception(Marshal.GetLastWin32Error(), operation + " failed.");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DocInfo
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string DocumentName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? OutputFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string DataType;
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenPrinter(string name, out IntPtr printer, IntPtr defaults);
    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int StartDocPrinter(IntPtr printer, int level, ref DocInfo info);
    [DllImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool StartPagePrinter(IntPtr printer);
    [DllImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WritePrinter(IntPtr printer, IntPtr buffer, int count, out int written);
    [DllImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EndPagePrinter(IntPtr printer);
    [DllImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EndDocPrinter(IntPtr printer);
    [DllImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AbortPrinter(IntPtr printer);
    [DllImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClosePrinter(IntPtr printer);
}
