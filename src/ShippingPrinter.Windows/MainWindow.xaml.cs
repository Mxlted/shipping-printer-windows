using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using ShippingPrinter.Windows.Models;
using ShippingPrinter.Windows.Services;
using ZXing;

namespace ShippingPrinter.Windows;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<LabelItem> _items = [];
    private PrintSettings _settings = SettingsStore.Load();
    private bool _ready, _busy;
    private CancellationTokenSource? _operation;
    private BitmapSource? _rendered;
    private PrinterSelection _printer = new();
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private LabelItem? Selected => ItemsList.SelectedItem as LabelItem;

    public MainWindow()
    {
        InitializeComponent();
        ItemsList.ItemsSource = _items;
        _printer = _settings.Printer ?? new PrinterSelection();
        if (string.IsNullOrWhiteSpace(_printer.PrinterName)) _printer.PrinterName = _settings.PrinterName;
        LoadFields(); ApplyTheme(); _ready = true; UpdatePreview(); UpdatePrinterHint();
        _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); UpdatePreview(); };
    }
    private static string Num(double value) => value.ToString("0.##", CultureInfo.CurrentCulture);
    private void LoadFields()
    {
        WidthBox.Text = Num(_settings.WidthMm); HeightBox.Text = Num(_settings.HeightMm);
        MarginBox.Text = Num(_settings.MarginMm); GapBox.Text = Num(_settings.GapMm);
        CopiesBox.Text = _settings.Copies.ToString(); DensityBox.Text = _settings.Density.ToString(); SpeedBox.Text = _settings.Speed.ToString();
        OffsetXBox.Text = Num(_settings.OffsetXMm); OffsetYBox.Text = Num(_settings.OffsetYMm);
        MonoBox.IsChecked = _settings.Monochrome; ThresholdSlider.Value = _settings.Threshold;
        DpiBox.SelectedIndex = _settings.Dpi == 300 ? 1 : 0;
        PresetBox.SelectedIndex = (_settings.WidthMm, _settings.HeightMm) switch
        {
            var (w, h) when Math.Abs(w - 101.6) < .01 && Math.Abs(h - 152.4) < .01 => 0,
            var (w, h) when Math.Abs(w - 100) < .01 && Math.Abs(h - 150) < .01 => 1,
            var (w, h) when Math.Abs(w - 50) < .01 && Math.Abs(h - 30) < .01 => 2,
            var (w, h) when Math.Abs(w - 100) < .01 && Math.Abs(h - 100) < .01 => 3,
            _ => 4
        };
    }
    private PrintSettings ReadSettings()
    {
        double Number(TextBox box, string label) => double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) && double.IsFinite(v) ? v : throw new ArgumentException($"Enter a valid number for {label}.");
        int Integer(TextBox box, string label) => int.TryParse(box.Text, out var v) ? v : throw new ArgumentException($"Enter a whole number for {label}.");
        var value = new PrintSettings { WidthMm = Number(WidthBox, "width"), HeightMm = Number(HeightBox, "height"), MarginMm = Number(MarginBox, "margin"), GapMm = Number(GapBox, "gap"), Copies = Integer(CopiesBox, "copies"), Density = Integer(DensityBox, "density"), Speed = Integer(SpeedBox, "speed"), Dpi = DpiBox.SelectedIndex == 1 ? 300 : 203, Monochrome = MonoBox.IsChecked == true, Threshold = (int)ThresholdSlider.Value, OffsetXMm = Number(OffsetXBox, "X offset"), OffsetYMm = Number(OffsetYBox, "Y offset"), PrinterName = _printer.PrinterName, Printer = _printer, DarkMode = _settings.DarkMode };
        value.Validate(); return value;
    }
    private void UpdatePreview()
    {
        if (!_ready) return;
        CountText.Text = _items.Count.ToString(CultureInfo.CurrentCulture);
        DocumentTitle.Text = Selected?.Name ?? "Label preview";
        DocumentTitle.ToolTip = Selected?.Name;
        DocumentDetail.Text = Selected is null ? "Select a label to preview its print layout" : $"Label {ItemsList.SelectedIndex + 1} of {_items.Count}  ·  {Selected.Detail}";
        EmptyState.Visibility = Selected is null ? Visibility.Visible : Visibility.Collapsed;
        UpdateActions();
        try
        {
            _settings = ReadSettings();
            _rendered = Selected is null ? null : ImageService.RenderLabel(Selected.Image, _settings);
            PreviewImage.Source = _rendered;
            PreviewInfo.Text = Selected is null ? "PDF · PNG · JPG · TIFF" : $"{Num(_settings.WidthMm)} × {Num(_settings.HeightMm)} mm  ·  {_settings.Dpi} DPI";
        }
        catch (Exception ex) { _rendered = null; PreviewImage.Source = null; PrintButton.IsEnabled = false; Status(ex.Message); }
    }
    private void Status(string text) => StatusText.Text = text;
    private void Error(Exception ex) { Status(ex.Message); MessageBox.Show(this, ex.Message, "Shipping Printer", MessageBoxButton.OK, MessageBoxImage.Warning); }
    private void Setting_Changed(object sender, RoutedEventArgs e) { if (_ready) { _previewTimer.Stop(); _previewTimer.Start(); } }
    private void Threshold_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => Setting_Changed(sender, e);
    private void Preset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        var preset = PresetBox.SelectedIndex switch { 0 => (101.6, 152.4), 1 => (100d, 150d), 2 => (50d, 30d), 3 => (100d, 100d), _ => (0d, 0d) };
        if (preset.Item1 > 0) { WidthBox.Text = Num(preset.Item1); HeightBox.Text = Num(preset.Item2); UpdatePreview(); }
    }
    private void Items_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdatePreview();
    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new OpenFileDialog { Title = "Import labels", Filter = "Labels|*.pdf;*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.gif|PDF documents|*.pdf|Images|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.gif", Multiselect = true };
        if (dialog.ShowDialog(this) == true) await ImportPathsAsync(dialog.FileNames);
    }
    public async Task ImportPathsAsync(IEnumerable<string> paths)
    {
        if (_busy) return;
        SetBusy(true); _operation = new();
        var failures = new List<string>(); var count = 0;
        try
        {
            foreach (var path in paths)
            {
                _operation.Token.ThrowIfCancellationRequested(); Status($"Importing {Path.GetFileName(path)}…");
                try
                {
                    var items = await ImageService.ImportAsync(path, _operation.Token);
                    var pixels = _items.Concat(items).Sum(x => (long)x.Original.PixelWidth * x.Original.PixelHeight);
                    if (_items.Count + items.Count > 100 || pixels > 64_000_000) throw new ArgumentException("Workspace limit reached (100 labels or 64 megapixels). Remove some labels before importing more.");
                    foreach (var item in items) { _items.Add(item); count++; }
                    if (items.Count > 0) ItemsList.SelectedItem = items[0];
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { failures.Add($"{Path.GetFileName(path)}: {ex.Message}"); }
            }
            Status($"Imported {count} label{(count == 1 ? "" : "s")}." + (failures.Count > 0 ? $" {failures.Count} file(s) could not be imported." : ""));
            if (failures.Count > 0) MessageBox.Show(this, string.Join("\n\n", failures.Take(8)), "Import details", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (OperationCanceledException) { Status($"Import cancelled. {count} labels were added."); }
        finally { _operation.Dispose(); _operation = null; SetBusy(false); UpdatePreview(); }
    }
    private void SetBusy(bool busy)
    {
        _busy = busy;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateActions();
    }
    private void UpdateActions()
    {
        var canEdit = Selected is not null && !_busy;
        foreach (var button in new[] { PrintButton, CropButton, RotateLeftButton, RotateRightButton, ResetButton, ExportButton, RemoveButton, ReadBarcodeButton })
            button.IsEnabled = canEdit;
        ImportButton.IsEnabled = CreateBarcodeButton.IsEnabled = PrinterSetupButton.IsEnabled = !_busy;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => _operation?.Cancel();
    private async void Window_Drop(object sender, DragEventArgs e) { if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) await ImportPathsAsync(paths); }
    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || Selected is null) return;
        var index = ItemsList.SelectedIndex; _items.RemoveAt(index); ItemsList.SelectedIndex = Math.Min(index, _items.Count - 1); UpdatePreview();
    }
    private void Edit(Func<BitmapSource, BitmapSource> action)
    {
        if (_busy || Selected is null) return;
        try { Selected.Image = action(Selected.Image); UpdatePreview(); Status("Label updated."); } catch (Exception ex) { Error(ex); }
    }
    private void RotateLeft_Click(object sender, RoutedEventArgs e) => Edit(x => ImageService.Rotate(x, -90));
    private void RotateRight_Click(object sender, RoutedEventArgs e) => Edit(x => ImageService.Rotate(x, 90));
    private void Reset_Click(object sender, RoutedEventArgs e) { if (Selected is { } item) Edit(_ => item.Original); }
    private void Crop_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || Selected is null) return;
        var window = new CropWindow(Selected.Image) { Owner = this };
        if (window.ShowDialog() == true && window.Result is not null) Edit(_ => window.Result);
    }
    private void Barcode_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var window = new BarcodeWindow { Owner = this };
        if (window.ShowDialog() == true && window.Result is { } image) AddGenerated(image, window.LabelName, window.ContentValue);
    }
    private void AddGenerated(BitmapSource image, string name, string? content)
    {
        if (_items.Count >= 100 || _items.Sum(x => (long)x.Original.PixelWidth * x.Original.PixelHeight) + (long)image.PixelWidth * image.PixelHeight > 64_000_000) { Status("Workspace limit reached. Remove labels before adding more."); return; }
        var item = new LabelItem { Name = name, Detail = "Generated locally", Original = image, Image = image, BarcodeContent = content };
        _items.Add(item); ItemsList.SelectedItem = item; UpdatePreview(); Status("Label added to workspace.");
    }
    private void Example_Click(object sender, RoutedEventArgs e)
    {
        var code = ImageService.Barcode("SAMPLE-001", BarcodeFormat.CODE_128, true);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 800, 1200));
            void Text(string text, double x, double y, double size, bool bold = false) => dc.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal), size, Brushes.Black, 1), new Point(x, y));
            Text("SHIPPING PRINTER", 45, 42, 24, true); Text("SAMPLE LABEL", 45, 95, 58, true);
            dc.DrawLine(new Pen(Brushes.Black, 3), new Point(45, 205), new Point(755, 205));
            Text("FROM", 45, 245, 20, true); Text("Your workspace", 45, 285, 34); Text("Windows edition", 45, 335, 27);
            Text("TO", 45, 440, 20, true); Text("Example destination", 45, 480, 38, true); Text("Check size, contrast and alignment.", 45, 540, 27);
            dc.DrawLine(new Pen(Brushes.Black, 3), new Point(45, 650), new Point(755, 650));
            Text("01", 45, 690, 92, true); Text("DEMO · NOT FOR SHIPPING", 210, 730, 22);
            dc.DrawImage(code, new Rect(30, 865, 740, 269));
        }
        var result = new RenderTargetBitmap(800, 1200, 96, 96, PixelFormats.Pbgra32); result.Render(visual); result.Freeze();
        AddGenerated(result, "Example shipping label", "SAMPLE-001");
    }
    private void ReadBarcode_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null) return;
        try
        {
            var result = ImageService.DecodeBarcode(Selected.Image);
            if (result is null) { Status("No barcode found. Try cropping closer to the code."); return; }
            var dialog = new Window { Title = "Barcode content", Owner = this, Width = 540, Height = 250, MinWidth = 380, MinHeight = 200, Style = (Style)FindResource(typeof(Window)), WindowStartupLocation = WindowStartupLocation.CenterOwner };
            dialog.Content = new TextBox { Text = result, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            dialog.ShowDialog(); Status("Barcode decoded locally.");
        }
        catch (Exception ex) { Error(ex); }
    }
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null || _busy) return;
        var menu = new ContextMenu();
        var png = new MenuItem { Header = "Save label as PNG…" }; png.Click += (_, _) => Export(false);
        var tspl = new MenuItem { Header = "Save thermal commands (.tspl)…" }; tspl.Click += (_, _) => Export(true);
        menu.Items.Add(png); menu.Items.Add(tspl); menu.PlacementTarget = (Button)sender; menu.IsOpen = true;
    }
    private void Export(bool thermal)
    {
        try
        {
            if (Selected is null) return;
            var settings = ReadSettings(); var bitmap = ImageService.RenderLabel(Selected.Image, settings);
            var dialog = new SaveFileDialog { Title = "Export label", FileName = "label", DefaultExt = thermal ? ".tspl" : ".png", Filter = thermal ? "TSPL commands|*.tspl" : "PNG image|*.png" };
            if (dialog.ShowDialog(this) != true) return;
            if (thermal) File.WriteAllBytes(dialog.FileName, _printer.IsOffnova ? ShiprinterEncoder.Encode(bitmap, settings) : TsplEncoder.Encode(bitmap, settings)); else ImageService.SavePng(bitmap, dialog.FileName);
            Status($"Saved {Path.GetFileName(dialog.FileName)}.");
        }
        catch (Exception ex) { Error(ex); }
    }
    private void PrinterSetup_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var window = new PrinterWindow(_printer) { Owner = this };
        if (window.ShowDialog() == true)
        {
            _printer = window.Selection; _settings.PrinterName = _printer.PrinterName; _settings.Printer = _printer;
            if (_printer.IsOffnova) { _settings.Dpi = 203; DpiBox.SelectedIndex = 0; UpdatePreview(); }
            UpdatePrinterHint();
            try { SettingsStore.Save(_settings); } catch (Exception ex) { Error(ex); return; }
            Status("Printer setup updated.");
        }
    }
    private void UpdatePrinterHint()
    {
        PrinterNameText.Text = _printer.IsWireless
            ? string.IsNullOrWhiteSpace(_printer.DeviceName) ? (_printer.IsOffnova ? "OFFNOVA N-6140" : "Bluetooth printer") : _printer.DeviceName
            : string.IsNullOrWhiteSpace(_printer.PrinterName) ? "Windows printer" : _printer.PrinterName;
        PrintHint.Text = _printer.IsWireless
            ? string.IsNullOrWhiteSpace(_printer.DeviceId) ? "Select a device to print" : (_printer.IsClassic ? "Bluetooth Classic" : "Bluetooth LE") + " · Saved device"
            : _printer.Mode == "raw" ? "Direct thermal printing" : "Review in the print dialog";
        PrinterSetupButton.ToolTip = $"Printer setup\n{PrinterNameText.Text}\n{PrintHint.Text}";
    }
    private async void Print_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || Selected is null) return;
        try
        {
            var settings = ReadSettings();
            if (_printer.IsOffnova) OffnovaPrinterProfile.Validate(settings);
            if (_printer.IsWireless && string.IsNullOrWhiteSpace(_printer.DeviceId))
                throw new InvalidOperationException("Choose your printer in Printer setup first.");
            var bitmap = ImageService.RenderLabel(Selected.Image, settings);
            if (_printer.Mode == "driver")
            {
                var accepted = PrinterService.PrintWithDriver(bitmap, settings, _printer.PrinterName);
                Status(accepted ? "Label submitted to the Windows print queue." : "Printing cancelled."); return;
            }
            if (MessageBox.Show(this, $"Send {settings.Copies} copy/copies to {(_printer.IsWireless ? _printer.DeviceName : _printer.PrinterName)}?\n\nLoad matching label stock. Check the first label for size, orientation and barcode clarity.", "Print label", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            var bytes = _printer.IsOffnova ? ShiprinterEncoder.Encode(bitmap, settings) : TsplEncoder.Encode(bitmap, settings);
            SetBusy(true); _operation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            Status("Sending label…");
            if (_printer.IsClassic) await ClassicBluetoothPrinterService.SendAsync(_printer.DeviceId, bytes, _operation.Token, new Progress<double>(p => Status($"Sending label… {p:P0}")));
            else if (_printer.IsBluetooth) await BluetoothPrinterService.SendAsync(_printer.DeviceId, _printer.ServiceUuid, _printer.CharacteristicUuid, bytes, _operation.Token, new Progress<double>(p => Status($"Sending label… {p:P0}")), _printer.IsOffnova ? OffnovaPrinterProfile.TransferOptions : null);
            else await PrinterService.SendRawAsync(_printer.PrinterName, bytes, _operation.Token);
            Status("Label sent. Check the printer for the result.");
        }
        catch (OperationCanceledException ex) { Status($"Transfer stopped. {ex.Message} Check the printer before retrying."); }
        catch (Exception ex) { Error(ex); }
        finally { _operation?.Dispose(); _operation = null; SetBusy(false); }
    }
    private void Theme_Click(object sender, RoutedEventArgs e) { _settings.DarkMode = !_settings.DarkMode; ApplyTheme(); }
    private void ApplyTheme()
    {
        Application.Current.Resources["UseDarkTitleBar"] = _settings.DarkMode;
        var colors = _settings.DarkMode
            ? new[] { "#202020", "#171717", "#EEEEEC", "#A6A6A1", "#353535", "#292929", "#333333", "#343434", "#ECECE7", "#202020", "#D6D6CF", "#B4B4AA", "#1D1D1D", "#353535", "#90908A", "#A3A399" }
            : new[] { "#FFFFFF", "#F1F1EE", "#262624", "#686863", "#DEDEDA", "#F7F7F5", "#EDEDE9", "#E9E9E4", "#292927", "#FFFFFF", "#454541", "#73736B", "#FAFAF8", "#D3D3CB", "#76766F", "#73736E" };
        var keys = new[] { "Surface", "Canvas", "Text", "Muted", "Line", "Input", "Hover", "Selected", "Accent", "OnAccent", "AccentHover", "Focus", "Panel", "GridDot", "Subtle", "SelectionEdge" };
        for (var i = 0; i < keys.Length; i++) Application.Current.Resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
        ThemeLabel.Text = _settings.DarkMode ? "Light mode" : "Dark mode";
        ThemeGlyph.Text = _settings.DarkMode ? "\uE706" : "\uE708";
        ThemeButton.ToolTip = _settings.DarkMode ? "Switch to light appearance" : "Switch to dark appearance";
    }
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (e.Key == Key.O) { Import_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.P) { Print_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.V && Keyboard.FocusedElement is not TextBox && !_busy && Clipboard.ContainsImage())
        { var image = Clipboard.GetImage(); if (image is not null) AddGenerated(image, "Clipboard image", null); e.Handled = true; }
    }
    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _operation?.Cancel();
        try { SettingsStore.Save(ReadSettings()); } catch (Exception ex) { Status("Settings could not be saved: " + ex.Message); }
    }
}
