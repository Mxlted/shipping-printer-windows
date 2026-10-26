using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ShippingPrinter.Windows.Services;
namespace ShippingPrinter.Windows;

public sealed class CropWindow : Window
{
    private readonly BitmapSource _source;
    private readonly Canvas _canvas = new() { Background = Brushes.Transparent, Cursor = Cursors.Cross };
    private readonly Rectangle _selection = new() { StrokeThickness = 2, Visibility = Visibility.Collapsed };
    private Point? _start; private Rect _rect; private double _scale = 1;
    public BitmapSource? Result { get; private set; }
    public CropWindow(BitmapSource source)
    {
        Style = (Style)Application.Current.FindResource(typeof(Window));
        _selection.SetResourceReference(Shape.StrokeProperty, "Focus");
        _selection.SetResourceReference(Shape.FillProperty, "CropFill");
        _source = source; Title = "Crop label"; Width = 900; Height = 760; MinWidth = 500; MinHeight = 400; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var cancel = DialogLayout.CancelButton(); var apply = new Button { Content = "Apply crop", Style = (Style)FindResource("Primary"), IsDefault = true, IsEnabled = false, Margin = new Thickness(0) }; buttons.Children.Add(cancel); buttons.Children.Add(apply);
        var host = new Grid { ClipToBounds = true, Margin = new Thickness(24) };
        var workspace = new Border { Child = host };
        workspace.SetResourceReference(Border.BackgroundProperty, "Canvas");
        Content = DialogLayout.Create("Crop label", "Drag around the area you want to keep.", workspace, buttons);
        var image = new Image { Source = source, Stretch = Stretch.Fill }; _canvas.Children.Add(image); _canvas.Children.Add(_selection); host.Children.Add(_canvas);
        _canvas.HorizontalAlignment = HorizontalAlignment.Center; _canvas.VerticalAlignment = VerticalAlignment.Center;
        host.SizeChanged += (_, _) => { _scale = Math.Min(host.ActualWidth / source.PixelWidth, host.ActualHeight / source.PixelHeight); _canvas.Width = image.Width = source.PixelWidth * _scale; _canvas.Height = image.Height = source.PixelHeight * _scale; _selection.Visibility = Visibility.Collapsed; _rect = Rect.Empty; apply.IsEnabled = false; };
        Point Bound(Point p) => new(Math.Clamp(p.X, 0, _canvas.Width), Math.Clamp(p.Y, 0, _canvas.Height));
        _canvas.MouseLeftButtonDown += (_, e) => { _start = Bound(e.GetPosition(_canvas)); _canvas.CaptureMouse(); };
        _canvas.MouseMove += (_, e) =>
        {
            if (_start is not { } p) return; _rect = new Rect(p, Bound(e.GetPosition(_canvas)));
            Canvas.SetLeft(_selection, _rect.X); Canvas.SetTop(_selection, _rect.Y); _selection.Width = _rect.Width; _selection.Height = _rect.Height; _selection.Visibility = Visibility.Visible; apply.IsEnabled = _rect.Width >= 3 && _rect.Height >= 3;
        };
        _canvas.MouseLeftButtonUp += (_, _) => { _start = null; _canvas.ReleaseMouseCapture(); };
        apply.Click += (_, _) =>
        {
            if (_rect.IsEmpty || _rect.Width < 3 || _rect.Height < 3) { MessageBox.Show(this, "Drag to select an area first.", "Crop label"); return; }
            var x = Math.Clamp((int)Math.Floor(_rect.X / _scale), 0, source.PixelWidth - 1); var y = Math.Clamp((int)Math.Floor(_rect.Y / _scale), 0, source.PixelHeight - 1);
            var w = Math.Clamp((int)Math.Round(_rect.Width / _scale), 1, source.PixelWidth - x); var h = Math.Clamp((int)Math.Round(_rect.Height / _scale), 1, source.PixelHeight - y);
            Result = ImageService.Crop(_source, new Int32Rect(x, y, w, h)); DialogResult = true;
        };
    }
}
