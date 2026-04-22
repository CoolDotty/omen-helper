using System;
using System.Collections.Generic;
using OmenHelper.Domain.Fan;

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
    public double? AveragedCpuTemperatureC { get; set; }
    public double? AveragedGpuTemperatureC { get; set; }
    public double? AveragedChassisTemperatureC { get; set; }
    public DateTime? PooledTelemetryTimestampUtc { get; set; }
    public string TemperatureSource { get; set; }
    public bool TemperatureReadSucceeded { get; set; }
    public string CurrentFanRpmSummary { get; set; }
    public int CurrentFanMinimumRpm { get; set; }
    public int? FanMinimumOverrideRpm { get; set; }
    public bool FanCurveRuntimeEnabled { get; set; }
    public string ActiveFanCurveMode { get; set; }
    public int FanCurveHysteresisRiseDeltaC { get; set; }
    public int FanCurveHysteresisDropDeltaC { get; set; }
    public FanCurveProfile ActiveCpuCurve { get; set; }
    public FanCurveProfile ActiveGpuCurve { get; set; }
    public FanCurveProfile ActiveChassisCurve { get; set; }
    public bool GpuCurveLinked { get; set; }
    public int CurveDesiredCpuRpm { get; set; }
    public int CurveDesiredGpuRpm { get; set; }
    public int CurveAppliedCpuRpm { get; set; }
    public int CurveAppliedGpuRpm { get; set; }
    public bool CurveChassisOverrideUsed { get; set; }
    public double? CpuHysteresisAnchorTemperatureC { get; set; }
    public double? GpuHysteresisAnchorTemperatureC { get; set; }
    public double? ChassisHysteresisAnchorTemperatureC { get; set; }
    public DateTime? LastCurveWriteTimestampUtc { get; set; }
    public string LastCurveWriteReason { get; set; }
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
