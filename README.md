# THIS IS A WORK IN PROGRESS.
# THE WIMI/BIOS INTERFACE WORKS WITH AN OMEN TRANSCEND 14 2025
# READ THE AI GENERATED SLOP BELOW AT YOUR OWN PERIL.

# Omen Helper

BIOS/WMI-only replacement utility for OMEN performance controls on the OMEN Transcend 14.

The app is a WinForms tool that talks directly to HP BIOS/WMI firmware surfaces instead of automating the OMEN Gaming Hub UI.

## Getting Started

### Prerequisites

- Windows 10 or Windows 11
- Visual Studio 2022, or the .NET SDK installed at `C:\Program Files\dotnet\dotnet.exe`
- .NET Framework 4.8 developer targeting pack
- HP SystemOptimizer installed at `C:\Program Files\HP\SystemOptimizer`
- Administrator privileges when running the app

Bundled third-party DLLs required by the app are checked in under `src/OmenHelper/bin_check/`. The only external install path the project expects by default is `HpSystemOptimizerRoot`.

### Clone and open

1. Clone the repository.
2. Open `OmenHelper.sln` in Visual Studio, or use the command line from the repo root.
3. If your HP install is in a different location, update `HpSystemOptimizerRoot` in `Directory.Build.props`.

### Build

From the repo root:

```bat
dotnet build OmenHelper.sln
```

Or run the local wrapper:

```bat
build.bat
```

The current build output is:

```text
src\OmenHelper\bin\Debug\net48\OmenHelper.exe
```

### Run

- Start `src\OmenHelper\bin\Debug\net48\OmenHelper.exe` from an elevated prompt.
- Or use `build.bat`, which builds and launches the latest Debug build.

## Development Notes

- The source tree lives under `src/OmenHelper/`.
- `MainForm` is the app shell, while orchestration lives under `src/OmenHelper/Application/` and low-level firmware access lives under `src/OmenHelper/Infrastructure/`.
- `BiosCommandCatalog`, `PerformanceModeFirmwareMap`, and `GraphicsSupportPolicy` hold the shared command and display mappings.
- Fan RPM telemetry is polled from BIOS/WMI and shown in the UI.

## Repository Layout

```text
src/
  OmenHelper/
    App/
    Application/
    Domain/
    Infrastructure/
    Presentation/
README.md
OmenHelper.sln
Directory.Build.props
```

## Notes

- This project intentionally avoids HP background named-pipe support.
- Performance mode write paths are conservative and should only be exposed when confirmed by BIOS/WMI behavior.
- Graphics mode support is gated by BIOS system design data readback.
- The normal diagnostics window is safe for end users. Raw firmware probe buttons are hidden in non-Debug builds unless `OMENHELPER_ENABLE_DEVTOOLS=1` is set in the environment.
