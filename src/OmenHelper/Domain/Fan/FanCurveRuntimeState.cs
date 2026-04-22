namespace OmenHelper.Domain.Fan;

internal sealed class FanCurveRuntimeState
{
    public double? CpuAnchorTemperatureC { get; set; }
    public double? GpuAnchorTemperatureC { get; set; }
    public double? ChassisAnchorTemperatureC { get; set; }
    public int AppliedCpuRpm { get; set; }
    public int AppliedGpuRpm { get; set; }
    public int AppliedChassisRpm { get; set; }
    public int? LastWrittenCpuRpm { get; set; }
    public int? LastWrittenGpuRpm { get; set; }

    public void ResetAnchors()
    {
        CpuAnchorTemperatureC = null;
        GpuAnchorTemperatureC = null;
        ChassisAnchorTemperatureC = null;
        AppliedCpuRpm = 0;
        AppliedGpuRpm = 0;
        AppliedChassisRpm = 0;
    }
}
