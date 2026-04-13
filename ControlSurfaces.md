# OMEN Transcend 14: Feature Inventory + Control Surfaces (Windows)

This file is split into:
- **Feature inventory**: what the official OMEN app UI exposes
- **Control surfaces**: the actual programmatic interfaces we can drive (or are blocked from driving)

## Feature Inventory (Official OMEN App UI)

- Toggle performance modes
  - Eco
  - Balanced
  - Performance
  - Unleashed
- Adjust fan speed
  - Eco mode
    - Cannot set fan speed
  - balanced or performance mode
    - Auto or Max
  - Auto, Max, or Manual.
    - Manual lets you adjust the fan curve
      - increments of 5 degrees celsius between 50 and 90 degrees
      - between 0% and 100% fan speed
      - Curved linearly between increments
      - Choice of adjust the CPU, GPU, or chassis temperature curve
- Overclock the gpu
  - Set core offset and memory offset
- Set power targets
  - Smart performance gain
    - On or off
    - between 0w or 15w
  - Maximum battery drain
    - Between 10% to 40%
  - PL1
    - Between 25W to 65W
  - PL2
    - Between 25W to 77W
  - PL4
    - Between 135W ad 168W
- Adjust keyboard lighting
  - 4 RGB keyboard zones
    - Left
    - Middle
    - Right
    - WASD
  - Windows Dynamic Lighting compatible?
- Change gpu mode
  - Hybrid mode: use both integrated and discrete graphics depending on the application
  - Integrated Graphics Only: Turn off your discrete GPU and use onboard GPU only for maximum battery life
  - Changing requires a reboot to take effect
- Key assignment
  - Rebind the keyboard
  - Out of scope for now

## Control Surfaces (Reverse Engineered)

### A) BIOS / WMI Commands (Preferred When Available)
These can be called directly from our app without OMEN pipes, but require whatever HP firmware/driver plumbing is present on the system.

- **Performance modes (Transcend 14 verified)**:
  - Set “platform performance mode”: `command=131080, commandType=26, input=[255,<modeByte>,0,0]`
    - Observed mode bytes on this machine:
      - Eco (256) -> `48` (L2)
      - Default/Balanced (0) -> `48` (L2)
      - Performance (1) -> `49` (L7)
      - Unleashed (4) -> `4`
  - Set GPU power behavior used by each mode:
    - `command=131080, commandType=34, input=[tgpEnable, ppabEnable, dState, gps]`
    - Observed per-mode payloads:
      - Eco -> `0,0,1,87`
      - Default -> `0,1,1,87`
      - Performance -> `1,1,1,87`
      - Unleashed -> `1,1,1,87`
  - Set concurrent TDP/TPP (observed only for Performance/Unleashed):
    - `command=131080, commandType=41, input=[255,255,255,45]`
  - Status: implemented in `omen-helper` as the primary control path; no OMEN background pipes required for these modes on this machine.

- **Graphics mode (MUX-ish)**:
  - Read: `command=1, commandType=82` (`GetGraphicsMode()`)
  - Write: `command=2, commandType=82, input=[mode,0,0,0]` (`SetGraphicsMode(...)`)
  - Modes enum: `Hybrid=0`, `Discrete=1`, `Optimus=2`, `UMAMode=3`
  - This machine:
    - HP helper reports supported bits `SupportedModes=6` (Hybrid + Discrete), `SupportedUMAmode=false`, reboot required.
    - Attempting `Discrete` currently returns `255` (failure) and stays on `Hybrid`.

- **Max fan**:
  - Write: `command=131080, commandType=39, input=[mode]` (`SetMaxFan(...)`)
  - Read: `command=131080, commandType=38`

- **System design data / capability bits**:
  - Read: `command=131080, commandType=40`
  - Cached at: `HKCU\\Software\\HP\\OMEN Ally\\Settings\\SystemDesignData`

Known limitation: we do not yet have confirmed BIOS/WMI command IDs for “performance mode” and “thermal mode” equivalents on this platform.

### B) HP Background Pipes (Works For Writes; Replies Are Gated)
This is how the OMEN overlay controls performance modes today. It works for our standalone app *only as long as HP background endpoints exist*.

- **Performance mode / thermal / legacy fan writes**:
  - Pipe: `PerformanceControlFg<SessionId>`
  - Serializer: XML (`PipeClientV2`)
  - Payload: `PerseusRevMsg { SendParameter = PerformanceControlMsg { Command = <int>, Data = <object> } }`
  - Observed commands:
    - `21` = set performance mode (`PerformanceMode` enum; includes `Cool` and `Quiet` even if UI hides them)
    - `23` = set thermal mode (packed as `CurrentPerformanceMode * 1000 + ThermalControl`)
    - `25` = set legacy fan mode
    - `29` = request initialization/state

- **Why state return does not work in our app**:
  - Background sends init/update to overlay/widget pipes using `PipeClientV3` (JSON + signature verification of the receiver process).
  - Because our app is not HP-signed, we cannot receive those PipeV3 replies.
  - Result: we can write mode changes, but we must build our own readback (BIOS/WMI reads, inferred state, etc.).

### C) Telemetry
- We can get telemetry via:
  - `LibreHardwareMonitor` + Windows perf counters (preferred, no HP dependencies)
  - HP performance monitor registration pipes (works today, but depends on HP background being up)

## Current Operational Dependency
- Performance modes: no longer require OMEN to be launched (firmware path).
- Telemetry and some advanced features may still depend on HP background endpoints if enabled (pipes/PerfMonitor registration).
