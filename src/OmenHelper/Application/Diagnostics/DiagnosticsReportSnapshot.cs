using System.Collections.Generic;

namespace OmenHelper.Application.Diagnostics;

internal sealed class DiagnosticsReportSnapshot
{
    public int SessionId { get; set; }
    public bool Initialized { get; set; }
    public bool Available { get; set; }

    public string CurrentMode { get; set; }
    public bool CurrentModeIsInferred { get; set; }
    public string CurrentThermalMode { get; set; }
    public string CurrentLegacyFanMode { get; set; }
    public string FanMinimumRpm { get; set; }
    public string FanMinimumOverrideRpm { get; set; }
    public string CurrentGraphicsMode { get; set; }

    public string LastPerformanceRequestMode { get; set; }
    public string LastPerformanceRequestPath { get; set; }
    public string LastPerfType26ExecuteResult { get; set; }
    public string LastPerfType26ReturnCode { get; set; }
    public string LastPerfType34ExecuteResult { get; set; }
    public string LastPerfType34ReturnCode { get; set; }
    public string LastPerfType41ExecuteResult { get; set; }
    public string LastPerfType41ReturnCode { get; set; }

    public string LastMaxFanExecuteResult { get; set; }
    public string LastMaxFanReturnCode { get; set; }
    public string PerformanceStatusBlobExecuteResult { get; set; }
    public string PerformanceStatusBlobReturnCode { get; set; }
    public string PerformanceStatusBlobHash { get; set; }
    public string PerformanceStatusBlobPreview { get; set; }
    public string PerformanceStatusBlobSensors { get; set; }
    public string PerformanceStatusBlobChangedBytes { get; set; }
    public string PreviousPerformanceStatusBlobHash { get; set; }

    public string FanMinimumBlobExecuteResult { get; set; }
    public string FanMinimumBlobReturnCode { get; set; }
    public string FanMinimumBlobHash { get; set; }
    public string FanMinimumBlobPreview { get; set; }
    public string FanMinimumBlobChangedBytes { get; set; }
    public string PreviousFanMinimumBlobHash { get; set; }

    public string ThermalUiType { get; set; }
    public bool ExtremeUnlocked { get; set; }
    public bool UnleashVisible { get; set; }
    public string SupportModes { get; set; }

    public string SystemDesignData { get; set; }
    public string ShippingAdapterPowerRating { get; set; }
    public string IsBiosPerformanceModeSupport { get; set; }
    public string IsSwFanControlSupport { get; set; }
    public string IsExtremeModeSupport { get; set; }
    public string IsExtremeModeUnlock { get; set; }
    public string GraphicsModeSwitchBits { get; set; }
    public bool GraphicsModeSwitchReadSucceeded { get; set; }
    public bool GraphicsModeSwitchSupported { get; set; }
    public string GraphicsModeSwitchRawSlots { get; set; }
    public string GraphicsModeSwitchSlots { get; set; }
    public bool GraphicsModeSwitchHasIntegratedSlot { get; set; }
    public bool GraphicsModeSwitchHasHybridSlot { get; set; }
    public bool GraphicsModeSwitchHasDedicatedSlot { get; set; }
    public bool GraphicsModeSwitchHasOptimusSlot { get; set; }
    public bool GraphicsSupportsHybrid { get; set; }
    public bool GraphicsSupportsUma { get; set; }
    public bool GraphicsNeedsReboot { get; set; }
    public string LastGraphicsRequestMode { get; set; }
    public string LastGraphicsRequestReturnCode { get; set; }
    public string MaxFanBios { get; set; }

    public IReadOnlyList<string> RecentEvents { get; set; }
}
