using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media.Imaging;
using ShippingPrinter.Windows.Services;
using ZXing;
namespace ShippingPrinter.Windows;

public sealed class BarcodeWindow : Window
{
    public BitmapSource? Result { get; private set; }
    public string ContentValue => _text.Text;
    public string LabelName => $"{_format.SelectedItem} · {(_text.Text.Length > 24 ? _text.Text[..24] + "…" : _text.Text)}";
    private readonly TextBox _text = new() { Text = "", MinHeight = 110, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly ComboBox _format = new() { ItemsSource = new[] { "Code 128", "QR code", "Code 39", "Code 93", "EAN-13", "UPC-A", "ITF", "Data Matrix" }, SelectedIndex = 0 };
    private readonly CheckBox _caption = new() { Content = "Include readable text", IsChecked = true };
    public BarcodeWindow()
    {
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Title = "Create barcode"; Width = 520; Height = 475; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(26) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "Create a barcode", FontSize = 23, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 18) });
        panel.Children.Add(new TextBlock { Text = "Format", Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(_format); panel.Children.Add(new TextBlock { Text = "Content", Margin = new Thickness(0, 18, 0, 6) }); panel.Children.Add(_text); panel.Children.Add(_caption);
        AutomationProperties.SetName(_format, "Barcode format");
        AutomationProperties.SetName(_text, "Barcode content");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        actions.Children.Add(new Button { Content = "Cancel", IsCancel = true });
        var button = new Button { Content = "Add to workspace", Style = (Style)FindResource("Primary"), Margin = new Thickness(0), IsDefault = true, IsEnabled = false };
        _text.TextChanged += (_, _) => button.IsEnabled = !string.IsNullOrWhiteSpace(_text.Text);
        button.Click += (_, _) =>
        {
            try
            {
                var format = new[] { BarcodeFormat.CODE_128, BarcodeFormat.QR_CODE, BarcodeFormat.CODE_39, BarcodeFormat.CODE_93, BarcodeFormat.EAN_13, BarcodeFormat.UPC_A, BarcodeFormat.ITF, BarcodeFormat.DATA_MATRIX }[_format.SelectedIndex];
                Result = ImageService.Barcode(_text.Text, format, _caption.IsChecked == true); DialogResult = true;
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Check barcode content", MessageBoxButton.OK, MessageBoxImage.Warning); }
        };
        actions.Children.Add(button); panel.Children.Add(actions); Loaded += (_, _) => _text.Focus();
    }
}
