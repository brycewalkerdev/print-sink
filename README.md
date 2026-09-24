# PrintSink

PrintSink is a packaged Windows virtual printer built on the Print Support App v4 surface. It is a modern software printer: no legacy driver, no port monitor, and no INF.

The **PrintSink - Clipboard (Image)** queue is included in this fork. Printing to it converts the job to PWG Raster, decodes all pages, and stacks them vertically into one Windows clipboard image. It is intended for pasting into chat, documents, and image editors. Each page is sized to at most 2,400 pixels on its longest side before stacking. Very long documents are scaled down to fit a 24-megapixel, 30,000-pixel-tall clipboard image.

The app installs PrintSink queues for PDF, XPS/OXPS, PostScript, cloud/custom routing, PWG Raster, PCLm, and Clipboard (Image). The foreground app is WinUI 3 with Microsoft.UI.Reactor. Background print activations run through CsWinRT components, while the shared routing and validation logic lives in `PrintSink.Core`.

## Requirements

- Windows 11 24H2, build 26100 or later.
- .NET SDK 10, pinned by `global.json`.
- Visual Studio 2026 with Windows App SDK and single-project MSIX tooling.
- MSBuild on `PATH` for Visual Studio-style builds.

## Build

Build the full solution:

```powershell
.\build.ps1
```

Run tests:

```powershell
.\test.ps1 -Configuration Debug -Platform x64 -NoBuild
.\test-app.ps1 -Configuration Debug -Platform x64 -NoBuild
```

`dotnet build` is useful for managed checks. The full solution uses MSBuild because `PrintSink.Xps` is a C++/WinRT project.

Run the packaged app from the project profile:

```powershell
dotnet run --project src\PrintSink.App
```

Run the CLI:

```powershell
dotnet run --project src\PrintSink.Cli -- --help
```

For unattended print-stack checks, the packaged alias can skip foreground Job UI:

```powershell
printsink-app.exe --disable-job-ui
printsink-app.exe --enable-job-ui
```

More detail lives in [docs/BUILD.md](docs/BUILD.md) and [docs/TESTING.md](docs/TESTING.md).

## Clipboard queue

Install the package, open the PrintSink management screen, install the **PrintSink - Clipboard (Image)** queue, and select that printer from any application's print dialog. Clipboard jobs run immediately without opening PrintSink's extra job preview, even when preview is enabled for other queues. The print workflow delegates the clipboard write to a short-lived desktop process in your interactive user session. The decoder supports compressed RaS2 and uncompressed RaS3 chunky, 8-bit grayscale, RGB, and RGBA raster output.
