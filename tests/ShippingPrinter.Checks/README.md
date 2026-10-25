# Offline Windows checks

From the repository root:

```powershell
dotnet run --project tests/ShippingPrinter.Checks/ShippingPrinter.Checks.csproj --configuration Release -- artifacts
```

The runner uses an STA thread and WPF dispatcher, so Windows PDF rendering can complete normally. It writes `artifacts/checks.json` and small generated PNG/PDF/TSPL fixtures. Settings are isolated in `artifacts/settings` through `SHIPPING_PRINTER_DATA`; the original environment value is restored when the runner finishes.

Checks cover barcode generation/decoding, image transformation and import size limits, label dimensions/colors, settings validation, concurrent saves and complete printer-selection persistence, Windows PDF rendering, file-handle release, generic TSPL, independent inflation of compressed mode-3 payloads, bitmap polarity/padding, BLE packet limits, typed addresses, and unambiguous channel selection. These checks do not enumerate printers, send print jobs, access a network, discover Bluetooth devices, or establish physical printer compatibility.
