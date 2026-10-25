# Printer setup and troubleshooting

[Back to the README](../README.md)

Shipping Printer can use an installed Windows print queue or connect directly to a compatible Bluetooth printer. Choose the connection in **Printer setup**. Saving a setup or checking a connection does not print anything.

## Choose a connection

| Connection | Use it for |
| --- | --- |
| OFFNOVA N-6140 · Bluetooth LE | The N-6140 entry whose name ends in `-LE`. This is the user-reported working wireless path. |
| OFFNOVA N-6140 · Bluetooth Classic | A paired N-6140 exposing a Bluetooth Serial Port service. Physical output has not been independently verified. |
| Windows printer driver | An installed Windows printer queue with the manufacturer's driver. |
| Direct TSPL · installed printer | A printer that accepts raw TSPL commands through a Windows queue. |
| Direct TSPL · other Bluetooth LE | A TSPL-compatible printer with a known writable GATT channel. |

A printer name alone does not establish command compatibility. The OFFNOVA profiles use compressed raster commands; the generic TSPL paths use uncompressed commands.

## OFFNOVA N-6140: Bluetooth LE

1. Turn on the printer and Windows Bluetooth. Keep the printer nearby and disconnect its phone app.
2. Open **Printer setup** and choose **OFFNOVA N-6140 · Bluetooth LE**.
3. Choose **Find OFFNOVA printer** and select the entry ending in `-LE`.
4. Wait for the connection check. If needed, choose **Check connection** again.
5. When a print channel is selected, choose **Use this printer**.
6. Set **203 DPI**, match the loaded paper size, and print one test label.

Prior Windows pairing is usually unnecessary for BLE. The app prefers [AiYin's documented print-write channel](https://iprtapp.com/en/psdk/programming/openharmony/) when present, otherwise a single channel that supports acknowledged writes. If several possible channels remain, **Connection details** requires an explicit choice. Use the printer's documentation rather than trying channels at random.

Before printing a batch, check the test label's dimensions, orientation, and barcode with a scanner. A successful transfer reports that Windows accepted the bytes, not that the printer produced a usable label.

## OFFNOVA N-6140: Bluetooth Classic

1. Choose **OFFNOVA N-6140 · Bluetooth Classic**.
2. Open **Windows Bluetooth settings** and pair the printer entry without the `-LE` suffix. The manufacturer's documented PIN is `0000` if Windows asks.
3. Return to the app, find the printer again, and check the connection.
4. Choose **Use this printer** when Windows reports pairing and one Serial Port service.

This path uses Bluetooth RFCOMM directly. As an alternative, [OFFNOVA's manual](https://cdn.shopify.com/s/files/1/0428/2053/4426/files/Manual_N6140_6240BT.pdf?v=1749794801) describes installing its Windows driver on the Bluetooth outgoing COM port. Use **Windows printer driver** to print through that queue.

## Windows queues and generic TSPL

For **Windows printer driver**, install the manufacturer's driver first. Match the stock size in the app and Windows printer preferences. The Windows print dialog opens before submission.

Direct TSPL sends commands without asking a driver to render the label. Use it only when the printer supports TSPL. A writable Bluetooth channel or installed queue does not guarantee that it accepts these commands.

**Export → Save thermal commands** saves the payload for the selected profile without sending it. OFFNOVA profiles export compressed mode 3; generic TSPL exports mode 0.

## Label settings

| Setting | Effect |
| --- | --- |
| Paper size | Sets the physical output size. Match the stock in the printer. |
| Resolution | Selects 203 or 300 DPI. Match the printer's actual resolution. |
| Margin | Leaves space around the fitted label image. |
| Black threshold | Controls which pixels become black in monochrome output. |
| Copies | Repeats the selected label, from 1 to 100 copies. |
| Gap | Sets the space between labels for direct thermal printing. |
| Density and speed | Controls direct thermal output where supported by the printer. |
| X and Y offsets | Shifts printed output for both driver and direct thermal paths. The preview stays centered. |

Cropping and rotation affect the selected label. **Reset** restores its original imported image. The app fits the image inside the chosen paper size; it does not infer the intended paper stock from a shipping carrier.

## When something goes wrong

**The printer does not appear.** Move it closer, disconnect the phone app, confirm Windows Bluetooth is on, and scan again. Short scans can miss a device. Check both BLE and Classic modes if the printer exposes both names.

**The connection check fails.** Close other apps connected to the printer and retry. For Classic, confirm pairing in Windows first. For BLE, inspect **Connection details** for service and write-channel results.

**A job stops partway through.** Check the printer before trying again. The app does not automatically retry a partially sent job because that could print duplicate or incomplete labels. Cancellation may leave data in the printer's buffer.

**The label is clipped, too small, or sideways.** Check paper size and resolution, rotate or crop the source, then adjust margins. With a Windows driver, confirm its stock size too. Use the printer's own instructions for media calibration.

**The barcode will not scan.** Use the printer's actual DPI, preserve clear space around the barcode, and avoid excessive density. Start with a sharp source image or generate the code in the app.

**Settings need a fresh start.** Close the app and rename `%LocalAppData%\ShippingPrinter.Windows\settings.json` to keep a backup. The next launch uses defaults. To test separately, launch with `SHIPPING_PRINTER_DATA` set to a different folder.

If the problem persists, [open an issue](https://github.com/Mxlted/shipping-printer-windows/issues). Include the app and Windows versions, printer model, connection type, paper size, and exact error. **Save connection details** can help diagnose service selection; remove device identifiers and personal data before attaching the file. See [Contributing](../CONTRIBUTING.md) for hardware reports.
