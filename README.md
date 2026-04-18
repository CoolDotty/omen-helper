# Omen Helper

BIOS/WMI-only replacement utility for OMEN performance controls on the OMEN Transcend 14.

The app is a WinForms tool that talks directly to HP firmware/driver surfaces instead of automating the OMEN Gaming Hub UI.

## Getting Started

### Prerequisites

- Windows 10 or Windows 11
- Visual Studio 2022, or the .NET SDK installed at `C:\Program Files\dotnet\dotnet.exe`
- .NET Framework 4.8 developer targeting pack
- HP SystemOptimizer installed at `C:\Program Files\HP\SystemOptimizer`
- Administrator privileges when running the app

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
- `MainForm` is the app shell, while the firmware and telemetry logic is in `src/OmenHelper/Services/`.
- `BiosCommandCatalog`, `PerformanceModeFirmwareMap`, and `GraphicsSupportHelper` hold the shared command and display mappings.
- LibreHardwareMonitor is currently referenced from the local `bin_temp5` cache in this workspace. If you are bringing up the repo on another machine, you may need to restore those binaries or convert the project back to NuGet package references.

## Repository Layout

```text
src/
  OmenHelper/
    App/
    Forms/
    Infrastructure/
    Models/
    Services/
README.md
OmenHelper.sln
Directory.Build.props
```

## Notes

- This project intentionally avoids HP background named-pipe support.
- Performance mode write paths are conservative and should only be exposed when confirmed by BIOS/WMI behavior.
- Graphics mode support is gated by BIOS system design data readback.
