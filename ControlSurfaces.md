# OMEN Transcend 14: Feature Inventory + BIOS Control Surfaces

This file tracks the app-visible controls that are still supported in the BIOS-only build.

## Feature Inventory

- Toggle performance modes
  - Eco
  - Balanced
  - Performance
  - Unleashed
- Adjust fan behavior
  - Auto
  - Max
- Change GPU mode
  - Hybrid mode
  - Integrated Graphics Only (UMA)
  - Changing requires a reboot to take effect

## Control Surfaces

### A) BIOS / WMI Commands
These are the only control paths used by the app now.

- **Performance modes**
  - Set platform performance mode: `command=131080, commandType=26, input=[255,<modeByte>,0,0]`
    - Observed mode bytes on this machine:
      - Eco (256) -> `48`
      - Default/Balanced (0) -> `48`
      - Performance (1) -> `49`
      - Unleashed (4) -> `4`
  - Set GPU power behavior used by each mode:
    - `command=131080, commandType=34, input=[tgpEnable, ppabEnable, dState, gps]`
    - Observed per-mode payloads:
      - Eco -> `0,0,1,87`
      - Default -> `0,1,1,87`
      - Performance -> `1,1,1,87`
      - Unleashed -> `1,1,1,87`
  - Set concurrent TDP/TPP:
    - `command=131080, commandType=41, input=[255,255,255,45]`

- **Graphics mode**
  - Read: `command=1, commandType=82`
  - Write: `command=2, commandType=82, input=[mode,0,0,0]`
  - Enum mapping:
    - `Hybrid=0`
    - `Discrete=1`
    - `Optimus=2`
    - `UMAMode=3`
  - On this machine, the earlier helper-flag interpretation that implied discrete support was wrong.
  - Treat the confirmed user-facing graphics modes as `Hybrid` and `Integrated Graphics Only (UMA)`.
  - Do not expose `Discrete` as a confirmed mode on this laptop unless a future BIOS readback proves it works.

- **Max fan**
  - Write: `command=131080, commandType=39, input=[mode]`
  - Read: `command=131080, commandType=38`

- **System design data / capability bits**
  - Read: `command=131080, commandType=40`
  - Cached at: `HKCU\\Software\\HP\\OMEN Ally\\Settings\\SystemDesignData`

## Current App Surface

- Performance modes: direct BIOS/WMI only, with safe readback where available.
- Graphics mode: direct BIOS/WMI only.
- Fan control: BIOS-backed max fan on/off only.
- Telemetry: not backed by HP pipes in the app.

## Historical Notes

The HP background pipe paths that were reverse engineered during research are no longer part of the app surface. They remain documented elsewhere in the repo for reference, but the build does not use them.
