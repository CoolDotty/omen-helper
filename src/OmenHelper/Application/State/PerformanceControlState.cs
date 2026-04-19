using System.Collections.Generic;

namespace OmenHelper.Application.State;

internal sealed class PerformanceControlState
{
    public bool Initialized { get; set; }

    public bool Available { get; set; }

    public bool CurrentModeKnown { get; set; }

    public string CurrentMode { get; set; }

    public bool CurrentModeIsInferred { get; set; }

    public string CurrentThermalMode { get; set; }

    public bool MaxFanEnabled { get; set; }

    public string CurrentLegacyFanMode { get; set; }

    public int? CpuFanRpm { get; set; }
    public int? GpuFanRpm { get; set; }
    public string FanRpmSource { get; set; }
    public bool FanRpmReadSucceeded { get; set; }
    public double? CpuTemperatureC { get; set; }
    public double? GpuTemperatureC { get; set; }
    public double? ChassisTemperatureC { get; set; }
    public string TemperatureSource { get; set; }
    public bool TemperatureReadSucceeded { get; set; }
    public string CurrentFanRpmSummary { get; set; }

    public int CurrentFanMinimumRpm { get; set; }

    public int? FanMinimumOverrideRpm { get; set; }

    public string CurrentGraphicsMode { get; set; }

    public bool GraphicsModeSwitchSupported { get; set; }

    public bool GraphicsSupportsUma { get; set; }

    public bool GraphicsSupportsHybrid { get; set; }

    public bool GraphicsNeedsReboot { get; set; }

    public byte GraphicsModeSwitchBits { get; set; }

    public string LastGraphicsRequestMode { get; set; }

    public int? LastGraphicsRequestReturnCode { get; set; }

    public bool ExtremeUnlocked { get; set; }

    public bool UnleashVisible { get; set; }

    public string ThermalUiType { get; set; }

    public IReadOnlyList<string> SupportModes { get; set; }
}
