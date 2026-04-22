using System;

namespace OmenHelper.Domain.Fan;

internal static class FanCurveEvaluator
{
    public static FanCurveEvaluationResult Evaluate(
        FanCurveSet set,
        double averagedCpuTemperatureC,
        double averagedGpuTemperatureC,
        double averagedChassisTemperatureC,
        FanCurveRuntimeState runtime,
        string sourceMode)
    {
        int desiredCpu = set.Cpu.EvaluateCpuOrGpu(averagedCpuTemperatureC);
        int desiredGpu = set.Gpu.EvaluateCpuOrGpu(averagedGpuTemperatureC);
        int desiredChassis = set.Chassis.EvaluateChassis(averagedChassisTemperatureC);
        int effectiveDesiredCpu = desiredCpu;
        int effectiveDesiredGpu = desiredGpu;

        double? chassisAnchor = runtime.ChassisAnchorTemperatureC;
        int appliedChassis = runtime.AppliedChassisRpm;
        int hysteresisChassis = ResolveHysteresis(desiredChassis, averagedChassisTemperatureC, set.HysteresisRiseDeltaC, set.HysteresisDropDeltaC, ref chassisAnchor, ref appliedChassis);
        runtime.ChassisAnchorTemperatureC = chassisAnchor;
        runtime.AppliedChassisRpm = appliedChassis;

        int hysteresisCpu;
        int hysteresisGpu;

        if (set.GpuLinked)
        {
            effectiveDesiredCpu = Math.Max(desiredCpu, FanCurveProfile.NormalizeRpm(desiredGpu + 200));
            effectiveDesiredGpu = FanCurveProfile.NormalizeRpm(effectiveDesiredCpu - 200);
            double combinedTemperature = Math.Max(averagedCpuTemperatureC, averagedGpuTemperatureC);
            double? linkedAnchor = runtime.CpuAnchorTemperatureC;
            int linkedAppliedCpu = runtime.AppliedCpuRpm;
            hysteresisCpu = ResolveHysteresis(effectiveDesiredCpu, combinedTemperature, set.HysteresisRiseDeltaC, set.HysteresisDropDeltaC, ref linkedAnchor, ref linkedAppliedCpu);
            runtime.CpuAnchorTemperatureC = linkedAnchor;
            runtime.GpuAnchorTemperatureC = linkedAnchor;
            runtime.AppliedCpuRpm = linkedAppliedCpu;
            runtime.AppliedGpuRpm = FanCurveProfile.NormalizeRpm(linkedAppliedCpu - 200);
            hysteresisGpu = FanCurveProfile.NormalizeRpm(hysteresisCpu - 200);
        }
        else
        {
            double? cpuAnchor = runtime.CpuAnchorTemperatureC;
            int appliedCpu = runtime.AppliedCpuRpm;
            hysteresisCpu = ResolveHysteresis(desiredCpu, averagedCpuTemperatureC, set.HysteresisRiseDeltaC, set.HysteresisDropDeltaC, ref cpuAnchor, ref appliedCpu);
            runtime.CpuAnchorTemperatureC = cpuAnchor;
            runtime.AppliedCpuRpm = appliedCpu;

            double? gpuAnchor = runtime.GpuAnchorTemperatureC;
            int appliedGpu = runtime.AppliedGpuRpm;
            hysteresisGpu = ResolveHysteresis(desiredGpu, averagedGpuTemperatureC, set.HysteresisRiseDeltaC, set.HysteresisDropDeltaC, ref gpuAnchor, ref appliedGpu);
            runtime.GpuAnchorTemperatureC = gpuAnchor;
            runtime.AppliedGpuRpm = appliedGpu;
        }

        int finalCpu = FanCurveProfile.NormalizeRpm(Math.Max(hysteresisCpu, hysteresisChassis));
        int finalGpu = set.GpuLinked
            ? FanCurveProfile.NormalizeRpm(finalCpu - 200)
            : FanCurveProfile.NormalizeRpm(Math.Max(hysteresisGpu, hysteresisChassis));

        return new FanCurveEvaluationResult
        {
            AveragedCpuTemperatureC = averagedCpuTemperatureC,
            AveragedGpuTemperatureC = averagedGpuTemperatureC,
            AveragedChassisTemperatureC = averagedChassisTemperatureC,
            DesiredCpuRpm = effectiveDesiredCpu,
            DesiredGpuRpm = effectiveDesiredGpu,
            DesiredChassisRpm = desiredChassis,
            HysteresisCpuRpm = hysteresisCpu,
            HysteresisGpuRpm = hysteresisGpu,
            HysteresisChassisRpm = hysteresisChassis,
            CpuAnchorTemperatureC = runtime.CpuAnchorTemperatureC,
            GpuAnchorTemperatureC = runtime.GpuAnchorTemperatureC,
            ChassisAnchorTemperatureC = runtime.ChassisAnchorTemperatureC,
            FinalCpuRpm = finalCpu,
            FinalGpuRpm = finalGpu,
            GpuLinked = set.GpuLinked,
            ChassisOverrideUsed = finalCpu != hysteresisCpu || finalGpu != hysteresisGpu,
            SourceMode = sourceMode ?? string.Empty
        };
    }

    private static int ResolveHysteresis(int desiredRpm, double averagedTemperatureC, int riseDeltaC, int dropDeltaC, ref double? anchorTemperatureC, ref int appliedRpm)
    {
        riseDeltaC = Math.Max(0, riseDeltaC);
        dropDeltaC = Math.Max(0, dropDeltaC);

        if (!anchorTemperatureC.HasValue)
        {
            anchorTemperatureC = averagedTemperatureC;
            appliedRpm = desiredRpm;
            return appliedRpm;
        }

        if (averagedTemperatureC >= anchorTemperatureC.Value + riseDeltaC || averagedTemperatureC <= anchorTemperatureC.Value - dropDeltaC)
        {
            anchorTemperatureC = averagedTemperatureC;
            appliedRpm = desiredRpm;
        }

        return appliedRpm;
    }
}
