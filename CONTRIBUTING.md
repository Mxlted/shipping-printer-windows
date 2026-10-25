# Contributing

Useful contributions include reproducible bug reports, verified printer compatibility reports, and focused improvements to the Windows app.

For a larger feature or a new printer protocol, open an issue before writing the implementation so its scope can be discussed.

## Development

Build on Windows x64 with PowerShell 7 and the .NET 10 SDK:

```powershell
pwsh ./scripts/build.ps1
dotnet run --project ./src/ShippingPrinter.Windows -c Release
```

The build script restores locked dependencies, builds in Release, and runs the offline checks. To run only the checks:

```powershell
dotnet run --project ./tests/ShippingPrinter.Checks -c Release -r win-x64 --self-contained true -- ./artifacts
```

The runner writes its results and generated fixtures under `artifacts/`. It does not discover Bluetooth devices or print. For a separate app profile, set `SHIPPING_PRINTER_DATA` to a temporary directory before launching.

## Making a change

- Keep pull requests focused on one problem, and explain the behavior before and after the change.
- Preserve printer selections and settings when changing the interface. Check light and dark modes, keyboard focus, and the minimum window size.
- Add regression coverage when changing image handling, settings, or print commands. Verify payloads independently where possible rather than checking an encoder against itself.
- Keep printer writes sequential and explicit. Do not add automatic pairing or retry partially submitted jobs.
- Include the checks you ran and their results. Distinguish offline checks, live discovery, connection inspection, and physical printing.

Application code belongs in `src/ShippingPrinter.Windows`; offline checks belong in `tests/ShippingPrinter.Checks`. Packaging is handled by `scripts/package.ps1`. Generated files, local profiles, captures, original application packages, and investigation tooling do not belong in a pull request.

## Printer compatibility reports

A useful report includes:

- App version and Windows version.
- Exact printer model and hardware or firmware revision, if available.
- Connection type selected in the app.
- Paper dimensions, DPI, and whether the barcode scans.
- Whether discovery, connection inspection, and physical output each succeeded.
- A photo or sample document with names, addresses, tracking numbers, and device identifiers removed.

Describe the model and revision you tested without generalizing the result to every printer with the same brand name. Connection details exported by the app can help with Bluetooth reports, but review them for device identifiers before posting.

## License

Contributions are made under the project's [MIT license](LICENSE). Include attribution and license notices for any new dependency or externally sourced asset.
