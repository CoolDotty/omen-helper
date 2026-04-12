namespace OmenHelper.Models;

internal sealed class TelemetrySnapshot
{
    public PerformanceMonitorSample Cpu { get; set; }

    public PerformanceMonitorSample Gpu { get; set; }
}
