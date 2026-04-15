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
  - No confirmed BIOS/WMI readback exists for the current performance mode itself; treat the mode as write-only unless a future read path is verified.
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
  - The BIOS/WMI graphics switch path is confirmed working on this machine.
  - Treat the confirmed user-facing graphics modes as `Integrated`/`UMA` and `Hybrid`.
  - Do not expose `Discrete` as a confirmed mode on this laptop unless future readback proves it works.
  - Gate graphics-mode UI from BIOS system-design-data support bits only, not from HP helper classes or other proprietary runtime support flags.
  - Keep the normal UI refresh path and the diagnostics refresh path aligned: both must re-read BIOS graphics support so the main window does not show stale `0x00` support bits while diagnostics shows newer BIOS state.

- **System design data / capability bits**:
  - Read: `command=131080, commandType=40` (cached at `HKCU\Software\HP\OMEN Ally\Settings\SystemDesignData`)

- **OEM idle / periodic polling / fan minimum policy**:
  - Read 128-byte blob: `command=131080, commandType=45, input=[0,0,0,0]` (out=128)
    - On this machine the first bytes look like temperature/fan-related sensor values, not a mode ID.
    - The leading values appear to be roughly half-resolution temperature/fan readings (for example, raw 25 ≈ 50°C, close to a ~52°C UI reading).
  - Read 4-byte "temperature-ish" value: `command=131080, commandType=35, input=[0,0,0,0]` (out=4)
    - HP's `OmenHsaClient.DtGetTemperature()` uses `commandType=35` and reads `return[0]` as the value (that method uses input byte `1`).
  - Write 128-byte fan minimum/apply blob: `command=131080, commandType=46` (in=128 out=4)
    - We currently treat bytes `0` and `1` as CPU/GPU fan minimum targets in RPM/100.
    - Observed hard minimums: Eco `0`, Balanced `2200`, Performance `2800`, Unleashed `2800`.
    - The OEM app appears to use additional software fan-curve logic, but the BIOS write is the key firmware-backed minimum/target step.

Notes:
- `returnCode=255` appears to be a generic failure/fallback in HP's `OmenHsaClient` wrappers when the BIOS/WMI command path fails.
- Hidden platform modes (`Cool`, `Quiet`) exist and show up in BIOS readback even if the current OMEN UI hides them.

#### Important: observed operational reality (Transcend 14, current app)

The shipped app is now BIOS/WMI-only and does not use HP background pipes.

Observed on this machine:
- BIOS/WMI remains the only supported control path in the app.
- Performance, thermal, fan, and graphics controls are exposed only when their BIOS readback or behavior can be confirmed. Graphics switching is now confirmed on this machine through BIOS/WMI.
- The prior HP pipe path was removed from the application.
- A real bug we hit: diagnostics re-polled BIOS graphics capability bits, but the normal refresh path did not. That let the main UI keep stale unsupported graphics state until the refresh path was fixed to re-read BIOS graphics support too.

Implication: treat BIOS/WMI performance/thermal writes as the only runtime path, and keep the UI conservative when write confirmation is weak. For graphics, only surface modes that have been confirmed by BIOS readback or observed rebooted behavior.

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
- Performance mode is currently write-only in BIOS/WMI; do not treat `CurrentMode` as verified readback unless a real read path is added later.
- Do not keep an HP pipe fallback in the app.
- Don't rely on `PipeClientV3` reply channels for correctness; build our own readback (BIOS/WMI reads, inferred state, telemetry).
- For graphics support, BIOS system-design-data bits are the source of truth for UI gating and diagnostics messaging.
- When the UI offers a manual refresh, refresh graphics capability bits from BIOS in that path as well; do not assume startup state is still current.
- After performance mode or thermal/fan mode changes, reapply the observed fan minimum blob (`131080 / 46`) so the firmware does not get left in a null fan state.
