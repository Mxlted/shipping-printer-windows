using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ShippingPrinter.Windows.Services;

public static class WindowAppearance
{
    public static readonly DependencyProperty UseDarkTitleBarProperty = DependencyProperty.RegisterAttached(
        "UseDarkTitleBar", typeof(bool?), typeof(WindowAppearance),
        new PropertyMetadata(null, OnUseDarkTitleBarChanged));

    public static bool? GetUseDarkTitleBar(DependencyObject target) => (bool?)target.GetValue(UseDarkTitleBarProperty);
    public static void SetUseDarkTitleBar(DependencyObject target, bool? value) => target.SetValue(UseDarkTitleBarProperty, value);

    private static void OnUseDarkTitleBarChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not Window window) return;
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
        {
            Apply(window);
            return;
        }

        // A style can be applied before WPF creates the native window.
        window.SourceInitialized -= OnSourceInitialized;
        window.SourceInitialized += OnSourceInitialized;
    }

    private static void OnSourceInitialized(object? sender, EventArgs args)
    {
        if (sender is not Window window) return;
        window.SourceInitialized -= OnSourceInitialized;
        Apply(window);
    }

    private static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var dark = GetUseDarkTitleBar(window) == true;
        var enabled = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));

        // Windows 11 supports explicit caption colors. Older systems ignore these
        // attributes and retain their standard title bar without affecting the app.
        var captionColor = dark ? 0x001C1C1C : 0x00FFFFFF;
        var textColor = dark ? 0x00F2F2F2 : 0x00202020;
        _ = DwmSetWindowAttribute(handle, 35, ref captionColor, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 36, ref textColor, sizeof(int));
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
