using System;

namespace OmenHelper.Presentation.Controls;

internal sealed class FanCurveHysteresisChangedEventArgs : EventArgs
{
    public FanCurveHysteresisChangedEventArgs(int riseDeltaC, int dropDeltaC)
    {
        RiseDeltaC = riseDeltaC;
        DropDeltaC = dropDeltaC;
    }

    public int RiseDeltaC { get; }

    public int DropDeltaC { get; }
}
