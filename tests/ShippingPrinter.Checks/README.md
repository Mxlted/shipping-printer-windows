# Offline Windows checks

From the repository root:

```powershell
dotnet run --project tests/ShippingPrinter.Checks/ShippingPrinter.Checks.csproj --configuration Release --runtime win-x64 --self-contained true -- artifacts
```

The runner uses an STA thread and a windowless WPF application, so Windows PDF rendering and shutdown follow the desktop app's lifecycle. It writes `artifacts/checks.json` and small generated PNG/PDF/TSPL fixtures. Settings are isolated in `artifacts/settings` through `SHIPPING_PRINTER_DATA`; the original environment value is restored when the runner finishes.

Checks cover barcode generation/decoding, image transformation and import size limits, label dimensions/colors, settings validation, concurrent saves and complete printer-selection persistence, Windows PDF rendering, file-handle release, generic TSPL, independent inflation of compressed mode-3 payloads, bitmap polarity/padding, BLE packet limits, typed addresses, and unambiguous channel selection. These checks do not enumerate printers, send print jobs, access a network, discover Bluetooth devices, or establish physical printer compatibility.

The layout checks import a generated 4×6 PDF with artwork at every edge and verify the full-size raster at 203 and 300 DPI, including the generic TSPL and OFFNOVA payloads. They also cover the one-time legacy-margin migration, custom margins, and preservation of source whitespace and aspect ratio.

Optionally append paths to single-page 4×6 PDFs with borders at the page edges after the artifacts directory. These local-only examples are checked for the old inset and the corrected output at both resolutions; before/after PNGs go to the artifacts directory. Do not commit private labels or generated images containing shipment details.

## Hosted runners

The suite passes with a zero process exit code on the release maintainer's Windows desktop. On GitHub's Windows Server 2022 and 2025 images, all assertions complete but the process exits with native error `0xC000000D` after WPF shutdown. The failing native component has not been identified; a passing JSON report is not treated as a successful process run.

Hosted CI therefore verifies compilation and packaging with `package.ps1 -SkipChecks`. Before a release, run `package.ps1` without that switch on a Windows desktop and require all checks and the process exit code to pass. The default local build and package commands still run every check.
