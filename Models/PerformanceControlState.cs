using System.Collections.Generic;

namespace OmenHelper.Models;

internal sealed class PerformanceControlState
{
    public bool Initialized { get; set; }

    public bool Available { get; set; }

    public bool CurrentModeKnown { get; set; }

    public string CurrentMode { get; set; }

    public bool CurrentModeIsInferred { get; set; }

    public string CurrentThermalMode { get; set; }

    public string CurrentLegacyFanMode { get; set; }

    public int CurrentFanMinimumRpm { get; set; }

    public string CurrentGraphicsMode { get; set; }

    public bool GraphicsModeSwitchSupported { get; set; }

    public bool GraphicsSupportsUma { get; set; }

    public bool GraphicsSupportsHybrid { get; set; }

    public bool GraphicsNeedsReboot { get; set; }

    public byte GraphicsModeSwitchBits { get; set; }

    public string LastGraphicsRequestMode { get; set; }

    public int? LastGraphicsRequestReturnCode { get; set; }

    public bool ExtremeUnlocked { get; set; }

    public bool UnleashVisible { get; set; }

    public string ThermalUiType { get; set; }

    public IReadOnlyList<string> SupportModes { get; set; }
}
