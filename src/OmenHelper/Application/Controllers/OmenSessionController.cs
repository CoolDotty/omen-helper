using System;
using System.Threading.Tasks;
using Hp.Bridge.Client.SDKs.PerformanceControl.Enums;
using HP.Omen.Core.Common.PowerControl.Enum;
using HP.Omen.Core.Common.Utilities.SystemAdvPerformanceHelper.CommonUtilities;
using HP.Omen.Core.Model.DataStructure.Modules.FanControl.Enums;
using HP.Omen.Core.Model.DataStructure.Modules.GraphicsSwitcher.Enums;
using OmenHelper.Application.Diagnostics;
using OmenHelper.Application.Services;
using OmenHelper.Application.State;
using OmenHelper.Domain.Firmware;
using OmenHelper.Domain.Graphics;
using OmenHelper.Infrastructure.Bios;
using OmenHelper.Infrastructure.Persistence;

namespace OmenHelper.Application.Controllers;

internal sealed class OmenSessionController : IDisposable
{
    private readonly SharedSessionState _state;
    private readonly OmenBiosClient _biosClient;
    private readonly LocalStateStore _stateStore;
    private readonly DiagnosticsReportBuilder _diagnosticsReportBuilder;
    private readonly PerformanceModeService _performanceModeService;
    private readonly FanControlService _fanControlService;
    private readonly GraphicsModeService _graphicsModeService;

    public OmenSessionController()
    {
        _state = new SharedSessionState();
        _biosClient = new OmenBiosClient();
        _stateStore = new LocalStateStore();
        _diagnosticsReportBuilder = new DiagnosticsReportBuilder();

        // Note: FanControlService is created first since PerformanceModeService depends on it
        _fanControlService = new FanControlService(_state, _biosClient, _stateStore);
        _graphicsModeService = new GraphicsModeService(_state, _biosClient);
        _performanceModeService = new PerformanceModeService(_state, _biosClient, _stateStore, _fanControlService);
    }

    public event EventHandler<PerformanceControlState> StateChanged
    {
        add { _state.StateChanged += value; }
        remove { _state.StateChanged -= value; }
    }

    public event EventHandler<string> LogMessage
    {
        add { _state.LogMessage += value; }
        remove { _state.LogMessage -= value; }
    }

    public void Start()
    {
        if (_state.Started)
        {
            return;
        }

        try
        {
            _biosClient.Initialize();
            _state.Log("BIOS WMI client initialized.");
        }
        catch (Exception ex)
        {
            _state.Available = false;
            _state.Log("BIOS WMI init failed: " + ex.Message);
        }

        _state.SupportModes = new System.Collections.Generic.List<string>
        {
            PerformanceModeFirmwareMap.FormatDisplayName(PerformanceMode.Default),
            PerformanceModeFirmwareMap.FormatDisplayName(PerformanceMode.Performance),
            PerformanceModeFirmwareMap.FormatDisplayName(PerformanceMode.Eco),
            PerformanceModeFirmwareMap.FormatDisplayName(PerformanceMode.Extreme)
        };
        _state.CurrentModeKnown = false;
        _performanceModeService.LoadRememberedPerformanceMode();
        _performanceModeService.LoadPowerModePreferences();
        _fanControlService.LoadFanMinimumPreference();

        _state.Started = true;

        _ = RequestInitializationAsync();

        _state.Log("Started control path: BIOS/WMI only. HP pipe support removed.");
        _state.Log("Process: " + (Environment.Is64BitProcess ? "x64" : "x86") + ", OS: " + (Environment.Is64BitOperatingSystem ? "x64" : "x86"));
        _state.Log("Elevation: " + (IsAdministrator() ? "admin" : "not-admin"));

        try
        {
            bool driverInstalled = CommonUtility.IsDriverInstalled();
            bool driverMeetsMin = CommonUtility.IsDriverMeetMinReq();
            _state.Log("HpReadHWData: installed=" + driverInstalled + " meetsMin=" + driverMeetsMin);
        }
        catch (Exception ex)
        {
            _state.Log("HpReadHWData check failed: " + ex.Message);
        }
    }

    public async Task RequestInitializationAsync()
    {
        try
        {
            bool maxFanEnabled = await _fanControlService.GetMaxFanAsync().ConfigureAwait(false);
            _graphicsModeService.RefreshGraphicsMode();
            _graphicsModeService.RefreshGraphicsSupport();
            await _performanceModeService.RefreshPerformanceStatusBlobAsync().ConfigureAwait(false);
            if (_state.FanMinimumOverrideRpm.HasValue)
            {
                await _fanControlService.ApplyFanMinimumBlobAsync("startup preference").ConfigureAwait(false);
                await _performanceModeService.RefreshPerformanceStatusBlobAsync().ConfigureAwait(false);
            }
            _state.Initialized = true;
            _state.Available = true;
            _state.TrackEvent("Firmware", "Read max fan=" + (maxFanEnabled ? "On" : "Off"));
            _state.RaiseStateChanged();
        }
        catch (Exception ex)
        {
            _state.Initialized = false;
            _state.Available = false;
            _state.Log("Firmware state refresh failed: " + ex.Message);
            _state.RaiseStateChanged();
        }
    }

    public async Task<string> BuildDiagnosticsReportAsync()
    {
        await _performanceModeService.RefreshPerformanceStatusBlobAsync().ConfigureAwait(false);

        byte[] systemDesignData = Array.Empty<byte>();
        SystemDesignDataInfo systemDesignDataInfo = SystemDesignDataInfo.Empty;
        string maxFanText = "<none>";

        try
        {
            systemDesignData = await _biosClient.GetSystemDesignDataAsync().ConfigureAwait(false);
            systemDesignDataInfo = await _biosClient.GetSystemDesignDataInfoAsync().ConfigureAwait(false);
            _state.GraphicsModeSwitchBits = systemDesignDataInfo.RawGpuModeSwitch;
            _state.GraphicsModeSwitchReadSucceeded = systemDesignDataInfo.ReadSucceeded;
            _graphicsModeService.RefreshGraphicsSupportFromProbe(systemDesignDataInfo);
            _graphicsModeService.RefreshGraphicsMode();
            _state.RaiseStateChanged();

            try
            {
                maxFanText = (await _fanControlService.GetMaxFanAsync().ConfigureAwait(false)) ? "On" : "Off";
            }
            catch (Exception ex)
            {
                maxFanText = "error: " + ex.Message;
            }
        }
        catch (Exception ex)
        {
            maxFanText = "error: " + ex.Message;
        }

        System.Collections.Generic.List<string> recentEvents;
        lock (_state.DiagnosticsSync)
        {
            recentEvents = new System.Collections.Generic.List<string>(_state.RecentEvents);
        }

        DiagnosticsReportSnapshot snapshot = new DiagnosticsReportSnapshot
        {
            SessionId = _state.SessionId,
            Initialized = _state.Initialized,
            Available = _state.Available,
            CurrentMode = DescribeCurrentMode(),
            CurrentModeIsInferred = _state.CurrentModeIsInferred,
            CurrentThermalMode = _state.CurrentThermalMode.ToString(),
            CurrentLegacyFanMode = _state.CurrentLegacyFanMode.ToString(),
            FanMinimumRpm = GetConfiguredFanMinimumRpm().ToString(),
            FanMinimumOverrideRpm = _state.FanMinimumOverrideRpm.HasValue ? _state.FanMinimumOverrideRpm.Value.ToString() : "<none>",
            CurrentGraphicsMode = _state.CurrentGraphicsMode.ToString(),
            LastPerformanceRequestMode = string.IsNullOrWhiteSpace(_state.LastPerformanceRequestMode) ? "<none>" : _state.LastPerformanceRequestMode,
            LastPerformanceRequestPath = string.IsNullOrWhiteSpace(_state.LastPerformanceRequestPath) ? "<none>" : _state.LastPerformanceRequestPath,
            LastPerfType26ExecuteResult = _state.LastPerfType26ExecuteResult.HasValue ? _state.LastPerfType26ExecuteResult.Value.ToString() : "<none>",
            LastPerfType26ReturnCode = _state.LastPerfType26ReturnCode.HasValue ? _state.LastPerfType26ReturnCode.Value.ToString() : "<none>",
            LastPerfType34ExecuteResult = _state.LastPerfType34ExecuteResult.HasValue ? _state.LastPerfType34ExecuteResult.Value.ToString() : "<none>",
            LastPerfType34ReturnCode = _state.LastPerfType34ReturnCode.HasValue ? _state.LastPerfType34ReturnCode.Value.ToString() : "<none>",
            LastPerfType41ExecuteResult = _state.LastPerfType41ExecuteResult.HasValue ? _state.LastPerfType41ExecuteResult.Value.ToString() : "<none>",
            LastPerfType41ReturnCode = _state.LastPerfType41ReturnCode.HasValue ? _state.LastPerfType41ReturnCode.Value.ToString() : "<none>",
            LastMaxFanExecuteResult = _state.LastMaxFanExecuteResult.HasValue ? _state.LastMaxFanExecuteResult.Value.ToString() : "<none>",
            LastMaxFanReturnCode = _state.LastMaxFanReturnCode.HasValue ? _state.LastMaxFanReturnCode.Value.ToString() : "<none>",
            PerformanceStatusBlobExecuteResult = _state.LastPerformanceStatusBlobExecuteResult.HasValue ? _state.LastPerformanceStatusBlobExecuteResult.Value.ToString() : "<none>",
            PerformanceStatusBlobReturnCode = _state.LastPerformanceStatusBlobReturnCode.HasValue ? _state.LastPerformanceStatusBlobReturnCode.Value.ToString() : "<none>",
            PerformanceStatusBlobHash = string.IsNullOrWhiteSpace(_state.LastPerformanceStatusBlobHash) ? "<none>" : _state.LastPerformanceStatusBlobHash,
            PerformanceStatusBlobPreview = string.IsNullOrWhiteSpace(_state.LastPerformanceStatusBlobPreview) ? "<none>" : _state.LastPerformanceStatusBlobPreview,
            PerformanceStatusBlobSensors = DescribePerformanceStatusBlobSensors(_state.LastPerformanceStatusBlob),
            PerformanceStatusBlobChangedBytes = _state.LastPerformanceStatusBlobChangedBytes.HasValue ? _state.LastPerformanceStatusBlobChangedBytes.Value.ToString() : "<n/a>",
            PreviousPerformanceStatusBlobHash = string.IsNullOrWhiteSpace(_state.PreviousPerformanceStatusBlobHash) ? "<none>" : _state.PreviousPerformanceStatusBlobHash,
            FanMinimumBlobExecuteResult = _state.LastFanTargetBlobExecuteResult.HasValue ? _state.LastFanTargetBlobExecuteResult.Value.ToString() : "<none>",
            FanMinimumBlobReturnCode = _state.LastFanTargetBlobReturnCode.HasValue ? _state.LastFanTargetBlobReturnCode.Value.ToString() : "<none>",
            FanMinimumBlobHash = string.IsNullOrWhiteSpace(_state.LastFanTargetBlobHash) ? "<none>" : _state.LastFanTargetBlobHash,
            FanMinimumBlobPreview = string.IsNullOrWhiteSpace(_state.LastFanTargetBlobPreview) ? "<none>" : _state.LastFanTargetBlobPreview,
            FanMinimumBlobChangedBytes = _state.LastFanTargetBlobChangedBytes.HasValue ? _state.LastFanTargetBlobChangedBytes.Value.ToString() : "<n/a>",
            PreviousFanMinimumBlobHash = string.IsNullOrWhiteSpace(_state.PreviousFanTargetBlobHash) ? "<none>" : _state.PreviousFanTargetBlobHash,
            ThermalUiType = _state.ThermalUiType.ToString(),
            ExtremeUnlocked = _state.ExtremeUnlocked,
            UnleashVisible = _state.UnleashVisible,
            SupportModes = string.Join(", ", _state.SupportModes),
            SystemDesignData = (systemDesignData != null && systemDesignData.Length > 0) ? BitConverter.ToString(systemDesignData) : "<empty>",
            ShippingAdapterPowerRating = Convert.ToString(HP.Omen.Core.Common.PowerControl.PowerControlHelper.ShippingAdapterPowerRating),
            IsBiosPerformanceModeSupport = Convert.ToString(HP.Omen.Core.Common.PowerControl.PowerControlHelper.IsBiosPerformanceModeSupport),
            IsSwFanControlSupport = Convert.ToString(HP.Omen.Core.Common.PowerControl.PowerControlHelper.IsSwFanControlSupport),
            IsExtremeModeSupport = Convert.ToString(HP.Omen.Core.Common.PowerControl.PowerControlHelper.IsExtremeModeSupport),
            IsExtremeModeUnlock = Convert.ToString(HP.Omen.Core.Common.PowerControl.PowerControlHelper.IsExtremeModeUnlock),
            GraphicsModeSwitchBits = "0x" + systemDesignDataInfo.RawGpuModeSwitch.ToString("X2"),
            GraphicsModeSwitchReadSucceeded = systemDesignDataInfo.ReadSucceeded,
            GraphicsModeSwitchSupported = systemDesignDataInfo.SupportsGraphicsSwitching,
            GraphicsModeSwitchRawSlots = systemDesignDataInfo.RawGraphicsModeSlots.ToString(),
            GraphicsModeSwitchSlots = systemDesignDataInfo.GraphicsModeSlots.ToString(),
            GraphicsModeSwitchHasIntegratedSlot = systemDesignDataInfo.HasIntegratedSlot,
            GraphicsModeSwitchHasHybridSlot = systemDesignDataInfo.HasHybridSlot,
            GraphicsModeSwitchHasDedicatedSlot = systemDesignDataInfo.HasDedicatedSlot,
            GraphicsModeSwitchHasOptimusSlot = systemDesignDataInfo.HasOptimusSlot,
            GraphicsSupportsHybrid = _state.GraphicsSupportsHybrid,
            GraphicsSupportsUma = _state.GraphicsSupportsUma,
            GraphicsNeedsReboot = _state.GraphicsNeedsReboot,
            LastGraphicsRequestMode = string.IsNullOrWhiteSpace(_state.LastGraphicsRequestMode) ? "<none>" : _state.LastGraphicsRequestMode,
            LastGraphicsRequestReturnCode = _state.LastGraphicsRequestReturnCode.HasValue ? _state.LastGraphicsRequestReturnCode.Value.ToString() : "<none>",
            MaxFanBios = maxFanText,
            RecentEvents = recentEvents
        };

        return _diagnosticsReportBuilder.Build(snapshot);
    }

    public Task SetPerformanceModeAsync(PerformanceMode mode) => _performanceModeService.SetPerformanceModeAsync(mode);

    public Task SetThermalModeAsync(ThermalControl thermalControl) => _performanceModeService.SetThermalModeAsync(thermalControl);

    public Task<bool> GetMaxFanAsync() => _fanControlService.GetMaxFanAsync();

    public Task SetMaxFanAsync(bool enabled) => _fanControlService.SetMaxFanAsync(enabled);

    public Task SetLegacyFanModeAsync(FanMode mode) => _fanControlService.SetLegacyFanModeAsync(mode);

    public int? GetFanMinimumOverrideRpm() => _fanControlService.GetFanMinimumOverrideRpm();

    public int GetEffectiveFanMinimumRpm() => _fanControlService.GetEffectiveFanMinimumRpm();

    public System.Collections.Generic.IReadOnlyList<int> GetFanMinimumOptions() => _fanControlService.GetFanMinimumOptions();

    public Task SetFanMinimumOverrideRpmAsync(int? rpm) => _fanControlService.SetFanMinimumOverrideRpmAsync(rpm);

    public Task<bool> SetGraphicsModeAsync(GraphicsSwitcherMode mode) => _graphicsModeService.SetGraphicsModeAsync(mode);

    public PerformanceMode? GetBatteryPowerModePreference() => _performanceModeService.GetBatteryPowerModePreference();

    public PerformanceMode? GetPluggedInPowerModePreference() => _performanceModeService.GetPluggedInPowerModePreference();

    public void SetBatteryPowerModePreference(PerformanceMode? mode) => _performanceModeService.SetBatteryPowerModePreference(mode);

    public void SetPluggedInPowerModePreference(PerformanceMode? mode) => _performanceModeService.SetPluggedInPowerModePreference(mode);

    public Task SyncPowerSourcePerformanceModeAsync(bool pluggedIn) => _performanceModeService.SyncPowerSourcePerformanceModeAsync(pluggedIn);

    public void Dispose()
    {
        if (_state.Disposed)
        {
            return;
        }

        _state.Disposed = true;
        _biosClient?.Dispose();
    }

    private static bool IsAdministrator()
    {
        try
        {
            System.Security.Principal.WindowsIdentity identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            System.Security.Principal.WindowsPrincipal principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private string DescribeCurrentMode()
    {
        if (_state.CurrentModeKnown || _state.CurrentModeIsInferred)
        {
            return PerformanceModeFirmwareMap.FormatDisplayName(_state.CurrentMode);
        }

        return "Unknown";
    }

    private int GetConfiguredFanMinimumRpm()
    {
        return _state.FanMinimumOverrideRpm.HasValue ? _state.FanMinimumOverrideRpm.Value : PerformanceModeFirmwareMap.GetFanMinimumRpm(_state.CurrentMode);
    }

    private static string DescribePerformanceStatusBlobSensors(byte[] blob)
    {
        if (blob == null || blob.Length == 0)
        {
            return "<empty>";
        }

        byte sensor0 = blob.Length > 0 ? blob[0] : (byte)0;
        byte sensor1 = blob.Length > 1 ? blob[1] : (byte)0;
        byte sensor2 = blob.Length > 2 ? blob[2] : (byte)0;

        return "S0=" + sensor0 + " (0x" + sensor0.ToString("X2") + ")"
            + ", S1=" + sensor1 + " (0x" + sensor1.ToString("X2") + ")"
            + ", S2=" + sensor2 + " (0x" + sensor2.ToString("X2") + ")";
    }
}
