# omen-helper: project context (reverse engineering notes)

## Purpose

Build a small replacement utility for **OMEN Gaming Hub** performance controls on an **OMEN Transcend 14** without automating the OMEN UI.

## Goal

- Prefer a **direct firmware/driver control path** (BIOS/WMI) where possible.
- Use HP background named pipes only when needed, and assume **reply/state channels may be gated**.
- Keep the project safe: only expose controls we can confirm via readback/behavior.

## What we’ve reverse engineered so far

### A) BIOS / WMI (firmware-backed) commands

HP calls these via `ExecuteBiosWmiCommandThruDriver` (visible in OMEN BG logs). We observe a vendor “command” plus a “commandType” subcommand.

- **Performance mode (Transcend 14 verified)**:
  - Set “platform performance mode”: `command=131080, commandType=26, input=[255,<modeByte>,0,0]`
  - Observed mode bytes on this machine:
    - Eco (`256`) -> `48` (L2)
    - Default/Balanced (`0`) -> `48` (L2)
    - Performance (`1`) -> `49` (L7)
    - Unleashed (`4`) -> `4`
  - Set GPU power behavior used by each mode: `command=131080, commandType=34, input=[tgpEnable, ppabEnable, dState, gps]`
    - Observed per-mode payloads:
      - Eco -> `0,0,1,87`
      - Default -> `0,1,1,87`
      - Performance -> `1,1,1,87`
      - Unleashed -> `1,1,1,87`
  - Set concurrent TDP/TPP (observed only for Performance/Unleashed): `command=131080, commandType=41, input=[255,255,255,45]`

- **Max fan**:
  - Write: `command=131080, commandType=39, input=[mode]`
  - Read: `command=131080, commandType=38`

- **Graphics mode (MUX-ish; reboot required on this platform)**:
  - Read: `command=1, commandType=82` (`GetGraphicsMode()`)
  - Write: `command=2, commandType=82, input=[mode,0,0,0]` (`SetGraphicsMode(...)`)
  - Enum mapping used by HP code: `Hybrid=0`, `Discrete=1`, `Optimus=2`, `UMAMode=3`
  - On this machine, HP runtime helper reports `SupportedModes=6` (Hybrid + Discrete), but attempts to set `Discrete` have returned `255` and stayed on Hybrid.

- **System design data / capability bits**:
  - Read: `command=131080, commandType=40` (cached at `HKCU\Software\HP\OMEN Ally\Settings\SystemDesignData`)

- **OEM idle / periodic polling (meanings still being nailed down)**:
  - Read 128-byte “status/settings blob” (selector appears to be 4 bytes): `command=131080, commandType=45, input=[0,0,0,0]` (out=128)
  - Read 4-byte “temperature-ish” value: `command=131080, commandType=35, input=[0,0,0,0]` (out=4)
    - HP’s `OmenHsaClient.DtGetTemperature()` uses `commandType=35` and reads `return[0]` as the value (that method uses input byte `1`).
  - Write 128-byte “restore/apply settings blob”: `command=131080, commandType=46` (in=128 out=4)

Notes:
- `returnCode=255` appears to be a generic failure/fallback in HP’s `OmenHsaClient` wrappers when the BIOS/WMI path fails.
- Hidden platform modes (`Cool`, `Quiet`) exist and show up in BIOS readback even if the current OMEN UI hides them.

### B) HP background named pipes (writes work; replies are gated)

This is how the OEM overlay drives performance controls today.

- Write pipe: `PerformanceControlFg<SessionId>` (XML; HP `PipeClientV2`)
  - Observed commands:
    - `21` = set performance mode (`PerformanceMode` enum; includes `Cool` and `Quiet`)
    - `23` = set thermal mode (packed as `CurrentPerformanceMode * 1000 + ThermalControl`)
    - `25` = set legacy fan mode
    - `29` = request initialization/state

- Why “state return” doesn’t work in a standalone app:
  - OMEN BG sends init/update to overlay/widget pipes using `PipeClientV3`, which verifies the receiver’s process signature.
  - An unsigned app can send writes to the BG, but cannot receive those trusted replies.

### C) Telemetry

- Preferred: `LibreHardwareMonitor` + Windows perf counters (no HP dependencies).
- HP monitor registration pipes work on this machine, but depend on HP background endpoints being alive.

## Practical workflow / artifacts

- OMEN BG logs: `%LOCALAPPDATA%\Packages\AD2F1837.OMENCommandCenter*\LocalCache\Local\HPOMEN\HPOMENBG_*.log`
- Tail & decode BIOS/WMI calls: `tools\tail-omen-bios.ps1`
  - Default mode follows **new entries only**; pass `-FromStart` to replay the whole file.
  - Output includes `[PID:TID]` to avoid mixing interleaved calls.

## Current project stance

- Prefer the firmware path for performance modes and any safe reads.
- Treat BIOS/WMI “unknown” commandTypes as **TBD** until we can correlate to HP code or confirm behavior.
- Don’t rely on `PipeClientV3` reply channels for correctness; build our own readback (BIOS/WMI reads, inferred state, telemetry).

