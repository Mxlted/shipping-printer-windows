using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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
        if (!_ready || _workspaceUpdating) return;
        CountText.Text = _items.Count.ToString(CultureInfo.CurrentCulture);
        DocumentTitle.Text = ItemsList.SelectedItems.Count > 1 ? $"{ItemsList.SelectedItems.Count} labels selected" : Selected?.Name ?? "Label preview";
        DocumentTitle.ToolTip = Selected?.Name;
        DocumentDetail.Text = Selected is null ? "" : ItemsList.SelectedItems.Count > 1 ? $"Preview: {Selected.Name}" : $"Label {ItemsList.SelectedIndex + 1} of {_items.Count}  ·  {Selected.Detail}";
        EmptyState.Visibility = Selected is null ? Visibility.Visible : Visibility.Collapsed;
        try
        {
            _settings = ReadSettings();
            _rendered = Selected is null ? null : ImageService.RenderLabel(Selected.Image, _settings);
            PreviewImage.Source = _rendered;
            var marginInfo = _settings.MarginMm == 0 ? "No added margin" : $"{Num(_settings.MarginMm)} mm added margin";
            PreviewInfo.Text = Selected is null ? "PDF · PNG · JPG · TIFF" : $"{Num(_settings.WidthMm)} × {Num(_settings.HeightMm)} mm  ·  {_settings.Dpi} DPI  ·  {marginInfo}";
        }
        catch (Exception ex) { _rendered = null; PreviewImage.Source = null; Status(ex.Message); }
        UpdateActions();
    }
    private void Status(string text) => StatusText.Text = text;
    private void Error(Exception ex) { Status(ex.Message); MessageBox.Show(this, ex.Message, "Shipping Printer", MessageBoxButton.OK, MessageBoxImage.Warning); }
    private void Setting_Changed(object sender, RoutedEventArgs e) { if (_ready) { _previewTimer.Stop(); _previewTimer.Start(); } }
    private void Threshold_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => Setting_Changed(sender, e);
    private void RemoveMargin_Click(object sender, RoutedEventArgs e)
    {
        MarginBox.Text = "0";
        _previewTimer.Stop();
        UpdatePreview();
    }
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
        var before = CaptureWorkspace();
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
                    _workspaceUpdating = true;
                    try
                    {
                        foreach (var item in items) { _items.Add(item); count++; }
                        if (items.Count > 0) SelectLabels([items[0]]);
                    }
                    finally { _workspaceUpdating = false; }
                    UpdatePreview();
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { failures.Add($"{Path.GetFileName(path)}: {ex.Message}"); }
            }
            Status($"Imported {count} label{(count == 1 ? "" : "s")}." + (failures.Count > 0 ? $" {failures.Count} file(s) could not be imported." : ""));
            if (failures.Count > 0) MessageBox.Show(this, string.Join("\n\n", failures.Take(8)), "Import details", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (OperationCanceledException) { Status($"Import cancelled. {count} labels were added."); }
        finally
        {
            _history.Record("Import labels", before, CaptureWorkspace());
            _operation.Dispose(); _operation = null; SetBusy(false); UpdatePreview();
            if (count > 0) FocusLabels();
        }
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
        foreach (var button in new[] { RotateLeftButton, RotateRightButton, RemoveButton })
            button.IsEnabled = canEdit;
        ResetButton.IsEnabled = canEdit && SelectedLabels.Any(item => !ReferenceEquals(item.Image, item.Original));
        CropButton.IsEnabled = ReadBarcodeButton.IsEnabled = canEdit && HasSingleSelection;
        PrintButton.IsEnabled = ExportButton.IsEnabled = canEdit && HasSingleSelection && _rendered is not null;
        PrintButton.ToolTip = HasSingleSelection ? "Print label (Ctrl+P)" : "Select one label to print";
        RemoveButton.Content = ItemsList.SelectedItems.Count > 1 ? $"Remove {ItemsList.SelectedItems.Count} labels" : "Remove label";
        UndoButton.IsEnabled = !_busy && _history.UndoName is not null;
        RedoButton.IsEnabled = !_busy && _history.RedoName is not null;
        UndoButton.ToolTip = _history.UndoName is { } undo ? $"Undo: {undo} (Ctrl+Z)" : "Undo (Ctrl+Z)";
        RedoButton.ToolTip = _history.RedoName is { } redo ? $"Redo: {redo} (Ctrl+Y)" : "Redo (Ctrl+Y)";
        ImportButton.IsEnabled = CreateBarcodeButton.IsEnabled = PrinterSetupButton.IsEnabled = !_busy;
        EmptyImportButton.IsEnabled = ExampleButton.IsEnabled = !_busy;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => _operation?.Cancel();
    private async void Window_Drop(object sender, DragEventArgs e) { if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) await ImportPathsAsync(paths); }
    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || Selected is null) return;
        var before = CaptureWorkspace();
        var removed = SelectedLabels;
        var index = _items.IndexOf(removed[0]);
        _workspaceUpdating = true;
        try
        {
            foreach (var item in removed) _items.Remove(item);
            if (_items.Count > 0) SelectLabels([_items[Math.Min(index, _items.Count - 1)]]);
        }
        finally { _workspaceUpdating = false; }
        _history.Record("Remove labels", before, CaptureWorkspace());
        UpdatePreview();
        FocusLabels();
        Status(removed.Length == 1 ? "Label removed." : $"{removed.Length} labels removed.");
    }
    private void Edit(Func<LabelItem, BitmapSource> action, string name)
    {
        if (_busy || Selected is null) return;
        try
        {
            var before = CaptureWorkspace();
            // Build every result first, so a failed bulk edit changes no labels.
            var changes = SelectedLabels.Select(item => (Item: item, Image: action(item))).ToArray();
            foreach (var change in changes) change.Item.Image = change.Image;
            _history.Record(name, before, CaptureWorkspace());
            UpdatePreview();
            Status(changes.Length == 1 ? "Label updated." : $"{changes.Length} labels updated.");
        }
        catch (Exception ex) { Error(ex); }
    }
    private void RotateLeft_Click(object sender, RoutedEventArgs e) => Edit(item => ImageService.Rotate(item.Image, -90), "Rotate left");
    private void RotateRight_Click(object sender, RoutedEventArgs e) => Edit(item => ImageService.Rotate(item.Image, 90), "Rotate right");
    private void Reset_Click(object sender, RoutedEventArgs e) => Edit(item => item.Original, "Reset edits");
    private void Crop_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !HasSingleSelection || Selected is null) return;
        var window = new CropWindow(Selected.Image) { Owner = this };
        if (window.ShowDialog() == true && window.Result is not null) Edit(_ => window.Result, "Crop label");
    }
    private void Barcode_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var window = new BarcodeWindow { Owner = this };
        if (window.ShowDialog() == true && window.Result is { } image) AddGenerated(image, window.LabelName, window.ContentValue);
    }
    private void AddGenerated(BitmapSource image, string name, string? content)
    {
        if (_busy) return;
        if (_items.Count >= 100 || _items.Sum(x => (long)x.Original.PixelWidth * x.Original.PixelHeight) + (long)image.PixelWidth * image.PixelHeight > 64_000_000) { Status("Workspace limit reached. Remove labels before adding more."); return; }
        var before = CaptureWorkspace();
        var item = new LabelItem { Name = name, Detail = "Generated locally", Original = image, Image = image, BarcodeContent = content };
        _workspaceUpdating = true;
        try { _items.Add(item); SelectLabels([item]); }
        finally { _workspaceUpdating = false; }
        _history.Record("Add label", before, CaptureWorkspace());
        UpdatePreview(); FocusLabels(); Status("Label added.");
    }
    private void Example_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
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
        if (_busy || !HasSingleSelection || Selected is null) return;
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
        if (!HasSingleSelection || Selected is null || _busy) return;
        var menu = new ContextMenu();
        AddMenuItem(menu, "Save as PNG…", "Ctrl+S", SavePng_Click);
        AddMenuItem(menu, "Save thermal commands…", "", SaveThermal_Click);
        menu.PlacementTarget = ExportButton; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; menu.IsOpen = true;
    }
    private void Export(bool thermal)
    {
        try
        {
            if (_busy || !HasSingleSelection || Selected is null) return;
            var settings = ReadSettings(); var bitmap = ImageService.RenderLabel(Selected.Image, settings);
            var name = Selected.Name;
            if (Path.GetExtension(name).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tif" or ".tiff" or ".gif")
                name = Path.GetFileNameWithoutExtension(name);
            name = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var dialog = new SaveFileDialog { Title = "Export label", FileName = string.IsNullOrWhiteSpace(name) ? "label" : name, DefaultExt = thermal ? ".tspl" : ".png", Filter = thermal ? "TSPL commands|*.tspl" : "PNG image|*.png" };
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
        if (_busy || !HasSingleSelection || Selected is null) return;
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
    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _operation?.Cancel();
        try { SettingsStore.Save(ReadSettings()); } catch (Exception ex) { Status("Settings could not be saved: " + ex.Message); }
    }
}
