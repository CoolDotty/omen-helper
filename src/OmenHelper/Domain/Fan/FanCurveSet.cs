using System;

namespace OmenHelper.Domain.Fan;

internal sealed class FanCurveSet
{
    public FanCurveSet(FanCurveProfile cpu, FanCurveProfile gpu, FanCurveProfile chassis, bool gpuLinked, int hysteresisRiseDeltaC = 5, int hysteresisDropDeltaC = 10)
    {
        Cpu = cpu ?? throw new ArgumentNullException(nameof(cpu));
        Gpu = gpu ?? throw new ArgumentNullException(nameof(gpu));
        Chassis = chassis ?? throw new ArgumentNullException(nameof(chassis));
        GpuLinked = gpuLinked;
        HysteresisRiseDeltaC = hysteresisRiseDeltaC;
        HysteresisDropDeltaC = hysteresisDropDeltaC;
    }

    public FanCurveProfile Cpu { get; }

    public FanCurveProfile Gpu { get; }

    public FanCurveProfile Chassis { get; }

    public bool GpuLinked { get; }

    public int HysteresisRiseDeltaC { get; }

    public int HysteresisDropDeltaC { get; }

    public FanCurveSet WithCpu(FanCurveProfile cpu) => new FanCurveSet(cpu, Gpu, Chassis, GpuLinked, HysteresisRiseDeltaC, HysteresisDropDeltaC);

    public FanCurveSet WithGpu(FanCurveProfile gpu) => new FanCurveSet(Cpu, gpu, Chassis, GpuLinked, HysteresisRiseDeltaC, HysteresisDropDeltaC);

    public FanCurveSet WithChassis(FanCurveProfile chassis) => new FanCurveSet(Cpu, Gpu, chassis, GpuLinked, HysteresisRiseDeltaC, HysteresisDropDeltaC);

    public FanCurveSet WithGpuLinked(bool gpuLinked)
    {
        FanCurveProfile gpu = gpuLinked ? FanCurveDefaults.BuildLinkedGpuProfile(Cpu) : Gpu;
        return new FanCurveSet(Cpu, gpu, Chassis, gpuLinked, HysteresisRiseDeltaC, HysteresisDropDeltaC);
    }
}
