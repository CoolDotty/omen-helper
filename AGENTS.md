# omen-helper: project context (reverse engineering notes)

## Purpose

Build a small replacement utility for **OMEN Gaming Hub** performance controls on an **OMEN Transcend 14** without automating the OMEN UI.

## Goal

- Use **direct firmware/driver control path** (BIOS/WMI) when it is truly available.
- Do not ship HP background named pipe support in the app.
- Keep the project safe: only expose controls we can confirm via readback/behavior.

## What we've reverse engineered so far

### A) BIOS / WMI (firmware-backed) commands

HP calls these via `ExecuteBiosWmiCommandThruDriver` (visible in OMEN BG logs). We observe a vendor "command" plus a "commandType" subcommand.

- **Performance mode (Transcend 14 observed in OMEN BG logs)**:
  - Set "platform performance mode": `command=131080, commandType=26, input=[255,<modeByte>,0,0]`
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
  - On this machine, the earlier helper-flag interpretation that implied discrete support was wrong.
  - Treat the confirmed user-facing graphics modes as `Integrated`/`UMA` and `Hybrid`.
  - Do not expose `Discrete` as a confirmed mode on this laptop unless future readback proves it works.

- **System design data / capability bits**:
  - Read: `command=131080, commandType=40` (cached at `HKCU\Software\HP\OMEN Ally\Settings\SystemDesignData`)

- **OEM idle / periodic polling (meanings still being nailed down)**:
  - Read 128-byte "status/settings blob" (selector appears to be 4 bytes): `command=131080, commandType=45, input=[0,0,0,0]` (out=128)
  - Read 4-byte "temperature-ish" value: `command=131080, commandType=35, input=[0,0,0,0]` (out=4)
    - HP's `OmenHsaClient.DtGetTemperature()` uses `commandType=35` and reads `return[0]` as the value (that method uses input byte `1`).
  - Write 128-byte "restore/apply settings blob": `command=131080, commandType=46` (in=128 out=4)

Notes:
- `returnCode=255` appears to be a generic failure/fallback in HP's `OmenHsaClient` wrappers when the BIOS/WMI command path fails.
- Hidden platform modes (`Cool`, `Quiet`) exist and show up in BIOS readback even if the current OMEN UI hides them.

#### Important: observed operational reality (Transcend 14, current app)

The shipped app is now BIOS/WMI-only and does not use HP background pipes.

Observed on this machine:
- BIOS/WMI remains the only supported control path in the app.
- Performance, thermal, fan, and graphics controls are exposed only when their BIOS readback or behavior can be confirmed.
- The prior HP pipe path was removed from the application.

Implication: treat BIOS/WMI performance/thermal writes as the only runtime path, and keep the UI conservative when write confirmation is weak.

### B) HP background named pipes

These are historical reverse-engineering notes only.

- `PerformanceControlFg<SessionId>` was the performance-control write pipe.
- OMEN BG used `PipeClientV3` for trusted reply/state channels.
- HP monitor registration pipes existed for telemetry.

They are no longer part of the app surface.

### C) Telemetry

- Preferred: `LibreHardwareMonitor` + Windows perf counters.
- Do not depend on HP monitor registration pipes in the app.

## Practical workflow / artifacts

- OMEN BG logs: `%LOCALAPPDATA%\Packages\AD2F1837.OMENCommandCenter*\LocalCache\Local\HPOMEN\HPOMENBG_*.log`
- Tail & decode BIOS/WMI calls: `tools\tail-omen-bios.ps1`
  - Default mode follows **new entries only**; pass `-FromStart` to replay the whole file.
  - Output includes `[PID:TID]` to avoid mixing interleaved calls.
- dll dlldumps of the OEM HP OMEN app are in `dlldumps\`
- an existing third party hp app OmenMon is in `OmenMon\`

## Current project stance

- Prefer firmware paths for **reads** and only expose firmware writes we can confirm via return codes/readback/behavior.
- Do not keep an HP pipe fallback in the app.
- Don't rely on `PipeClientV3` reply channels for correctness; build our own readback (BIOS/WMI reads, inferred state, telemetry).
