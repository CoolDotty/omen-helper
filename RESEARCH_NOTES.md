# OMEN Helper Research Notes

## Goal

Build a small replacement utility for OMEN Gaming Hub performance controls on an OMEN Transcend 14 without automating the OMEN UI.

## What Was Verified Locally

- OMEN's overlay performance widget sends commands to `PerformanceControlFg<SessionId>`.
- The overlay expects replies on `HP.Omen.Overlay.Plugin.PerformanceControlPlugin<SessionId>`.
- The overlay registers CPU and GPU monitor streams through HP's performance-monitor registration pipes.
- HP also exposes some lower-level BIOS-backed functionality in `OmenHsaClient`, including `SetMaxFan()`.

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
- The background receiver implementation was not present in the exported source tree, so direct command handling is inferred from the overlay sender and enum mappings rather than the background code itself.
- The control path still depends on HP's background service or OMEN background process being alive.

## Current PoC Design

- Use HP's installed `HP.Omen.Core.Common.dll`, `HP.Omen.Core.Model.DataStructure.dll`, and `PerformanceControl.dll`.
- Send control commands through `PerformanceControlFg<SessionId>`.
- Host the overlay reply pipe to receive initialization and update messages.
- Register HP's CPU/GPU simple monitor feeds for lightweight telemetry.
- Keep the first PoC focused on mode switching, thermal mode, legacy fan mode, and basic CPU/GPU temperature data.
