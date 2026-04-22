using Hp.Bridge.Client.SDKs.PerformanceControl.Enums;
using OmenHelper.Domain.Firmware;

namespace OmenHelper.Domain.Fan;

internal sealed class FanCurveStore
{
    public FanCurveStore(bool enabled, FanCurveSet eco, FanCurveSet balanced, FanCurveSet performance, FanCurveSet unleashed, int hysteresisRiseDeltaC = 5, int hysteresisDropDeltaC = 10)
    {
        Enabled = enabled;
        Eco = eco;
        Balanced = balanced;
        Performance = performance;
        Unleashed = unleashed;
        HysteresisRiseDeltaC = hysteresisRiseDeltaC;
        HysteresisDropDeltaC = hysteresisDropDeltaC;
    }

    public bool Enabled { get; }

    public FanCurveSet Eco { get; }

    public FanCurveSet Balanced { get; }

    public FanCurveSet Performance { get; }

    public FanCurveSet Unleashed { get; }

    public int HysteresisRiseDeltaC { get; }

    public int HysteresisDropDeltaC { get; }

    public FanCurveSet GetForMode(PerformanceMode mode)
    {
        if (mode == PerformanceMode.Eco)
        {
            return WithHysteresis(Eco);
        }

        if (mode == PerformanceMode.Performance)
        {
            return WithHysteresis(Performance);
        }

        if (PerformanceModeFirmwareMap.IsUnleashedMode(mode))
        {
            return WithHysteresis(Unleashed);
        }

        return WithHysteresis(Balanced);
    }

    public FanCurveStore WithEnabled(bool enabled) => new FanCurveStore(enabled, Eco, Balanced, Performance, Unleashed, HysteresisRiseDeltaC, HysteresisDropDeltaC);

    public FanCurveStore WithHysteresis(int riseDeltaC, int dropDeltaC)
    {
        return new FanCurveStore(Enabled, Eco, Balanced, Performance, Unleashed, riseDeltaC, dropDeltaC);
    }

    public FanCurveStore WithMode(PerformanceMode mode, FanCurveSet set)
    {
        FanCurveSet normalizedSet = WithHysteresis(set);
        if (mode == PerformanceMode.Eco)
        {
            return new FanCurveStore(Enabled, normalizedSet, Balanced, Performance, Unleashed, HysteresisRiseDeltaC, HysteresisDropDeltaC);
        }

        if (mode == PerformanceMode.Performance)
        {
            return new FanCurveStore(Enabled, Eco, Balanced, normalizedSet, Unleashed, HysteresisRiseDeltaC, HysteresisDropDeltaC);
        }

        if (PerformanceModeFirmwareMap.IsUnleashedMode(mode))
        {
            return new FanCurveStore(Enabled, Eco, Balanced, Performance, normalizedSet, HysteresisRiseDeltaC, HysteresisDropDeltaC);
        }

        return new FanCurveStore(Enabled, Eco, normalizedSet, Performance, Unleashed, HysteresisRiseDeltaC, HysteresisDropDeltaC);
    }

    private FanCurveSet WithHysteresis(FanCurveSet set)
    {
        return new FanCurveSet(set.Cpu, set.Gpu, set.Chassis, set.GpuLinked, HysteresisRiseDeltaC, HysteresisDropDeltaC);
    }
}
