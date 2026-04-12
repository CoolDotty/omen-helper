using System.Collections.Generic;

namespace OmenHelper.Models;

internal sealed class PerformanceControlState
{
    public bool Initialized { get; set; }

    public bool Available { get; set; }

    public string CurrentMode { get; set; }

    public string CurrentThermalMode { get; set; }

    public string CurrentLegacyFanMode { get; set; }

    public bool ExtremeUnlocked { get; set; }

    public bool UnleashVisible { get; set; }

    public string ThermalUiType { get; set; }

    public IReadOnlyList<string> SupportModes { get; set; }
}
