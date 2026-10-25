using System.Windows.Media.Imaging;
namespace ShippingPrinter.Windows.Models;

public class LabelItem
{
    public required string Name { get; init; }
    public required string Detail { get; init; }
    public required BitmapSource Original { get; init; }
    public required BitmapSource Image { get; set; }
    public string? BarcodeContent { get; init; }
    public override string ToString() => Name;
}
