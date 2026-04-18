using System;
using System.Collections.Generic;
using Hp.Bridge.Client.SDKs.PerformanceControl.Enums;
using HP.Omen.Core.Common.Enums;
using HP.Omen.Core.Common.PowerControl.Enum;
using HP.Omen.Core.Model.DataStructure.Modules.FanControl.Enums;
using HP.Omen.Core.Model.DataStructure.Modules.GraphicsSwitcher.Enums;

namespace OmenHelper.Application.State;

internal sealed class SharedSessionState
{
    public int SessionId { get; set; }
    public bool Started { get; set; }
    public bool Disposed { get; set; }
    public bool Initialized { get; set; }
    public bool Available { get; set; }
    public PerformanceMode CurrentMode { get; set; } = PerformanceMode.Default;
    public bool CurrentModeKnown { get; set; }
    public bool CurrentModeIsInferred { get; set; }
    public ThermalControl CurrentThermalMode { get; set; } = ThermalControl.Auto;
    public FanMode CurrentLegacyFanMode { get; set; } = FanMode.Normal;
    public bool MaxFanEnabled { get; set; }
    public GraphicsSwitcherMode CurrentGraphicsMode { get; set; } = GraphicsSwitcherMode.Unknown;
    public int? FanMinimumOverrideRpm { get; set; }
    public bool ExtremeUnlocked { get; set; } = true;
    public bool UnleashVisible { get; set; } = true;
    public ThermalModeOnUI ThermalUiType { get; set; }
    public PerformanceMode? BatteryPowerMode { get; set; } = PerformanceMode.Eco;
    public PerformanceMode? PluggedInPowerMode { get; set; } = PerformanceMode.Performance;
    public bool? LastKnownPluggedIn { get; set; }
    public bool GraphicsSupportsUma { get; set; }
    public bool GraphicsSupportsHybrid { get; set; }
    public bool GraphicsModeSwitchSupported { get; set; }
    public bool GraphicsNeedsReboot { get; set; }
    public byte GraphicsModeSwitchBits { get; set; }
    public bool GraphicsModeSwitchReadSucceeded { get; set; }
    public string LastGraphicsRequestMode { get; set; } = string.Empty;
    public int? LastGraphicsRequestReturnCode { get; set; }
    public List<string> SupportModes { get; set; } = new List<string>();
    public string LastPerformanceRequestMode { get; set; } = string.Empty;
    public string LastPerformanceRequestPath { get; set; } = string.Empty;
    public int? LastPerfType26ReturnCode { get; set; }
    public bool? LastPerfType26ExecuteResult { get; set; }
    public int? LastPerfType34ReturnCode { get; set; }
    public bool? LastPerfType34ExecuteResult { get; set; }
    public int? LastPerfType41ReturnCode { get; set; }
    public bool? LastPerfType41ExecuteResult { get; set; }
    public int? LastMaxFanReturnCode { get; set; }
    public bool? LastMaxFanExecuteResult { get; set; }
    public byte[] LastPerformanceStatusBlob { get; set; } = Array.Empty<byte>();
    public byte[] PreviousPerformanceStatusBlob { get; set; } = Array.Empty<byte>();
    public string LastPerformanceStatusBlobHash { get; set; } = string.Empty;
    public string PreviousPerformanceStatusBlobHash { get; set; } = string.Empty;
    public string LastPerformanceStatusBlobPreview { get; set; } = string.Empty;
    public int? LastPerformanceStatusBlobChangedBytes { get; set; }
    public int? LastPerformanceStatusBlobReturnCode { get; set; }
    public bool? LastPerformanceStatusBlobExecuteResult { get; set; }
    public byte[] LastFanTargetBlob { get; set; } = Array.Empty<byte>();
    public byte[] PreviousFanTargetBlob { get; set; } = Array.Empty<byte>();
    public string LastFanTargetBlobHash { get; set; } = string.Empty;
    public string PreviousFanTargetBlobHash { get; set; } = string.Empty;
    public string LastFanTargetBlobPreview { get; set; } = string.Empty;
    public int? LastFanTargetBlobChangedBytes { get; set; }
    public int? LastFanTargetBlobReturnCode { get; set; }
    public bool? LastFanTargetBlobExecuteResult { get; set; }
    public Queue<string> RecentEvents { get; set; } = new Queue<string>();
    public object DiagnosticsSync { get; } = new object();

    public event EventHandler<PerformanceControlState>? StateChanged;
    public event EventHandler<string>? LogMessage;

    public void RaiseStateChanged(PerformanceControlState? state = null)
    {
        StateChanged?.Invoke(this, state ?? BuildPerformanceControlState());
    }

    public void Log(string message)
    {
        string formatted = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message;
        LogMessage?.Invoke(this, formatted);
    }

    public void TrackEvent(string channel, string detail)
    {
        lock (DiagnosticsSync)
        {
            RecentEvents.Enqueue("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + channel + " " + detail);
            while (RecentEvents.Count > 20)
            {
                RecentEvents.Dequeue();
            }
        }
    }

    private PerformanceControlState BuildPerformanceControlState()
    {
        return new PerformanceControlState
        {
            Initialized = Initialized,
            Available = Available,
            CurrentModeKnown = CurrentModeKnown,
            CurrentMode = DescribeCurrentMode(),
            CurrentModeIsInferred = CurrentModeIsInferred,
            CurrentThermalMode = CurrentThermalMode.ToString(),
            MaxFanEnabled = MaxFanEnabled,
            CurrentLegacyFanMode = CurrentLegacyFanMode.ToString(),
            CurrentFanMinimumRpm = GetConfiguredFanMinimumRpm(),
            FanMinimumOverrideRpm = FanMinimumOverrideRpm,
            CurrentGraphicsMode = CurrentGraphicsMode.ToString(),
            GraphicsModeSwitchSupported = GraphicsModeSwitchSupported,
            GraphicsSupportsUma = GraphicsSupportsUma,
            GraphicsSupportsHybrid = GraphicsSupportsHybrid,
            GraphicsNeedsReboot = GraphicsNeedsReboot,
            GraphicsModeSwitchBits = GraphicsModeSwitchBits,
            LastGraphicsRequestMode = LastGraphicsRequestMode,
            LastGraphicsRequestReturnCode = LastGraphicsRequestReturnCode,
            ExtremeUnlocked = ExtremeUnlocked,
            UnleashVisible = UnleashVisible,
            ThermalUiType = ThermalUiType.ToString(),
            SupportModes = SupportModes.ToArray()
        };
    }

    private string DescribeCurrentMode()
    {
        if (CurrentModeKnown || CurrentModeIsInferred)
        {
            return OmenHelper.Domain.Firmware.PerformanceModeFirmwareMap.FormatDisplayName(CurrentMode);
        }

        return "Unknown";
    }

    private int GetConfiguredFanMinimumRpm()
    {
        return FanMinimumOverrideRpm.HasValue ? FanMinimumOverrideRpm.Value : OmenHelper.Domain.Firmware.PerformanceModeFirmwareMap.GetFanMinimumRpm(CurrentMode);
    }
}
