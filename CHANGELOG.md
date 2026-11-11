# Changelog

## 1.4.0 · 2026-11-10

- Added Delete-to-remove, multi-selection, undo/redo, and keyboard actions for copying, pasting files or images, exporting, cropping, and rotation.
- Added themed right-click menus to the label list and preview, with selection-aware actions and visible shortcut hints.
- Kept source files untouched, restored selection when undoing workspace changes, and preserved normal text-field editing shortcuts.
- Simplified workspace copy and suggested the label's name when exporting.
- Shortened the README around downloads, features, and setup while keeping basic build-from-source instructions.

## 1.3.2 · 2026-11-10

- Removed the default 2 mm added margin so matching 4×6 PDFs fill the preview, exported image, and print raster at their full size.
- Upgraded the old 2 mm default for saved 4×6 and 100×150 mm layouts without offsets. Other margins and offset-calibrated layouts are retained; newly selected margins persist.
- Show PDF page dimensions and the added print margin in the preview, with a shortcut to remove extra padding. Different page proportions continue to fit without cropping.

## 1.3.1 · 2026-10-26

- Added a Windows x64 installer with Start menu integration, an optional desktop shortcut, and uninstall support. No administrator access or separate .NET installation is needed.
- Kept the portable ZIP and added SHA-256 checksums for both downloads.
- Removed the mobile package and obsolete screenshot from the Windows source tree. Packaging now cleans up its temporary files after each build.

## 1.3.0 · 2026-10-25

- Redesigned workspace with live label thumbnails, a focused preview canvas, and compact print settings.
- Printer destination and copies alongside the print action, with consistent light and charcoal appearances.
- Consistent barcode, crop, and printer dialogs with fixed action areas and improved keyboard focus states.

## 1.2.0 · 2026-10-25

First public release.

- Neutral dark appearance with refined controls and clearer interaction states.
- Matching native title bars and an original app icon.
- Bounded image decoding for unusually narrow images and safer settings saves across app instances.
- PDF and image import, label crop and rotation, barcode generation and reading, and PNG or thermal-command export.
- Dedicated OFFNOVA N-6140 Bluetooth LE and Bluetooth Classic profiles, alongside Windows driver and generic TSPL printing.
- Portable Windows x64 package with the .NET runtime included.
- Build and packaging scripts, offline checks, and documentation for printer setup and contributions.

The OFFNOVA Bluetooth LE workflow is user-reported working. Other hardware and connection paths require device testing.
