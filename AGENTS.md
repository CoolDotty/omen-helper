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
  - Read 4-byte "temperature-ish" value: `command=131080, commandType=35, input=[0,1,0,0]` (out=4)
    - HP's `OmenHsaClient.DtGetTemperature()` uses `commandType=35` and reads `return[0]` as the value (the observed selector byte is `input[1] = 1`).
    - Current evidence on this machine: this sensor appears to be chassis temperature, not CPU temperature.
    - If the UI needs chassis temperature and LibreHardwareMonitor does not expose a chassis/board sensor, use this BIOS/WMI read as the fallback source.
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

## Codebase best practices

- Keep `Presentation` thin: forms and controls should only compose UI and forward actions.
- Put orchestration in `Application` services/controllers; keep one feature boundary per service.
- Keep raw BIOS/WMI transport in `Infrastructure/Bios/OmenBiosClient` only.
- Keep device/mode mappings and invariants in `Domain`; do not duplicate them in UI code.
- Use `GraphicsSupportPolicy` as the single graphics display-name/helper path.
- Gate graphics options from BIOS `SystemDesignData` support bits, not HP runtime helper flags.
- Expose only confirmed firmware-backed behavior; treat unsupported or hidden modes as unavailable until readback/behavior proves otherwise.
- Keep normal UI refresh and diagnostics refresh aligned so both read the same BIOS source of truth.
- Prefer async I/O for BIOS, file, and telemetry work, and keep DTOs/snapshots small and immutable where practical.
- Add regression tests around firmware maps, graphics support policy, fan limits, and diagnostics formatting.
- Preserve the BIOS/WMI-only runtime stance; do not reintroduce HP background pipe fallbacks or `PipeClientV3`-based correctness assumptions.

# OMEN Helper Research Notes

## Goal

Build a small replacement utility for OMEN Gaming Hub performance controls on an OMEN Transcend 14 without automating the OMEN UI.

## Implementation Status

The shipped app has since been cut to BIOS/WMI-only control paths. The pipe-related notes below are historical reverse-engineering notes, not a supported runtime path in the current build.

## What Was Verified Locally

- OMEN's overlay performance widget sends commands to `PerformanceControlFg<SessionId>`.
- The overlay expects replies on `HP.Omen.Overlay.Plugin.PerformanceControlPlugin<SessionId>`.
- The overlay registers CPU and GPU monitor streams through HP's performance-monitor registration pipes.
- HP also exposes some lower-level BIOS-backed functionality in `OmenHsaClient`, including `SetMaxFan()`.
- The standalone PoC can successfully send mode changes through `PerformanceControlFg<SessionId>`.
- OMEN Gaming Hub reflects those changes live, which confirms the control path is real.
- HP monitor telemetry works in the standalone PoC.
- The standalone PoC does not receive the overlay initialization/update payloads yet, even though control and telemetry work.
- The background implementation is now identified: `HP.Omen.Background.PerformanceControl.dll`.
- The missing overlay state-return path is not a pipe-name problem. It uses `PipeClientV3`, which verifies the target process signature before sending replies.

## Findings On This OMEN Transcend 14

- `Cool` and `Quiet` are real platform modes on this machine.
- BIOS readback reflects `Cool` and `Quiet`, even though OMEN's current UI does not expose them on this hardware.
- `Unleashed` maps to backend `L8`.
- `Extreme` exists in shared HP software capability structures, but it is not unlocked on this platform.
- This is consistent with the thinner OMEN Transcend 14 policy set being more restrictive than larger OMEN 16-class systems.
- The user-observed graphics switcher UI on this machine exposed `Integrated`/`UMA` and `Hybrid`, and HP required a restart for the change to take effect.
- HP's live runtime helper reported `SupportedModes = 6` and `SupportedUMAmode = False`, but that readout does not map cleanly to a working discrete mode on this machine.
- The earlier notes misread the helper flags as discrete support on this machine.
- Treat the confirmed user-facing graphics modes as `Integrated`/`UMA` and `Hybrid`.
- Do not infer `Discrete` support from `SupportedModes = 6` alone on this platform.
- A request that was previously labeled `Discrete` did not produce a confirmed discrete-only mode after reboot.
- In `OmenHsaClient`, `255` is the generic fallback error code when the BIOS/WMI command path fails and no valid result is returned.
- `DeviceModel.IsCurrentPlatformAtOrAfter("26C1") = False` on this machine, so HP keeps the graphics switcher on the legacy reboot-required path.

## Latest Diagnostics Snapshot

- `Initialized: False`
- `Available: False`
- `Last Initialization Payload: <none>`
- CPU and GPU monitor payloads are received continuously
- `SystemDesignData: 8C-00-35-01-03-A8-00-03-1E-00-0B-01-...`
- `ShippingAdapterPowerRating: 140`
- `IsBiosPerformanceModeSupport: False`
- `IsSwFanControlSupport: True`
- `IsExtremeModeSupport: True`
- `IsExtremeModeUnlock: False`
- `GraphicsMode: Hybrid`
- `MaxFan(BIOS): Off`

Interpretation:

- Control writes are working.
- HP monitor registration is working.
- The OMEN overlay state-return path is still incomplete in the standalone app.
- BIOS/platform reads are usable for support detection and some safe readback.

## Root Cause Of Missing State Return

- HP's background performance-control module listens for incoming control messages on:
  - `PerformanceControlFg<SessionId>`
  - `PowerOptionUiToPerformanceControlBg<SessionId>`
  in [Entry.cs](</C:/Users/dot/Downloads/hp4/HP.Omen.Background.PerformanceControl/HP.Omen.Background.PerformanceControl/Entry.cs:411>)
- The background accepts those incoming writes through `PipeServerV2`, which matches why a normal standalone app can successfully send commands.
- The background sends initialization and update messages back out to:
  - `HP.Omen.Overlay.Plugin.PerformanceControlPlugin<SessionId>`
  - `HP.Omen.PerformancePage.Widget<SessionId>`
  in [Entry.cs](</C:/Users/dot/Downloads/hp4/HP.Omen.Background.PerformanceControl/HP.Omen.Background.PerformanceControl/Entry.cs:1579>)
- Those reply sends go through `SendPipeMessageToModule(...)`, which uses `PipeClientV3`, in [Entry.cs](</C:/Users/dot/Downloads/hp4/HP.Omen.Background.PerformanceControl/HP.Omen.Background.PerformanceControl/Entry.cs:1754>)
- `PipeClientV3` validates the connected server process signature before sending, so an unsigned standalone app cannot impersonate OMEN's overlay receiver.
- This explains the observed asymmetry:
  - control path works
  - HP monitor telemetry works
  - overlay initialization and update replies do not arrive in the standalone app

## Graphics Switcher Findings

- HP ships a dedicated graphics-switch module:
  - [GraphicsSwitcherControlModel.cs](</C:/Users/dot/Downloads/hp5/HP.Omen.GraphicsSwitcherModule.Models/GraphicsSwitcherControlModel.cs:14>)
- Graphics mode is controlled through a direct BIOS/WMI call, not through the performance-control pipes:
  - `SetGraphicsMode(GraphicsSwitcherMode mode)` calls `BiosWmiCmd_Set(2, 82, [mode, 0, 0, 0])`
  - `GetGraphicsMode()` calls `OmenHsaClient.GetGraphicsMode()`
- Current graphics mode readback uses BIOS/WMI command `1`, type `82` in [OmenHsaClient.cs](</C:/Users/dot/Downloads/hp2/HP.Omen.Core.Common/HP.Omen.Core.Common/OmenHsaClient.cs:1434>)
- The mode enum is:
  - [GraphicsSwitcherMode.cs](</C:/Users/dot/Downloads/hp2/HP.Omen.Core.Model.DataStructure/HP.Omen.Core.Model.DataStructure.Modules.GraphicsSwitcher.Enums/GraphicsSwitcherMode.cs:3>)
  - `Hybrid = 0`
  - `Discrete = 1`
  - `Optimus = 2`
  - `UMAMode = 3`
- For this laptop's OMEN UI:
  - `Integrated` maps to `UMAMode`
  - `Hybrid` maps to `Hybrid`
- The earlier interpretation of `SupportedModes = 6` and `SupportedUMAmode = False` was not reliable for this machine.
- Treat the observed modes as `Hybrid` and `UMAMode`/`Integrated`.
- Do not use that helper readout alone to suppress `UMAMode` on this platform.
- A live runtime query against `HP.Omen.Core.Model.Device.Models.GraphicsSwitcherHelper` on this machine returned `SupportedModes = 6` and `SupportedUMAmode = False`, but that readout should not be treated as proof of discrete support here.
- The PoC should gate graphics buttons from HP's own support flags and only treat `BiosWmiCmd_Set(2, 82, ...)` as successful when `returnCode == 0`
- The official graphics-switch UI applies the BIOS change and then triggers a restart prompt/flow:
  - [GraphicsSwitcherControlViewModel.cs](</C:/Users/dot/Downloads/hp5/HP.Omen.GraphicsSwitcherModule.ViewModels/GraphicsSwitcherControlViewModel.cs:711>)
- There is also a newer `SetGraphicsModeV2(...)` variant:
  - [GraphicsSwitcherControlModel.cs](</C:/Users/dot/Downloads/hp5/HP.Omen.GraphicsSwitcherModule.Models/GraphicsSwitcherControlModel.cs:37>)
  - It toggles the high bit on newer no-reboot platforms.
  - Reboot policy is gated by [GraphicsSwitcherHelper.cs](</C:/Users/dot/Downloads/hp5/HP.Omen.GraphicsSwitcherModule.Models/GraphicsSwitcherHelper.cs:26>)
- On this machine, the practical verification path is:
  1. request graphics mode change
  2. reboot
  3. read back `GetGraphicsMode()`
  4. confirm `UMAMode` for Integrated or `Hybrid` for Hybrid

## UI Launch And UISync

- The official desktop performance UI sends a `UI launch` command before using the feature:
  - `Command = 14` in [FanControlModel.cs](</C:/Users/dot/Downloads/hp4/HP.Omen.PerformanceControlModule/HP.Omen.PerformanceControlModule.Models/FanControlModel.cs:354>)
- The background sets `UiLaunchFlag = true` on that command in [Entry.cs](</C:/Users/dot/Downloads/hp4/HP.Omen.Background.PerformanceControl/HP.Omen.Background.PerformanceControl/Entry.cs:589>)
- The official desktop module also hosts `HP.Omen.PerformanceControl.UISync` in [PerformanceControlViewModel.cs](</C:/Users/dot/Downloads/hp4/HP.Omen.PerformanceControlModule/HP.Omen.PerformanceControlModule.ViewModels/PerformanceControlViewModel.cs:2779>)
- The background writes to that sync pipe in [Entry.cs](</C:/Users/dot/Downloads/hp4/HP.Omen.Background.PerformanceControl/HP.Omen.Background.PerformanceControl/Entry.cs:1813>)
- `UISync` appears to be a desktop-UI refresh signal, not the same thing as overlay init/update payload delivery.

## Concrete File References

- Pipe name constants:
  - [CommonStr.cs](</C:/Users/dot/Downloads/hp2/HP.Omen.Core.Common/HP.Omen.Core.Common.PipeUtility/CommonStr.cs:43>)
  - `PerformanceControlFgStr = "PerformanceControlFg"`
  - `PerformanceControlBgStr = "PerformanceControlBg"`
  - `PerformanceControlPerformanceMonitorBgStr = "PerformanceControlPerformanceMonitorBg"`
  - `PowerOptionPipeStr2 = "PowerOptionUiToPerformanceControlBg"`
  - `PerformanceControlPluginStr = "HP.Omen.Overlay.Plugin.PerformanceControlPlugin"`
  - `PerformanceControlUISyncPipeStr = "HP.Omen.PerformanceControl.UISync"`

- Command enum:
  - [PerformanceControlCmd.cs](</C:/Users/dot/Downloads/hp2/HP.Omen.Core.Common/HP.Omen.Core.Common.Enums/PerformanceControlCmd.cs:26>)
  - `21 = SetModeFromOverlay`
  - `22 = UpdateModeToOverlay`
  - `23 = SetThermalModeFromOverlay`
  - `24 = UpdateThermalControlToOverlay`
  - `25 = SetLegacyFanModeFromOverlay`
  - `26 = UpdateLegacyFanToOverlay`
  - `29 = Initialization`
  - `30 = SetPowerMode`
  - `37 = SetModeFromPerformancePage`

- Overlay implementation:
  - [PerformanceControlWidgetViewModel.cs](</C:/Users/dot/Downloads/hp2/PerformanceControlPlugin/PerformanceControlPlugin.ViewModels/PerformanceControlWidgetViewModel.cs:640>)
  - It hosts `PipeServerV3("HP.Omen.Overlay.Plugin.PerformanceControlPlugin" + SessionId, ReceiveMsg)`.
  - It sends initialization to `PerformanceControlFg<SessionId>` in [PerformanceControlWidgetViewModel.cs](</C:/Users/dot/Downloads/hp2/PerformanceControlPlugin/PerformanceControlPlugin.ViewModels/PerformanceControlWidgetViewModel.cs:679>)
  - It sends:
    - mode command `21` in [PerformanceControlWidgetViewModel.cs](</C:/Users/dot/Downloads/hp2/PerformanceControlPlugin/PerformanceControlPlugin.ViewModels/PerformanceControlWidgetViewModel.cs:1266>)
    - thermal command `23` in [PerformanceControlWidgetViewModel.cs](</C:/Users/dot/Downloads/hp2/PerformanceControlPlugin/PerformanceControlPlugin.ViewModels/PerformanceControlWidgetViewModel.cs:1253>)
    - legacy fan command `25` in [PerformanceControlWidgetViewModel.cs](</C:/Users/dot/Downloads/hp2/PerformanceControlPlugin/PerformanceControlPlugin.ViewModels/PerformanceControlWidgetViewModel.cs:1196>)
  - It receives:
    - mode update `22`
    - thermal update `24`
    - fan update `26`
    - availability `28`
    - initialization `29`
    in [PerformanceControlWidgetViewModel.cs](</C:/Users/dot/Downloads/hp2/PerformanceControlPlugin/PerformanceControlPlugin.ViewModels/PerformanceControlWidgetViewModel.cs:943>)

- Background implementation:
  - [Entry.cs](</C:/Users/dot/Downloads/hp4/HP.Omen.Background.PerformanceControl/HP.Omen.Background.PerformanceControl/Entry.cs:411>)
  - It hosts:
    - `PipeServerV2("PerformanceControlFg" + SessionId, ReceiveMsg)`
    - `PipeServerV2("PowerOptionUiToPerformanceControlBg" + SessionId, ReceiveMsg)`
  - It handles:
    - `Command 14` as UI launch in [Entry.cs](</C:/Users/dot/Downloads/hp4/HP.Omen.Background.PerformanceControl/HP.Omen.Background.PerformanceControl/Entry.cs:589>)
    - `Command 29` by calling `SendInitSettingToPerformanceOverlay()` in [Entry.cs](</C:/Users/dot/Downloads/hp4/HP.Omen.Background.PerformanceControl/HP.Omen.Background.PerformanceControl/Entry.cs:593>)
  - It emits:
    - `FuncType 22` mode update in [Entry.cs](</C:/Users/dot/Downloads/hp4/HP.Omen.Background.PerformanceControl/HP.Omen.Background.PerformanceControl/Entry.cs:1648>)
    - `FuncType 24` thermal update in [Entry.cs](</C:/Users/dot/Downloads/hp4/HP.Omen.Background.PerformanceControl/HP.Omen.Background.PerformanceControl/Entry.cs:1685>)
    - `FuncType 26` legacy fan update in [Entry.cs](</C:/Users/dot/Downloads/hp4/HP.Omen.Background.PerformanceControl/HP.Omen.Background.PerformanceControl/Entry.cs:1705>)
    - `FuncType 28` availability in [Entry.cs](</C:/Users/dot/Downloads/hp4/HP.Omen.Background.PerformanceControl/HP.Omen.Background.PerformanceControl/Entry.cs:1722>)
    - `FuncType 29` initialization in [Entry.cs](</C:/Users/dot/Downloads/hp4/HP.Omen.Background.PerformanceControl/HP.Omen.Background.PerformanceControl/Entry.cs:1579>)

- Reply transport:
  - [Entry.cs](</C:/Users/dot/Downloads/hp4/HP.Omen.Background.PerformanceControl/HP.Omen.Background.PerformanceControl/Entry.cs:1754>)
  - `SendPipeMessageToModule(...)` uses `PipeClientV3`
  - [PipeClientV3.cs](</C:/Users/dot/Downloads/hp2/HP.Omen.Core.Common/HP.Omen.Core.Common.PipeUtility/PipeClientV3.cs:12>)
  - [ConnectedPipeVerifier.cs](</C:/Users/dot/Downloads/hp2/HP.Omen.Core.Common/HP.Omen.Core.Common.PipeUtility/ConnectedPipeVerifier.cs:178>)
  - This is the key reason the standalone app cannot receive overlay replies.

- Monitor registration:
  - [PerformanceMonitorHelper.cs](</C:/Users/dot/Downloads/hp2/HP.Omen.Core.Common/HP.Omen.Core.Common.Utilities/PerformanceMonitorHelper.cs:18>)
  - CPU simple registration pipe:
    - [PerformanceMonitorHelper.cs](</C:/Users/dot/Downloads/hp2/HP.Omen.Core.Common/HP.Omen.Core.Common.Utilities/PerformanceMonitorHelper.cs:32>)
  - GPU simple registration pipe:
    - [PerformanceMonitorHelper.cs](</C:/Users/dot/Downloads/hp2/HP.Omen.Core.Common/HP.Omen.Core.Common.Utilities/PerformanceMonitorHelper.cs:26>)

- BIOS-backed helper:
  - [OmenHsaClient.cs](</C:/Users/dot/Downloads/hp2/HP.Omen.Core.Common/HP.Omen.Core.Common/OmenHsaClient.cs:1360>)
  - `SetMaxFan()` uses BIOS command `131080`, type `39`
  - `GetMaxFanAsync()` uses BIOS command `131080`, type `38`
  - `SystemDesignData` uses BIOS command `131080`, type `40` in [OmenHsaClient.cs](</C:/Users/dot/Downloads/hp2/HP.Omen.Core.Common/HP.Omen.Core.Common/OmenHsaClient.cs:217>)
  - `GetGraphicsMode()` uses BIOS command `1`, type `82` in [OmenHsaClient.cs](</C:/Users/dot/Downloads/hp2/HP.Omen.Core.Common/HP.Omen.Core.Common/OmenHsaClient.cs:1434>)

## Payload Notes

- OMEN's overlay uses `PipeClientV2`, which means XML serialization over named pipes.
- The payload shell is `PerseusRevMsg`.
- The command payload is `PerformanceControlMsg { Command, Data }`.
- `PerformanceControlMsg` is defined in [PerformanceControlMsg.cs](</C:/Users/dot/Downloads/hp2/HP.Omen.Core.Model.DataStructure/HP.Omen.Core.Model.DataStructure.Structs/PerformanceControlMsg.cs:6>)
- The performance mode enum values are:
  - [PerformanceMode.cs](</C:/Users/dot/Downloads/hp2/PerformanceControl/Hp.Bridge.Client.SDKs.PerformanceControl.Enums/PerformanceMode.cs:5>)
  - `Default = 0`
  - `Performance = 1`
  - `Cool = 2`
  - `Quiet = 3`
  - `Extreme = 4`
  - `Eco = 256`

## Caveats

- This PoC uses the same plugin return pipe name as OMEN's overlay.
- If the official OMEN overlay widget is open, it may already own `HP.Omen.Overlay.Plugin.PerformanceControlPlugin<SessionId>`.
- In that case, close OMEN's overlay before running this PoC.
- The control path still depends on HP's background service or OMEN background process being alive.
- The standalone PoC currently does not reproduce OMEN's full state-return channel because HP sends overlay replies through `PipeClientV3` to a signature-verified receiver.
- The desktop `UISync` channel exists, but it appears to be a refresh signal for HP's own desktop module rather than a full replacement for overlay init/update payloads.
- Hidden modes should be treated as valid only when confirmed by BIOS readback or observable system behavior.
- Graphics mode changes on this machine should be treated as post-reboot operations. Readback before reboot is not enough to prove the switch took effect.
- Current known result: `Hybrid` readback is stable, and the non-hybrid mode should be treated as `Integrated`/`UMA` on this platform.
- `Graphics` options should be gated by confirmed readback/behavior, not by the earlier discrete-support interpretation.

## Current PoC Design

- Use HP's installed `HP.Omen.Core.Common.dll`, `HP.Omen.Core.Model.DataStructure.dll`, and `PerformanceControl.dll`.
- Send control commands through `PerformanceControlFg<SessionId>`.
- Register HP's CPU/GPU simple monitor feeds for lightweight telemetry.
- Keep the first PoC focused on mode switching, thermal mode, legacy fan mode, and basic CPU/GPU temperature data.
- Do not rely on the overlay reply pipe as the primary state source. It is gated by HP's trusted `PipeClientV3` reply path.
- Prefer future readback work through:
  - BIOS/WMI reads
  - HP storage/proxy values
  - desktop `PerformanceControlBg` / `UISync` analysis
- Graphics switching is handled separately from performance-control pipes:
  - use BIOS command `2, 82` for writes
  - use `GetGraphicsMode()` for post-reboot verification
- Use diagnostics to inspect:
  - raw initialization JSON, when available
  - last CPU/GPU monitor payloads
  - recent inbound events
  - `SystemDesignData`
  - support flags from `PowerControlHelper`
  - `GetMaxFanAsync()` readback
  - current graphics mode
  - graphics support flags from `HP.Omen.Core.Model.Device.Models.GraphicsSwitcherHelper`
  - the last graphics-mode BIOS return code
