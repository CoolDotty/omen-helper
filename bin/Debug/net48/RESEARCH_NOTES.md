# OMEN Helper Research Notes

## Goal

Build a small replacement utility for OMEN Gaming Hub performance controls on an OMEN Transcend 14 without automating the OMEN UI.

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
- The user-observed graphics switcher UI on this machine appeared to expose `Integrated` and `Hybrid`, and HP required a restart for the change to take effect.
- HP's live runtime helper reports `SupportedModes = 6` and `SupportedUMAmode = False` on this machine.
- `SupportedModes = 6` means the platform advertises `Hybrid` and `Discrete` support, but not `UMA`.
- A live attempt to switch to `Discrete` from the standalone PoC returned BIOS/WMI `returnCode = 255` and the system stayed on `Hybrid` after reboot.
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
- A live runtime query against `HP.Omen.Core.Model.Device.Models.GraphicsSwitcherHelper` on this machine returned:
  - `SupportedModes = 6`
  - `SupportedUMAmode = False`
  - which means the platform does not currently advertise `UMA` support to HP's own helper layer
- That explains why the PoC's original `Integrated` button did not stick after reboot:
  - the app exposed `UMAMode`
  - HP's own support helper says `UMAMode` is unsupported on this hardware/config
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
- Current known result: `Hybrid` readback is stable, while a standalone request to switch to `Discrete` failed with `255` and did not persist.
- `UMA` should not be offered blindly. The app should first read HP's support flags and suppress unsupported graphics modes.

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


