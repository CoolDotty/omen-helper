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

- Direct BIOS read getters from OmenMon:
  - GetAnimTable
  - GetBacklight
  - GetColorTable
  - GetAdapter
  - GetBornDate
  - GetKbdType
  - GetSystem
  - HasBacklight
  - HasMemoryOverclock
  - HasOverclock
  - HasUndervoltBios
  - GetGpuMode
  - GetGpuPower
  - GetFanCount
  - GetFanType
  - GetFanLevel
  - GetFanTable
  - GetMaxFan
  - GetTemperature
  - GetThrottling
  - see /C:/Users/dot/Documents/omen-helper/OmenMon/Hardware/BiosCtl.cs and /C:/Users/dot/Documents/omen-helper/OmenMon/App/Cli/CliOp.cs
- Convenience reads OmenMon derives from those getters:
  - GetGpuCustomTgp
  - GetGpuDState
  - GetGpuPpab
  - GetDefaultCpuPowerLimit4
  - GetGpuModeSupport
  - GetKbdBacklightSupport
  - GetKbdColorSupport
  - see /C:/Users/dot/Documents/omen-helper/OmenMon/Hardware/Settings.cs
- WMI getters OmenMon also exposes, but they are not BIOS:
  - GetManufacturer
  - GetProduct
  - GetSerial
  - GetVersion
  - see /C:/Users/dot/Documents/omen-helper/OmenMon/Hardware/Settings.cs
