namespace OmenHelper.Domain.Fan;

internal sealed class FanCurveEvaluationResult
{
    public double AveragedCpuTemperatureC { get; set; }
    public double AveragedGpuTemperatureC { get; set; }
    public double AveragedChassisTemperatureC { get; set; }
    public int DesiredCpuRpm { get; set; }
    public int DesiredGpuRpm { get; set; }
    public int DesiredChassisRpm { get; set; }
    public int HysteresisCpuRpm { get; set; }
    public int HysteresisGpuRpm { get; set; }
    public int HysteresisChassisRpm { get; set; }
    public double? CpuAnchorTemperatureC { get; set; }
    public double? GpuAnchorTemperatureC { get; set; }
    public double? ChassisAnchorTemperatureC { get; set; }
    public int FinalCpuRpm { get; set; }
    public int FinalGpuRpm { get; set; }
    public bool GpuLinked { get; set; }
    public bool ChassisOverrideUsed { get; set; }
    public string SourceMode { get; set; }
}
