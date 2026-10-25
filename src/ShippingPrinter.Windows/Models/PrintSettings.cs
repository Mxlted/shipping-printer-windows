namespace ShippingPrinter.Windows.Models;

public class PrintSettings
{
    public double WidthMm { get; set; } = 101.6;
    public double HeightMm { get; set; } = 152.4;
    public double GapMm { get; set; } = 3;
    public int Dpi { get; set; } = 203;
    public int Density { get; set; } = 8;
    public int Speed { get; set; } = 4;
    public int Copies { get; set; } = 1;
    public double OffsetXMm { get; set; }
    public double OffsetYMm { get; set; }
    public bool Monochrome { get; set; } = true;
    public int Threshold { get; set; } = 160;
    public double MarginMm { get; set; } = 2;
    public string PrinterName { get; set; } = "";
    public PrinterSelection Printer { get; set; } = new();
    public bool DarkMode { get; set; }
    public void Validate()
    {
        if (!double.IsFinite(WidthMm) || WidthMm < 38 || WidthMm > 110) throw new ArgumentException("Paper width must be between 38 and 110 mm.");
        if (!double.IsFinite(HeightMm) || HeightMm < 20 || HeightMm > 500) throw new ArgumentException("Paper height must be between 20 and 500 mm.");
        if (Dpi is not (203 or 300)) throw new ArgumentException("Choose 203 or 300 DPI.");
        if (Density < 1 || Density > 15 || Speed < 1 || Speed > 8) throw new ArgumentException("Density must be 1–15 and speed must be 1–8.");
        if (Copies < 1 || Copies > 100) throw new ArgumentException("Copies must be between 1 and 100.");
        if (!double.IsFinite(GapMm) || GapMm < 0 || GapMm > 20) throw new ArgumentException("Gap must be between 0 and 20 mm.");
        if (!double.IsFinite(MarginMm) || MarginMm < 0 || MarginMm * 2 >= Math.Min(WidthMm, HeightMm)) throw new ArgumentException("Margin must leave space inside the label.");
        if (!double.IsFinite(OffsetXMm) || !double.IsFinite(OffsetYMm) || OffsetXMm < 0 || OffsetYMm < 0 || OffsetXMm >= WidthMm || OffsetYMm >= HeightMm) throw new ArgumentException("Offsets must be positive and within the label.");
        if (Threshold < 1 || Threshold > 254) throw new ArgumentException("Threshold must be between 1 and 254.");
    }
}
