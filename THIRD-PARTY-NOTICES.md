# Third-party components

Shipping Printer's own code and artwork are covered by the root MIT license. Its dependencies retain the following terms.

| Component | License and source |
| --- | --- |
| ZXing.Net 0.16.11 | Apache-2.0, Michael Jahn and ZXing contributors. [Source](https://github.com/micjahn/ZXing.Net); license in `licenses/ZXing-Apache-2.0.txt`. |
| Microsoft .NET and Windows Desktop Runtime | The portable package includes the runtime packages' license texts and .NET third-party notices in `licenses/`. [Runtime source](https://github.com/dotnet/runtime); [WPF source](https://github.com/dotnet/wpf). |
| C#/WinRT runtime (`WinRT.Runtime.dll`) | MIT, Microsoft Corporation. [Source](https://github.com/microsoft/CsWinRT); license in `licenses/CsWinRT-MIT.txt`. |
| Windows SDK .NET projections (`Microsoft.Windows.SDK.NET.dll`) | Microsoft Windows SDK terms. [Package license](https://aka.ms/WinSDKLicenseURL); SDK license and notices in `licenses/Windows-SDK-License.rtf` and `licenses/Windows-SDK-Third-Party-Notices.rtf`. |

`scripts/package.ps1` copies the license files from the exact .NET runtime package versions used for that release. Runtime versions are recorded in `ShippingPrinter.runtimeconfig.json` in the portable package.

Original mobile application packages, code, and artwork are not distributed in this repository or its releases.
