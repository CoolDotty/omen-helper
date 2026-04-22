using System;

namespace OmenHelper.Application.State;

internal sealed class HardwareTelemetrySnapshot
{
    public DateTime TimestampUtc { get; set; }
    public double? CpuTemperatureC { get; set; }
    public double? GpuTemperatureC { get; set; }
    public double? ChassisTemperatureC { get; set; }
    public int? CpuFanRpm { get; set; }
    public int? GpuFanRpm { get; set; }
    public string TemperatureSource { get; set; }
    public string FanSource { get; set; }
    public bool TemperatureReadSucceeded { get; set; }
    public bool FanReadSucceeded { get; set; }
}
