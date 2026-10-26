using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using Microsoft.Win32;
using ShippingPrinter.Windows.Services;

namespace ShippingPrinter.Windows;

public sealed class PrinterSelection
{
    public string Mode { get; set; } = "offnova-ble";
    public string PrinterName { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public Guid ServiceUuid { get; set; }
    public Guid CharacteristicUuid { get; set; }
    [JsonIgnore] public bool IsBluetooth => Mode is "ble" or "offnova-ble";
    [JsonIgnore] public bool IsClassic => Mode == "offnova-classic";
    [JsonIgnore] public bool IsWireless => IsBluetooth || IsClassic;
    [JsonIgnore] public bool IsOffnova => Mode is "offnova-ble" or "offnova-classic";
}

public sealed class PrinterWindow : Window
{
    public PrinterSelection Selection { get; private set; }
    private readonly PrinterSelection _saved;
    private readonly ComboBox _mode = new()
    {
        ItemsSource = new[] { "OFFNOVA N-6140 · Bluetooth LE", "OFFNOVA N-6140 · Bluetooth Classic", "Windows printer driver", "Direct TSPL · installed printer", "Direct TSPL · other Bluetooth LE" }
    };
    private readonly ComboBox _queues = new();
    private readonly ComboBox _devices = new() { DisplayMemberPath = "Name" };
    private readonly ComboBox _targets = new() { DisplayMemberPath = "DisplayName" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock _help = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0) };
    private readonly TextBlock _queueHelp = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), FontSize = 12 };
    private readonly TextBlock _channelHelp = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), FontSize = 12 };
    private readonly StackPanel _blePanel = new();
    private readonly StackPanel _queuePanel = new();
    private readonly StackPanel _classicPanel = new();
    private readonly ComboBox _classicDevices = new() { DisplayMemberPath = "Name" };
    private readonly Button _classicScan = new() { Content = "Find OFFNOVA printer" };
    private readonly Button _classicCheck = new() { Content = "Check connection" };
    private readonly Button _classicExport = new() { Content = "Save connection details", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
    private readonly Button _scan = new() { Content = "Find OFFNOVA printer" };
    private readonly Button _check = new() { Content = "Check connection" };
    private readonly Button _export = new() { Content = "Save connection details", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0) };
    private readonly Button _save = new() { Content = "Use this printer", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0), IsDefault = true };
    private readonly Expander _details = new() { Header = "Connection details", Margin = new Thickness(0, 18, 0, 0) };
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _work;
    private IReadOnlyList<BluetoothPrinterDevice> _nearby = Array.Empty<BluetoothPrinterDevice>();
    private IReadOnlyList<ClassicPrinterDevice> _classicNearby = Array.Empty<ClassicPrinterDevice>();
    private BleDeviceInspection? _inspection;
    private ClassicDeviceInspection? _classicInspection;
    private int _revision;
    private bool _busy, _closed, _bindingDevices;
    private bool IsBluetooth => _mode.SelectedIndex is 0 or 4;
    private bool IsClassic => _mode.SelectedIndex == 1;
    private bool IsOffnova => _mode.SelectedIndex == 0;

    public PrinterWindow(PrinterSelection current)
    {
        Style = (Style)Application.Current.FindResource(typeof(Window));
        _saved = current;
        Selection = current;
        Title = "Printer setup";
        Width = 710; Height = 660; MinHeight = 550; MinWidth = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24, 22, 24, 24) };
        panel.Children.Add(DialogLayout.FieldLabel("Connection"));
        panel.Children.Add(_mode);

        var classicInstructions = new TextBlock
        {
            Text = "Use the OFFNOVA name without an LE suffix. Pair it in Windows Bluetooth settings first; if asked for a PIN, enter 0000. Keep the printer nearby and disconnect the phone app before checking its connection.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0), FontSize = 12
        };
        classicInstructions.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _classicPanel.Children.Add(classicInstructions);
        var classicActions = new WrapPanel { Margin = new Thickness(0, 18, 0, 12) };
        classicActions.Children.Add(_classicScan); classicActions.Children.Add(_classicCheck);
        var bluetoothSettings = new Button { Content = "Windows Bluetooth settings", Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Style = (Style)FindResource("GhostButton") };
        bluetoothSettings.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true }); }
            catch (Exception ex) { _status.Text = $"Could not open Windows Bluetooth settings: {ex.Message}"; }
        };
        _classicPanel.Children.Add(classicActions);
        _classicPanel.Children.Add(DialogLayout.FieldLabel("Printer"));
        _classicPanel.Children.Add(_classicDevices);
        _classicPanel.Children.Add(bluetoothSettings);
        var classicDetails = new Expander { Header = "Connection details", Margin = new Thickness(0, 18, 0, 0) };
        var classicDetailPanel = new StackPanel();
        var classicHelp = new TextBlock { Text = "This connection uses the printer's Bluetooth Serial Port service. Checking discovers that service and sends no print data.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 10, 0, 0) };
        classicHelp.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        classicDetailPanel.Children.Add(classicHelp); classicDetailPanel.Children.Add(_classicExport);
        classicDetails.Content = classicDetailPanel;
        _classicPanel.Children.Add(classicDetails);
        panel.Children.Add(_classicPanel);

        _queuePanel.Children.Add(DialogLayout.FieldLabel("Installed printer", new Thickness(0, 22, 0, 7)));
        _queuePanel.Children.Add(_queues);
        _queueHelp.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _queuePanel.Children.Add(_queueHelp);
        panel.Children.Add(_queuePanel);
        string? queueError = null;
        try
        {
            _queues.ItemsSource = PrinterService.GetPrinters();
            _queues.SelectedItem = current.PrinterName;
            if (_queues.SelectedIndex < 0 && _queues.Items.Count > 0) _queues.SelectedIndex = 0;
        }
        catch (Exception ex) { queueError = ex.Message; }

        _help.SetResourceReference(FrameworkElement.StyleProperty, "MutedText");
        _blePanel.Children.Add(_help);
        var actions = new WrapPanel { Margin = new Thickness(0, 18, 0, 12) };
        actions.Children.Add(_scan); actions.Children.Add(_check);
        _blePanel.Children.Add(actions);
        _blePanel.Children.Add(DialogLayout.FieldLabel("Printer"));
        _blePanel.Children.Add(_devices);
        var detailPanel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        detailPanel.Children.Add(DialogLayout.FieldLabel("Print channel"));
        detailPanel.Children.Add(_targets);
        _channelHelp.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        detailPanel.Children.Add(_channelHelp);
        detailPanel.Children.Add(_export);
        _details.Content = detailPanel;
        _blePanel.Children.Add(_details);
        panel.Children.Add(_blePanel);
        var statusPanel = new Border { Child = _status, Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 22, 0, 0), CornerRadius = new CornerRadius(6) };
        statusPanel.SetResourceReference(Border.BackgroundProperty, "Input");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        panel.Children.Add(statusPanel);
        _export.SetResourceReference(FrameworkElement.StyleProperty, "GhostButton");
        _classicExport.SetResourceReference(FrameworkElement.StyleProperty, "GhostButton");
        _save.Style = (Style)FindResource("Primary");
        var footer = new StackPanel { Orientation = Orientation.Horizontal };
        footer.Children.Add(DialogLayout.CancelButton());
        footer.Children.Add(_save);
        Content = DialogLayout.Create("Printer setup", "Choose a connection for your labels.",
            new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, footer);
        AutomationProperties.SetName(_mode, "Printer connection");
        AutomationProperties.SetName(_queues, "Installed printer");
        AutomationProperties.SetName(_devices, "Bluetooth LE printer");
        AutomationProperties.SetName(_classicDevices, "Bluetooth Classic printer");
        AutomationProperties.SetName(_targets, "Print channel");

        _mode.SelectedIndex = current.Mode switch { "offnova-classic" => 1, "driver" => 2, "raw" => 3, "ble" => 4, _ => 0 };
        _mode.SelectionChanged += (_, _) => ModeChanged();
        _devices.SelectionChanged += async (_, _) =>
        {
            if (!_bindingDevices) await InspectSelectedAsync();
        };
        _targets.SelectionChanged += (_, _) => UpdateControls();
        _queues.SelectionChanged += (_, _) => UpdateControls();
        _classicDevices.SelectionChanged += async (_, _) => { if (!_bindingDevices) await InspectClassicAsync(); };
        _scan.Click += async (_, _) => await ScanAsync();
        _check.Click += async (_, _) => await InspectSelectedAsync();
        _classicScan.Click += async (_, _) => await ScanClassicAsync();
        _classicCheck.Click += async (_, _) => await InspectClassicAsync();
        _export.Click += (_, _) => ExportDetails();
        _classicExport.Click += (_, _) => ExportDetails();
        _save.Click += (_, _) => SaveSelection();
        Closed += (_, _) =>
        {
            _closed = true;
            _revision++;
            _lifetime.Cancel();
            _work?.Cancel();
            _work?.Dispose();
            _lifetime.Dispose();
        };
        ModeChanged();
        if (!IsBluetooth && !IsClassic && queueError is not null) _status.Text = queueError;
    }

    private void ModeChanged()
    {
        CancelWork();
        _blePanel.Visibility = IsBluetooth ? Visibility.Visible : Visibility.Collapsed;
        _classicPanel.Visibility = IsClassic ? Visibility.Visible : Visibility.Collapsed;
        _queuePanel.Visibility = IsBluetooth || IsClassic ? Visibility.Collapsed : Visibility.Visible;
        _scan.Content = IsOffnova ? "Find OFFNOVA printer" : "Find Bluetooth LE devices";
        _help.Text = IsOffnova
            ? "Turn on your N-6140 and keep it close to this computer. Disconnect it in the phone's Shipping Printer app, then find it here. You do not need to pair it in Windows first."
            : "Find your nearby BLE printer, then choose its documented TSPL print channel. Other Bluetooth devices can also appear in this list.";
        _queueHelp.Text = _mode.SelectedIndex == 3
            ? "This sends raw TSPL commands. Choose an installed queue for a printer that accepts TSPL."
            : "Use the manufacturer's Windows driver for USB or network printing. Some older OFFNOVA models also support a paired Bluetooth COM port: select that outgoing port in the manufacturer's driver, then use its queue here.";
        _channelHelp.Text = IsOffnova
            ? "The OFFNOVA profile selects AiYin's documented print channel when present, otherwise a single acknowledged-write channel. Other ambiguous channels need a documented choice. Physical output still needs a label test."
            : "Choose the writable service and characteristic documented for your printer. Connection details can be saved without sending a label.";
        _details.IsExpanded = !IsOffnova;
        BindDevices(includeSaved: true);
        BindClassicDevices();
        _status.Text = IsBluetooth
            ? _devices.SelectedItem is BluetoothPrinterDevice
                ? "Saved printer shown. Check its connection before using these settings."
                : "Find your printer to inspect its connection. No label is sent during setup."
            : IsClassic
                ? _saved.IsClassic && !string.IsNullOrWhiteSpace(_saved.DeviceName)
                    ? $"Saved printer: {_saved.DeviceName}. Find it again and check its connection before saving."
                    : "Find your OFFNOVA printer. If it is not paired, pair it in Windows settings and scan again."
                : _mode.SelectedIndex == 3 ? "Choose a TSPL-compatible printer queue." : "The Windows print dialog will let you review the job before printing.";
        UpdateControls();
    }

    private void CancelWork()
    {
        _revision++;
        _work?.Cancel();
        _work?.Dispose();
        _work = null;
        _busy = false;
        _inspection = null;
        _classicInspection = null;
        _targets.ItemsSource = null;
    }

    private void BindDevices(bool includeSaved)
    {
        var items = _nearby.Where(device => !IsOffnova || OffnovaPrinterProfile.IsCandidateName(device.Name)).ToList();
        if (includeSaved && _saved.IsBluetooth && !string.IsNullOrWhiteSpace(_saved.DeviceId) &&
            (!IsOffnova || OffnovaPrinterProfile.IsCandidateName(_saved.DeviceName)) &&
            !items.Any(device => device.Id == _saved.DeviceId))
            items.Insert(0, new BluetoothPrinterDevice(_saved.DeviceId, _saved.DeviceName));
        _bindingDevices = true;
        try
        {
            _devices.ItemsSource = items;
            _devices.SelectedItem = items.FirstOrDefault(device => device.Id == _saved.DeviceId);
        }
        finally { _bindingDevices = false; }
    }

    private async Task ScanAsync()
    {
        if (_closed || _busy) return;
        CancelWork();
        _work = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _work.Token;
        int revision = _revision;
        _busy = true;
        _status.Text = IsOffnova ? "Looking for nearby OFFNOVA printers…" : "Looking for Bluetooth LE devices…";
        UpdateControls();
        try
        {
            var devices = await BluetoothPrinterService.DiscoverAsync(token);
            if (_closed || revision != _revision) return;
            _nearby = devices;
            BindDevices(includeSaved: false);
            _status.Text = _devices.Items.Count == 0
                ? IsOffnova
                    ? "No OFFNOVA printer found. Move it closer, check its power, and disconnect the phone app, then scan again. Other Bluetooth LE mode can show devices with unfamiliar names."
                    : "No BLE devices found. Check Bluetooth in Windows and move the printer closer, then scan again."
                : $"Found {_devices.Items.Count} device(s). Select your printer, then check its connection.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed && revision == _revision) _status.Text = ex.Message; }
        finally
        {
            if (!_closed && revision == _revision) { _busy = false; UpdateControls(); }
        }
    }

    private async Task InspectSelectedAsync()
    {
        if (_closed || !IsBluetooth) return;
        CancelWork();
        if (_devices.SelectedItem is not BluetoothPrinterDevice device) { UpdateControls(); return; }
        _work = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _work.Token;
        int revision = _revision;
        bool offnova = IsOffnova;
        _busy = true;
        _status.Text = $"Checking {device.Name}… No print data is sent.";
        UpdateControls();
        try
        {
            var inspection = await BluetoothPrinterService.InspectAsync(device.Id, token);
            if (_closed || revision != _revision) return;
            _inspection = inspection;
            var discovered = BluetoothPrinterService.GetWriteTargets(inspection);
            var targets = offnova ? discovered.Where(OffnovaPrinterProfile.AcceptsResponseWrites).ToArray() : discovered.ToArray();
            _targets.ItemsSource = targets;
            _targets.SelectedItem = offnova ? OffnovaPrinterProfile.SelectTarget(targets) : null;
            if (inspection.ServiceDiscoveryStatus != "Success")
            {
                _status.Text = $"Connection inspection returned {inspection.ServiceDiscoveryStatus}. Move the printer closer and disconnect the phone app. Connection details are available below.";
                _details.IsExpanded = true;
            }
            else if (targets.Length == 0)
            {
                _status.Text = offnova
                    ? "No acknowledged-write channel was found. Save connection details to check this printer's Bluetooth services."
                    : "No accessible writable channel was found. Save connection details to inspect this device's services.";
                _details.IsExpanded = true;
            }
            else if (_targets.SelectedItem is not null)
                _status.Text = "Connection inspected and print channel selected. Use this printer to save the setup; a physical label test is still needed.";
            else
            {
                _status.Text = offnova
                    ? "Several possible print channels were found. Choose the printer's channel in Connection details before saving."
                    : "Connection inspected. Choose the documented print channel in Connection details.";
                _details.IsExpanded = true;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed && revision == _revision) _status.Text = ex.Message; }
        finally
        {
            if (!_closed && revision == _revision) { _busy = false; UpdateControls(); }
        }
    }

    private void UpdateControls()
    {
        if (_closed) return;
        bool currentInspection = _devices.SelectedItem is BluetoothPrinterDevice device && _inspection?.DeviceId == device.Id;
        bool currentClassicInspection = _classicDevices.SelectedItem is ClassicPrinterDevice classic && _classicInspection?.DeviceId == classic.Id;
        _scan.IsEnabled = !_busy;
        _check.IsEnabled = !_busy && _devices.SelectedItem is BluetoothPrinterDevice;
        _devices.IsEnabled = !_busy;
        _targets.IsEnabled = !_busy && currentInspection;
        _export.IsEnabled = !_busy && currentInspection;
        _classicScan.IsEnabled = !_busy;
        _classicCheck.IsEnabled = !_busy && _classicDevices.SelectedItem is ClassicPrinterDevice { IsPaired: true };
        _classicDevices.IsEnabled = !_busy;
        _classicExport.IsEnabled = !_busy && currentClassicInspection;
        _save.IsEnabled = !_busy && (IsBluetooth
            ? currentInspection && _inspection?.ServiceDiscoveryStatus == "Success" && _targets.SelectedItem is BleWriteTarget
            : IsClassic
                ? currentClassicInspection && _classicInspection is { IsPaired: true, ServiceDiscoveryStatus: "Success", ServiceCount: 1 }
                : _mode.SelectedIndex == 2 || _queues.SelectedItem is string);
    }

    private void BindClassicDevices()
    {
        _bindingDevices = true;
        try
        {
            _classicDevices.ItemsSource = _classicNearby;
            _classicDevices.SelectedItem = _classicNearby.FirstOrDefault(device => device.Id == _saved.DeviceId);
        }
        finally { _bindingDevices = false; }
    }

    private async Task ScanClassicAsync()
    {
        if (_closed || _busy) return;
        CancelWork();
        _work = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _work.Token;
        int revision = _revision;
        _busy = true;
        _status.Text = "Looking for OFFNOVA Bluetooth Classic printers…";
        UpdateControls();
        try
        {
            var devices = await ClassicBluetoothPrinterService.DiscoverAsync(token);
            if (_closed || revision != _revision) return;
            _classicNearby = devices.Where(device => OffnovaPrinterProfile.IsCandidateName(device.Name)).ToArray();
            BindClassicDevices();
            _status.Text = _classicNearby.Count == 0
                ? "No OFFNOVA Classic printer found. Move it closer, check its power, and disconnect the phone app. If its name ends in LE, try the Bluetooth LE mode."
                : "Select your printer. Unpaired printers need pairing in Windows Bluetooth settings before their connection can be checked.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed && revision == _revision) _status.Text = ex.Message; }
        finally
        {
            if (!_closed && revision == _revision) { _busy = false; UpdateControls(); }
        }
    }

    private async Task InspectClassicAsync()
    {
        if (_closed || !IsClassic) return;
        CancelWork();
        if (_classicDevices.SelectedItem is not ClassicPrinterDevice device) { UpdateControls(); return; }
        if (!device.IsPaired)
        {
            _status.Text = $"{device.Name} is not paired. Open Windows Bluetooth settings, pair this printer using PIN 0000 if requested, then find it again here.";
            UpdateControls();
            return;
        }
        _work = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _work.Token;
        int revision = _revision;
        _busy = true;
        _status.Text = $"Checking the Serial Port service on {device.Name}… No print data is sent.";
        UpdateControls();
        try
        {
            var inspection = await ClassicBluetoothPrinterService.InspectAsync(device.Id, token);
            if (_closed || revision != _revision) return;
            _classicInspection = inspection;
            _status.Text = !inspection.IsPaired
                ? "The printer is no longer paired. Pair it in Windows Bluetooth settings, then find it again here."
                : inspection.ServiceDiscoveryStatus != "Success"
                    ? $"Connection inspection returned {inspection.ServiceDiscoveryStatus}. Move the printer closer and disconnect the phone app, then check again."
                    : inspection.ServiceCount == 0
                        ? "No Bluetooth Serial Port service was found. Save connection details to inspect this printer, or try the Bluetooth LE mode."
                        : inspection.ServiceCount != 1
                            ? "Several Serial Port services were found. Use the manufacturer's Windows driver with its documented outgoing COM port."
                            : "Bluetooth Serial Port service found. Use this printer to save the setup; a physical label test is still needed.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed && revision == _revision) _status.Text = ex.Message; }
        finally
        {
            if (!_closed && revision == _revision) { _busy = false; UpdateControls(); }
        }
    }

    private void ExportDetails()
    {
        object? inspection = IsClassic ? _classicInspection : _inspection;
        if (_busy || inspection is null) return;
        var dialog = new SaveFileDialog { Title = "Save Bluetooth connection details", Filter = "JSON details|*.json", FileName = IsClassic ? "OFFNOVA-classic-connection.json" : IsOffnova ? "OFFNOVA-connection.json" : "Bluetooth-connection.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(inspection, new JsonSerializerOptions { WriteIndented = true }));
            _status.Text = "Connection details saved. No print job was sent.";
        }
        catch (Exception ex) { _status.Text = $"Could not save connection details: {ex.Message}"; }
    }

    private void SaveSelection()
    {
        if (_busy || !_save.IsEnabled) return;
        var selection = new PrinterSelection
        {
            Mode = _mode.SelectedIndex switch { 1 => "offnova-classic", 2 => "driver", 3 => "raw", 4 => "ble", _ => "offnova-ble" },
            PrinterName = _queues.SelectedItem as string ?? ""
        };
        if (selection.IsBluetooth)
        {
            if (_devices.SelectedItem is not BluetoothPrinterDevice device || _targets.SelectedItem is not BleWriteTarget target ||
                _inspection?.DeviceId != device.Id || _inspection.ServiceDiscoveryStatus != "Success") return;
            if (selection.IsOffnova && !OffnovaPrinterProfile.AcceptsResponseWrites(target)) return;
            selection.DeviceId = device.Id;
            selection.DeviceName = device.Name;
            selection.ServiceUuid = target.ServiceUuid;
            selection.CharacteristicUuid = target.CharacteristicUuid;
        }
        else if (selection.IsClassic)
        {
            if (_classicDevices.SelectedItem is not ClassicPrinterDevice { IsPaired: true } device ||
                _classicInspection?.DeviceId != device.Id || _classicInspection.ServiceDiscoveryStatus != "Success" ||
                _classicInspection.ServiceCount != 1 || !_classicInspection.IsPaired) return;
            selection.DeviceId = device.Id;
            selection.DeviceName = device.Name;
        }
        Selection = selection;
        DialogResult = true;
    }
}
