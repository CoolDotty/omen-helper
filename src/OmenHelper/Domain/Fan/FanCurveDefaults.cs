using System.Collections.Generic;
using Hp.Bridge.Client.SDKs.PerformanceControl.Enums;
using OmenHelper.Domain.Firmware;

namespace OmenHelper.Domain.Fan;

internal static class FanCurveDefaults
{
    public static FanCurveStore CreateDefaultStore()
    {
        return new FanCurveStore(
            enabled: true,
            eco: CreateDefaultSet(PerformanceMode.Eco),
            balanced: CreateDefaultSet(PerformanceMode.Default),
            performance: CreateDefaultSet(PerformanceMode.Performance),
            unleashed: CreateDefaultSet(PerformanceMode.Extreme));
    }

    public static FanCurveSet CreateDefaultSet(PerformanceMode mode)
    {
        FanCurveProfile cpu = BuildCpuDefault(mode);
        FanCurveProfile gpu = BuildLinkedGpuProfile(cpu);
        FanCurveProfile chassis = BuildChassisDefault();
        return new FanCurveSet(cpu, gpu, chassis, gpuLinked: true);
    }

    public static FanCurveProfile BuildCpuDefault(PerformanceMode mode)
    {
        int minimum = FanCurveProfile.NormalizeRpm(PerformanceModeFirmwareMap.GetFanMinimumRpm(mode));
        List<int> rpms = new List<int>(FanCurveProfile.CpuGpuTemperaturePoints.Length);
        int current = minimum;
        for (int i = 0; i < FanCurveProfile.CpuGpuTemperaturePoints.Length; i++)
        {
            rpms.Add(current);
            current = FanCurveProfile.NormalizeRpm(current + 600);
        }

        rpms[rpms.Count - 1] = 6500;
        return new FanCurveProfile(FanCurveProfile.CpuGpuTemperaturePoints, rpms);
    }

    public static FanCurveProfile BuildLinkedGpuProfile(FanCurveProfile cpu)
    {
        List<int> rpms = new List<int>(FanCurveProfile.CpuGpuTemperaturePoints.Length);
        for (int i = 0; i < FanCurveProfile.CpuGpuTemperaturePoints.Length; i++)
        {
            rpms.Add(FanCurveProfile.NormalizeRpm(cpu[i] - 200));
        }

        return new FanCurveProfile(FanCurveProfile.CpuGpuTemperaturePoints, rpms);
    }

    public static FanCurveProfile BuildChassisDefault()
    {
        return new FanCurveProfile(FanCurveProfile.ChassisTemperaturePoints, new[] { 6500, 6500, 6500, 6500, 6500, 6500, 6500, 6500, 6500 });
    }
}
