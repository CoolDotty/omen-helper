using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hp.Bridge.Client.SDKs.PerformanceControl.Enums;
using HP.Omen.Core.Common.PowerControl.Enum;
using HP.Omen.Core.Model.DataStructure.Modules.FanControl.Enums;
using HP.Omen.Core.Model.DataStructure.Modules.GraphicsSwitcher.Enums;
using OmenHelper.Application.Diagnostics;
using OmenHelper.Application.Services;
using OmenHelper.Application.State;
using OmenHelper.Domain.Fan;
using OmenHelper.Domain.Firmware;
using OmenHelper.Domain.Graphics;
using OmenHelper.Infrastructure.Bios;
using OmenHelper.Infrastructure.Persistence;
using OmenHelper.Infrastructure.Telemetry;

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
    private BiosFanTelemetryService _fanTelemetryService;
    private bool _fanTelemetryServiceAttempted;
    private LibreHardwareTemperatureService _temperatureTelemetryService;
    private bool _temperatureTelemetryServiceAttempted;
    private CancellationTokenSource _workerCancellation;
    private Task _workerTask;

    public OmenSessionController()
    {
        _state = new SharedSessionState();
        _biosClient = new OmenBiosClient();
        _stateStore = new LocalStateStore();
        _diagnosticsReportBuilder = new DiagnosticsReportBuilder();
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

        _state.SupportModes = new List<string>
        {
            PerformanceModeFirmwareMap.FormatDisplayName(PerformanceMode.Default),
            PerformanceModeFirmwareMap.FormatDisplayName(PerformanceMode.Performance),
            PerformanceModeFirmwareMap.FormatDisplayName(PerformanceMode.Eco),
            PerformanceModeFirmwareMap.FormatDisplayName(PerformanceMode.Extreme)
        };
        _state.CurrentModeKnown = false;
        _state.CurrentModeIsInferred = false;
        _performanceModeService.LoadPowerModePreferences();
        _fanControlService.LoadFanMinimumPreference();
        _fanControlService.LoadFanCurveStore();
        _fanControlService.RefreshActiveCurveState();

        _state.Started = true;
        _workerCancellation = new CancellationTokenSource();
        _workerTask = RunWorkerLoopAsync(_workerCancellation.Token);

        _ = RequestInitializationAsync();

        _state.Log("Started control path: BIOS/WMI only. HP pipe support removed.");
        _state.Log("Process: " + (Environment.Is64BitProcess ? "x64" : "x86") + ", OS: " + (Environment.Is64BitOperatingSystem ? "x64" : "x86"));
        _state.Log("Elevation: " + (IsAdministrator() ? "admin" : "not-admin"));
    }

    public async Task RequestInitializationAsync()
    {
        try
        {
            bool maxFanEnabled = await _fanControlService.GetMaxFanAsync().ConfigureAwait(false);
            _graphicsModeService.RefreshGraphicsMode();
            _graphicsModeService.RefreshGraphicsSupport();
            await RefreshHardwareTelemetryInternalAsync().ConfigureAwait(false);
            await _performanceModeService.RefreshPerformanceStatusBlobAsync().ConfigureAwait(false);
            if (_state.FanCurveRuntimeEnabled)
            {
                await _fanControlService.ApplyCurrentCurveImmediatelyAsync("startup curve activation").ConfigureAwait(false);
            }
            else
            {
                await _fanControlService.ApplyCurrentFanTargetAsync("startup fan target activation").ConfigureAwait(false);
            }

            await _performanceModeService.RefreshPerformanceStatusBlobAsync().ConfigureAwait(false);

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
        await RefreshHardwareTelemetryInternalAsync().ConfigureAwait(false);
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

        List<string> recentEvents;
        lock (_state.DiagnosticsSync)
        {
            recentEvents = new List<string>(_state.RecentEvents);
        }

        IReadOnlyList<string> fanSensorLines = GetFanSensorLines();

        DiagnosticsReportSnapshot snapshot = new DiagnosticsReportSnapshot
        {
            SessionId = _state.SessionId,
            Initialized = _state.Initialized,
            Available = _state.Available,
            CurrentMode = DescribeCurrentMode(),
            CurrentModeIsInferred = _state.CurrentModeIsInferred,
            CurrentThermalMode = _state.CurrentThermalMode.ToString(),
            CurrentLegacyFanMode = _state.CurrentLegacyFanMode.ToString(),
            CpuFanRpm = _state.CpuFanRpm.HasValue ? _state.CpuFanRpm.Value.ToString("N0") : "<none>",
            GpuFanRpm = _state.GpuFanRpm.HasValue ? _state.GpuFanRpm.Value.ToString("N0") : "<none>",
            FanRpmSource = string.IsNullOrWhiteSpace(_state.FanRpmSource) ? "<none>" : _state.FanRpmSource,
            FanRpmSummary = DescribeFanRpmSummary(_state.CpuFanRpm, _state.GpuFanRpm),
            FanRpmReadSucceeded = _state.FanRpmReadSucceeded,
            CpuTemperatureC = _state.CpuTemperatureC.HasValue ? _state.CpuTemperatureC.Value.ToString("0.0") : "<none>",
            GpuTemperatureC = _state.GpuTemperatureC.HasValue ? _state.GpuTemperatureC.Value.ToString("0.0") : "<none>",
            ChassisTemperatureC = _state.ChassisTemperatureC.HasValue ? _state.ChassisTemperatureC.Value.ToString("0.0") : "<none>",
            AveragedCpuTemperatureC = _state.AveragedCpuTemperatureC.HasValue ? _state.AveragedCpuTemperatureC.Value.ToString("0.0") : "<none>",
            AveragedGpuTemperatureC = _state.AveragedGpuTemperatureC.HasValue ? _state.AveragedGpuTemperatureC.Value.ToString("0.0") : "<none>",
            AveragedChassisTemperatureC = _state.AveragedChassisTemperatureC.HasValue ? _state.AveragedChassisTemperatureC.Value.ToString("0.0") : "<none>",
            PooledTelemetryTimestampUtc = _state.PooledTelemetryTimestampUtc.HasValue ? _state.PooledTelemetryTimestampUtc.Value.ToString("O") : "<none>",
            TemperatureSource = string.IsNullOrWhiteSpace(_state.TemperatureSource) ? "<none>" : _state.TemperatureSource,
            TemperatureReadSucceeded = _state.TemperatureReadSucceeded,
            FanMinimumRpm = GetConfiguredFanMinimumRpm().ToString(),
            FanMinimumOverrideRpm = _state.FanMinimumOverrideRpm.HasValue ? _state.FanMinimumOverrideRpm.Value.ToString() : "<none>",
            FanCurveRuntimeEnabled = _state.FanCurveRuntimeEnabled,
            ActiveFanCurveMode = _state.ActiveFanCurveMode,
            FanCurveHysteresisRiseDeltaC = _state.FanCurveHysteresisRiseDeltaC,
            FanCurveHysteresisDropDeltaC = _state.FanCurveHysteresisDropDeltaC,
            GpuCurveLinked = _state.GpuCurveLinked,
            DesiredCpuRpm = _state.CurveDesiredCpuRpm.ToString(),
            DesiredGpuRpm = _state.CurveDesiredGpuRpm.ToString(),
            AppliedCpuRpm = _state.CurveAppliedCpuRpm.ToString(),
            AppliedGpuRpm = _state.CurveAppliedGpuRpm.ToString(),
            ChassisOverrideChangedTarget = _state.CurveChassisOverrideUsed,
            CpuHysteresisAnchorTemperatureC = _state.CpuHysteresisAnchorTemperatureC.HasValue ? _state.CpuHysteresisAnchorTemperatureC.Value.ToString("0.0") : "<none>",
            GpuHysteresisAnchorTemperatureC = _state.GpuHysteresisAnchorTemperatureC.HasValue ? _state.GpuHysteresisAnchorTemperatureC.Value.ToString("0.0") : "<none>",
            ChassisHysteresisAnchorTemperatureC = _state.ChassisHysteresisAnchorTemperatureC.HasValue ? _state.ChassisHysteresisAnchorTemperatureC.Value.ToString("0.0") : "<none>",
            LastCurveWriteTimestampUtc = _state.LastCurveWriteTimestampUtc.HasValue ? _state.LastCurveWriteTimestampUtc.Value.ToString("O") : "<none>",
            LastCurveWriteReason = string.IsNullOrWhiteSpace(_state.LastCurveWriteReason) ? "<none>" : _state.LastCurveWriteReason,
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
            FanSensorLines = fanSensorLines,
            RecentEvents = recentEvents
        };

        return _diagnosticsReportBuilder.Build(snapshot);
    }

    public async Task SetPerformanceModeAsync(PerformanceMode mode)
    {
        await _performanceModeService.SetPerformanceModeAsync(mode).ConfigureAwait(false);
        await _fanControlService.OnPerformanceModeChangedAsync().ConfigureAwait(false);
        _state.RaiseStateChanged();
    }

    public Task SetThermalModeAsync(ThermalControl thermalControl) => _performanceModeService.SetThermalModeAsync(thermalControl);
    public Task<bool> GetMaxFanAsync() => _fanControlService.GetMaxFanAsync();
    public Task SetMaxFanAsync(bool enabled) => _fanControlService.SetMaxFanAsync(enabled);
    public Task SetLegacyFanModeAsync(FanMode mode) => _fanControlService.SetLegacyFanModeAsync(mode);
    public int? GetFanMinimumOverrideRpm() => _fanControlService.GetFanMinimumOverrideRpm();
    public int GetEffectiveFanMinimumRpm() => _fanControlService.GetEffectiveFanMinimumRpm();
    public IReadOnlyList<int> GetFanMinimumOptions() => _fanControlService.GetFanMinimumOptions();
    public Task SetFanMinimumOverrideRpmAsync(int? rpm) => _fanControlService.SetFanMinimumOverrideRpmAsync(rpm);
    public Task<bool> SetGraphicsModeAsync(GraphicsSwitcherMode mode) => _graphicsModeService.SetGraphicsModeAsync(mode);
    public PerformanceMode? GetBatteryPowerModePreference() => _performanceModeService.GetBatteryPowerModePreference();
    public PerformanceMode? GetPluggedInPowerModePreference() => _performanceModeService.GetPluggedInPowerModePreference();
    public void SetBatteryPowerModePreference(PerformanceMode? mode) => _performanceModeService.SetBatteryPowerModePreference(mode);
    public void SetPluggedInPowerModePreference(PerformanceMode? mode) => _performanceModeService.SetPluggedInPowerModePreference(mode);
    public Task SyncPowerSourcePerformanceModeAsync(bool pluggedIn) => _performanceModeService.SyncPowerSourcePerformanceModeAsync(pluggedIn);
    public void SetFanCurveRuntimeEnabled(bool enabled) => _fanControlService.SetCurveRuntimeEnabled(enabled);
    public void SetFanCurveGpuLinked(bool linked) => _fanControlService.SetGpuLinked(linked);
    public Task SetFanCurveHysteresisAsync(int riseDeltaC, int dropDeltaC) => _fanControlService.SetFanCurveHysteresisAsync(riseDeltaC, dropDeltaC);
    public void SetFanCurveProfile(FanCurveKind kind, FanCurveProfile profile) => _fanControlService.SetCurveProfile(kind, profile);

    public void Dispose()
    {
        if (_state.Disposed)
        {
            return;
        }

        _state.Disposed = true;
        try
        {
            _workerCancellation?.Cancel();
            _workerTask?.Wait(2000);
        }
        catch
        {
        }

        _temperatureTelemetryService?.Dispose();
        _fanTelemetryService?.Dispose();
        _biosClient?.Dispose();
    }

    private IReadOnlyList<string> GetFanSensorLines()
    {
        try
        {
            List<string> lines = new List<string>();
            BiosFanTelemetryService telemetryService = EnsureFanTelemetryService();
            if (telemetryService != null)
            {
                BiosFanTelemetryService.FanTelemetrySnapshot snapshot = telemetryService.GetFanTelemetrySnapshot();
                if (snapshot?.Lines != null)
                {
                    lines.AddRange(snapshot.Lines);
                }
            }

            return lines;
        }
        catch (Exception ex)
        {
            _state.Log("Fan telemetry unavailable: " + ex.Message);
            return Array.Empty<string>();
        }
    }

    private BiosFanTelemetryService EnsureFanTelemetryService()
    {
        if (_fanTelemetryServiceAttempted)
        {
            return _fanTelemetryService;
        }

        _fanTelemetryServiceAttempted = true;
        try
        {
            _fanTelemetryService = new BiosFanTelemetryService(_biosClient);
        }
        catch (Exception ex)
        {
            _fanTelemetryService = null;
            _state.Log("BIOS fan telemetry init failed: " + ex.Message);
        }

        return _fanTelemetryService;
    }

    private LibreHardwareTemperatureService EnsureTemperatureTelemetryService()
    {
        if (_temperatureTelemetryServiceAttempted)
        {
            return _temperatureTelemetryService;
        }

        _temperatureTelemetryServiceAttempted = true;
        try
        {
            _temperatureTelemetryService = new LibreHardwareTemperatureService();
        }
        catch (Exception ex)
        {
            _temperatureTelemetryService = null;
            _state.Log("LibreHardwareMonitor temperature telemetry init failed: " + ex.Message);
        }

        return _temperatureTelemetryService;
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

    public Task RefreshHardwareTelemetryAsync()
    {
        return RefreshHardwareTelemetryInternalAsync();
    }

    public async Task<string> ProbeTemperatureSelectorAsync(byte[] inputData, string label)
    {
        return await ProbeTemperatureCommandAsync(35, inputData, label, 4).ConfigureAwait(false);
    }

    public async Task<string> ProbeTemperatureSelectorAsync(byte[] inputData, string label, int returnDataSize)
    {
        return await ProbeTemperatureCommandAsync(35, inputData, label, returnDataSize).ConfigureAwait(false);
    }

    public async Task<string> ProbeTemperatureCommandAsync(int commandType, byte[] inputData, string label, int returnDataSize)
    {
        if (inputData == null || inputData.Length != 4)
        {
            throw new ArgumentException("Temperature probe input must be 4 bytes.", nameof(inputData));
        }

        OmenBiosClient.BiosWmiResult result = await _biosClient.ProbeTemperatureCommandAsync(commandType, inputData, returnDataSize).ConfigureAwait(false);
        string message = $"Temp probe cmd={commandType} {label}: input={BitConverter.ToString(inputData)} out={returnDataSize} exec={result.ExecuteResult} rc={result.ReturnCode} data={BitConverter.ToString(result.ReturnData ?? Array.Empty<byte>())}";
        _state.Log(message);
        _state.TrackEvent("TempProbe", message);
        _state.RaiseStateChanged();
        return message;
    }

    private async Task RunWorkerLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            DateTime startedUtc = DateTime.UtcNow;
            try
            {
                await RefreshHardwareTelemetryInternalAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _state.Log("Telemetry worker tick failed: " + ex.Message);
            }

            try
            {
                await _fanControlService.MaintainFanTargetKeepaliveAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _state.Log("Fan target keepalive failed: " + ex.Message);
            }

            try
            {
                await RunFirmwareHeartbeatAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _state.Log("Firmware heartbeat failed: " + ex.Message);
            }

            TimeSpan remaining = TimeSpan.FromSeconds(1) - (DateTime.UtcNow - startedUtc);
            if (remaining > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task RefreshHardwareTelemetryInternalAsync()
    {
        try
        {
            BiosFanTelemetryService fanTelemetryService = EnsureFanTelemetryService();
            LibreHardwareTemperatureService temperatureTelemetryService = EnsureTemperatureTelemetryService();
            if (fanTelemetryService == null || temperatureTelemetryService == null)
            {
                return;
            }

            Task<BiosFanTelemetryService.FanTelemetrySnapshot> fanTask = Task.Run(() => fanTelemetryService.GetFanTelemetrySnapshot());
            Task<LibreHardwareTemperatureService.TemperatureTelemetrySnapshot> tempTask = Task.Run(() => temperatureTelemetryService.GetTemperatureTelemetrySnapshot(_biosClient));
            await Task.WhenAll(fanTask, tempTask).ConfigureAwait(false);

            BiosFanTelemetryService.FanTelemetrySnapshot fanSnapshot = fanTask.Result;
            LibreHardwareTemperatureService.TemperatureTelemetrySnapshot tempSnapshot = tempTask.Result;
            HardwareTelemetrySnapshot snapshot = new HardwareTelemetrySnapshot
            {
                TimestampUtc = DateTime.UtcNow,
                CpuFanRpm = fanSnapshot?.CpuFanRpm,
                GpuFanRpm = fanSnapshot?.GpuFanRpm,
                FanSource = !string.IsNullOrWhiteSpace(fanSnapshot?.Source) ? fanSnapshot.Source : "BIOS/WMI",
                FanReadSucceeded = fanSnapshot != null && fanSnapshot.IsAvailable,
                CpuTemperatureC = tempSnapshot?.CpuTemperatureC,
                GpuTemperatureC = tempSnapshot?.GpuTemperatureC,
                ChassisTemperatureC = tempSnapshot?.ChassisTemperatureC,
                TemperatureSource = !string.IsNullOrWhiteSpace(tempSnapshot?.Source) ? tempSnapshot.Source : "LibreHardwareMonitor + BIOS/WMI",
                TemperatureReadSucceeded = tempSnapshot != null && tempSnapshot.IsAvailable
            };

            lock (_state.DiagnosticsSync)
            {
                _state.CpuFanRpm = snapshot.CpuFanRpm;
                _state.GpuFanRpm = snapshot.GpuFanRpm;
                _state.FanRpmSource = snapshot.FanSource;
                _state.FanRpmReadSucceeded = snapshot.FanReadSucceeded;
                _state.CpuTemperatureC = snapshot.CpuTemperatureC;
                _state.GpuTemperatureC = snapshot.GpuTemperatureC;
                _state.ChassisTemperatureC = snapshot.ChassisTemperatureC;
                _state.TemperatureSource = snapshot.TemperatureSource;
                _state.TemperatureReadSucceeded = snapshot.TemperatureReadSucceeded;
            }

            if (!string.IsNullOrWhiteSpace(tempSnapshot?.Error))
            {
                _state.Log("Temperature telemetry snapshot error: " + tempSnapshot.Error);
            }

            await _fanControlService.ProcessTelemetryTickAsync(snapshot).ConfigureAwait(false);

            _state.TrackEvent(
                "Telemetry",
                "Temp source=" + (string.IsNullOrWhiteSpace(_state.TemperatureSource) ? "<none>" : _state.TemperatureSource) +
                ", cpu=" + FormatTemperature(_state.CpuTemperatureC) +
                ", gpu=" + FormatTemperature(_state.GpuTemperatureC) +
                ", chassis=" + FormatTemperature(_state.ChassisTemperatureC) +
                ", avgCpu=" + FormatTemperature(_state.AveragedCpuTemperatureC) +
                ", avgGpu=" + FormatTemperature(_state.AveragedGpuTemperatureC));
            _state.RaiseStateChanged();
        }
        catch (Exception ex)
        {
            _state.Log("Hardware telemetry refresh failed: " + ex.Message);
        }
    }

    private async Task RunFirmwareHeartbeatAsync()
    {
        if (!_state.Initialized || !_state.Available)
        {
            return;
        }

        byte[] zeroInput = new byte[4];

        OmenBiosClient.BiosWmiResult type16 = await _biosClient.ExecuteAsync(
            command: 131080,
            commandType: 16,
            inputData: zeroInput,
            returnDataSize: 4).ConfigureAwait(false);

        OmenBiosClient.BiosWmiResult type35 = await _biosClient.ExecuteAsync(
            command: 131080,
            commandType: 35,
            inputData: zeroInput,
            returnDataSize: 4).ConfigureAwait(false);

        OmenBiosClient.BiosWmiResult type45 = await _biosClient.ExecuteAsync(
            command: 131080,
            commandType: 45,
            inputData: zeroInput,
            returnDataSize: 128).ConfigureAwait(false);

        _state.Log("Heartbeat BIOS 131080/16: exec=" + type16.ExecuteResult + " rc=" + type16.ReturnCode + " out=" + FormatReturnBytes(type16.ReturnData, 4));
        _state.Log("Heartbeat BIOS 131080/35: exec=" + type35.ExecuteResult + " rc=" + type35.ReturnCode + " out=" + FormatReturnBytes(type35.ReturnData, 4));
        _state.Log("Heartbeat BIOS 131080/45: exec=" + type45.ExecuteResult + " rc=" + type45.ReturnCode + " out=" + FormatReturnBytes(type45.ReturnData, 16));
        _state.TrackEvent(
            "Heartbeat",
            "16 rc=" + type16.ReturnCode + ", 35 rc=" + type35.ReturnCode + ", 45 rc=" + type45.ReturnCode + ", 45 preview=" + FormatReturnBytes(type45.ReturnData, 16));
    }

    private static string FormatReturnBytes(byte[] data, int take)
    {
        if (data == null || data.Length == 0)
        {
            return "<empty>";
        }

        int count = Math.Min(Math.Max(take, 1), data.Length);
        string prefix = BitConverter.ToString(data, 0, count);
        return data.Length <= count ? prefix : prefix + "...(len=" + data.Length + ")";
    }

    private static string FormatTemperature(double? temperatureC)
    {
        return temperatureC.HasValue ? temperatureC.Value.ToString("0.0") + "°C" : "<none>";
    }

    private int GetConfiguredFanMinimumRpm()
    {
        return _state.FanMinimumOverrideRpm.HasValue ? _state.FanMinimumOverrideRpm.Value : PerformanceModeFirmwareMap.GetFanMinimumRpm(_state.CurrentMode);
    }

    private static string DescribeFanRpmSummary(int? cpuFanRpm, int? gpuFanRpm)
    {
        if (!cpuFanRpm.HasValue && !gpuFanRpm.HasValue)
        {
            return "<unavailable>";
        }

        string cpuText = cpuFanRpm.HasValue ? "CPU " + cpuFanRpm.Value.ToString("N0") + " RPM" : "CPU <unavailable>";
        string gpuText = gpuFanRpm.HasValue ? "GPU " + gpuFanRpm.Value.ToString("N0") + " RPM" : "GPU <unavailable>";
        return cpuText + " | " + gpuText;
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
