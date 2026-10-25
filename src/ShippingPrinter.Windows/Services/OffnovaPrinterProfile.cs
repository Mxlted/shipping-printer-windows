using ShippingPrinter.Windows.Models;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace ShippingPrinter.Windows.Services;

/// <summary>Shipping Printer 1.0.19 behavior recovered from its ARM64 code.
/// Names are discovery hints; only a hardware print can establish compatibility.</summary>
public static class OffnovaPrinterProfile
{
    public static BleTransferOptions TransferOptions { get; } = new(512, 0, true);
    // AiYin's documented print-write UUID, corroborated by live enumeration of
    // an OFFNOVA N-6140 BLE device. This is a vendor transport preference, not a UUID
    // recovered from Shiprinter (whose selection is capability based).
    public static Guid VendorPrintService { get; } = Guid.Parse("49535343-fe7d-4ae5-8fa9-9fafd205e455");
    public static Guid VendorPrintCharacteristic { get; } = Guid.Parse("49535343-8841-43f4-a8d4-ecbe34729bb3");

    public static bool IsCandidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var normalized = name.Replace("-", "").Replace("_", "").Replace(" ", "");
        return normalized.Contains("OFFNOVA", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("N6140", StringComparison.OrdinalIgnoreCase);
    }

    public static bool AcceptsResponseWrites(BleWriteTarget target) =>
        Enum.TryParse<GattCharacteristicProperties>(target.Properties, out var properties) &&
        properties.HasFlag(GattCharacteristicProperties.Write);

    public static BleWriteTarget? SelectTarget(IEnumerable<BleWriteTarget> targets)
    {
        // The IPA accepts write-capable characteristics without UUID matching.
        // Require its actual acknowledged write mode and avoid its last-service-wins ambiguity.
        var candidates = targets.Where(AcceptsResponseWrites)
            .DistinctBy(t => (t.ServiceUuid, t.CharacteristicUuid)).ToArray();
        var vendor = candidates.FirstOrDefault(t => t.ServiceUuid == VendorPrintService && t.CharacteristicUuid == VendorPrintCharacteristic);
        if (vendor is not null) return vendor;
        return candidates.Length == 1 ? candidates[0] : null;
    }

    public static void Validate(PrintSettings settings)
    {
        settings.Validate();
        if (settings.Dpi != 203)
            throw new ArgumentException("The OFFNOVA N-6140 uses 203 DPI. Select 203 DPI before printing.");
    }
}
