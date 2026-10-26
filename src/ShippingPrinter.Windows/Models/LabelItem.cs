using System.ComponentModel;
using System.Windows.Media.Imaging;
namespace ShippingPrinter.Windows.Models;

public class LabelItem : INotifyPropertyChanged
{
    private BitmapSource _image = null!;
    public event PropertyChangedEventHandler? PropertyChanged;
    public required string Name { get; init; }
    public required string Detail { get; init; }
    public required BitmapSource Original { get; init; }
    public required BitmapSource Image
    {
        get => _image;
        set
        {
            if (ReferenceEquals(_image, value)) return;
            _image = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Image)));
        }
    }
    public string? BarcodeContent { get; init; }
    public override string ToString() => Name;
}
