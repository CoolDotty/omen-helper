using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Hp.Bridge.Client.SDKs.PerformanceControl.Enums;
using HP.Omen.Core.Common.PowerControl.Enum;
using HP.Omen.Core.Model.DataStructure.Modules.FanControl.Enums;
using OmenHelper.Application.State;
using OmenHelper.Domain.Fan;
using OmenHelper.Domain.Firmware;
using OmenHelper.Infrastructure.Bios;
using OmenHelper.Infrastructure.Persistence;

namespace OmenHelper.Application.Services;

internal sealed class FanControlService
{
    private readonly SharedSessionState _state;
    private readonly OmenBiosClient _biosClient;
    private readonly LocalStateStore _stateStore;
    private readonly Queue<double> _cpuTemperatureWindow = new Queue<double>();
    private readonly Queue<double> _gpuTemperatureWindow = new Queue<double>();
    private readonly Queue<double> _chassisTemperatureWindow = new Queue<double>();
    private readonly FanCurveRuntimeState _runtimeState = new FanCurveRuntimeState();
    private readonly object _curveSync = new object();

    public FanControlService(
        SharedSessionState state,
        OmenBiosClient biosClient,
        LocalStateStore stateStore)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _biosClient = biosClient ?? throw new ArgumentNullException(nameof(biosClient));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
    }

    public async Task<bool> GetMaxFanAsync()
    {
        bool maxFanEnabled = await _biosClient.GetMaxFanEnabledAsync().ConfigureAwait(false);
        _state.MaxFanEnabled = maxFanEnabled;
        _state.CurrentLegacyFanMode = maxFanEnabled ? FanMode.Turbo : FanMode.Normal;
        _state.CurrentThermalMode = maxFanEnabled ? ThermalControl.Max : ThermalControl.Auto;
        return maxFanEnabled;
    }

    public async Task SetMaxFanAsync(bool enabled)
    {
        bool success = await TrySetMaxFanAsync(enabled).ConfigureAwait(false);
        if (!success)
        {
            _state.Log("Max fan change failed (firmware only).");
            return;
        }

        _state.MaxFanEnabled = enabled;
        _state.CurrentLegacyFanMode = enabled ? FanMode.Turbo : FanMode.Normal;
        _state.CurrentThermalMode = enabled ? ThermalControl.Max : ThermalControl.Auto;

        if (enabled)
        {
            _state.LastCurveWriteReason = "Max fan enabled; curve runtime suspended.";
        }
        else
        {
            _runtimeState.ResetAnchors();
            await ApplyCurrentFanTargetAsync("max fan disabled").ConfigureAwait(false);
        }

        await RefreshPerformanceStatusBlobAsync().ConfigureAwait(false);
        _state.RaiseStateChanged();
    }

    public async Task SetLegacyFanModeAsync(FanMode mode)
    {
        await SetMaxFanAsync(mode == FanMode.Turbo).ConfigureAwait(false);
    }

    public int? GetFanMinimumOverrideRpm()
    {
        return _state.FanMinimumOverrideRpm;
    }

    public int GetEffectiveFanMinimumRpm()
    {
        return _state.FanMinimumOverrideRpm ?? 0;
    }

    public IReadOnlyList<int> GetFanMinimumOptions()
    {
        return new[] { 0, 1300, 1500, 2500, 3500, 4500, 5500, 6500 };
    }

    public async Task SetFanMinimumOverrideRpmAsync(int? rpm)
    {
        if (rpm.HasValue && !IsAllowedFanMinimumRpm(rpm.Value))
        {
            _state.Log("Ignored unsupported fan minimum RPM: " + rpm.Value + ".");
            return;
        }

        int? previousOverride = _state.FanMinimumOverrideRpm;
        _state.FanMinimumOverrideRpm = rpm;

        if (!rpm.HasValue)
        {
            SaveFanMinimumPreference();
            _state.Log("Debug fan minimum selection cleared.");
            _runtimeState.ResetAnchors();
            _state.RaiseStateChanged();
            return;
        }

        _state.LastCurveWriteReason = "Debug fan minimum override active; curve runtime suspended.";
        if (!await ApplyFanTargetsAsync(rpm.Value, rpm.Value, "fan minimum debug selection").ConfigureAwait(false))
        {
            _state.FanMinimumOverrideRpm = previousOverride;
            _state.Log("Fan minimum selection failed; reverted to previous value.");
            _state.RaiseStateChanged();
            return;
        }

        SaveFanMinimumPreference();
        await RefreshPerformanceStatusBlobAsync().ConfigureAwait(false);
        _state.RaiseStateChanged();
    }

    public void LoadFanMinimumPreference()
    {
        try
        {
            int? rpm;
            if (!_stateStore.TryLoadFanMinimumPreference(out rpm))
            {
                return;
            }

            _state.FanMinimumOverrideRpm = rpm;
            _state.Log("Loaded fan minimum preference: " + FormatFanMinimumPreference(_state.FanMinimumOverrideRpm));
        }
        catch (Exception ex)
        {
            _state.Log("Fan minimum preference load failed: " + ex.Message);
        }
    }

    public void SaveFanMinimumPreference()
    {
        try
        {
            _stateStore.SaveFanMinimumPreference(_state.FanMinimumOverrideRpm);
        }
        catch (Exception ex)
        {
            _state.Log("Fan minimum preference save failed: " + ex.Message);
        }
    }

    public void LoadFanCurveStore()
    {
        try
        {
            FanCurveStore store;
            if (!_stateStore.TryLoadFanCurveStore(out store))
            {
                store = FanCurveDefaults.CreateDefaultStore();
                _stateStore.SaveFanCurveStore(store);
                _state.Log("Created default fan curve store.");
            }

            ApplyFanCurveStore(store, "load");
        }
        catch (Exception ex)
        {
            _state.Log("Fan curve store load failed: " + ex.Message);
            ApplyFanCurveStore(FanCurveDefaults.CreateDefaultStore(), "fallback-defaults");
        }
    }

    public void SetCurveRuntimeEnabled(bool enabled)
    {
        lock (_curveSync)
        {
            _state.FanCurveStore = (_state.FanCurveStore ?? FanCurveDefaults.CreateDefaultStore()).WithEnabled(enabled);
            if (enabled)
            {
                _state.FanMinimumOverrideRpm = null;
            }
        }

        SaveFanCurveStore();
        SaveFanMinimumPreference();
        _runtimeState.ResetAnchors();
        _state.LastCurveWriteReason = enabled ? "Custom fan curves enabled." : "Custom fan curves disabled.";
        RefreshActiveCurveState();
        _state.RaiseStateChanged();
    }

    public void SetGpuLinked(bool linked)
    {
        lock (_curveSync)
        {
            FanCurveStore store = EnsureCurveStore();
            FanCurveSet current = store.GetForMode(_state.CurrentMode);
            if (linked)
            {
                current = new FanCurveSet(current.Cpu, FanCurveDefaults.BuildLinkedGpuProfile(current.Cpu), current.Chassis, true);
            }
            else
            {
                current = current.WithGpuLinked(false);
            }

            _state.FanCurveStore = store.WithMode(_state.CurrentMode, current);
        }

        SaveFanCurveStore();
        _runtimeState.ResetAnchors();
        RefreshActiveCurveState();
        _state.RaiseStateChanged();
    }

    public async Task SetFanCurveHysteresisAsync(int riseDeltaC, int dropDeltaC)
    {
        riseDeltaC = Math.Max(0, riseDeltaC);
        dropDeltaC = Math.Max(0, dropDeltaC);

        lock (_curveSync)
        {
            FanCurveStore store = EnsureCurveStore().WithHysteresis(riseDeltaC, dropDeltaC);
            _state.FanCurveStore = store;
        }

        SaveFanCurveStore();
        _runtimeState.ResetAnchors();
        RefreshActiveCurveState();

        if (_state.FanCurveRuntimeEnabled && !_state.MaxFanEnabled && !_state.FanMinimumOverrideRpm.HasValue)
        {
            await ApplyCurrentCurveImmediatelyAsync("fan hysteresis change").ConfigureAwait(false);
        }
        else if (!_state.FanCurveRuntimeEnabled)
        {
            _state.LastCurveWriteReason = "Fan hysteresis updated.";
        }

        _state.RaiseStateChanged();
    }

    public void SetCurveProfile(FanCurveKind kind, FanCurveProfile profile)
    {
        lock (_curveSync)
        {
            FanCurveStore store = EnsureCurveStore();
            FanCurveSet current = store.GetForMode(_state.CurrentMode);
            switch (kind)
            {
                case FanCurveKind.Cpu:
                    current = new FanCurveSet(profile, current.GpuLinked ? FanCurveDefaults.BuildLinkedGpuProfile(profile) : current.Gpu, current.Chassis, current.GpuLinked);
                    break;
                case FanCurveKind.Gpu:
                    if (!current.GpuLinked)
                    {
                        current = new FanCurveSet(current.Cpu, profile, current.Chassis, false);
                    }
                    break;
                case FanCurveKind.Chassis:
                    current = new FanCurveSet(current.Cpu, current.Gpu, profile, current.GpuLinked);
                    break;
            }

            _state.FanCurveStore = store.WithMode(_state.CurrentMode, current);
        }

        SaveFanCurveStore();
        _runtimeState.ResetAnchors();
        RefreshActiveCurveState();
        _state.RaiseStateChanged();
    }

    public void RefreshActiveCurveState()
    {
        FanCurveStore store = EnsureCurveStore();
        FanCurveSet set = store.GetForMode(_state.CurrentMode);
        _state.FanCurveRuntimeEnabled = store.Enabled;
        _state.ActiveFanCurveMode = PerformanceModeFirmwareMap.FormatDisplayName(_state.CurrentMode);
        _state.FanCurveHysteresisRiseDeltaC = store.HysteresisRiseDeltaC;
        _state.FanCurveHysteresisDropDeltaC = store.HysteresisDropDeltaC;
        _state.ActiveCpuCurve = set.Cpu;
        _state.ActiveGpuCurve = set.GpuLinked ? FanCurveDefaults.BuildLinkedGpuProfile(set.Cpu) : set.Gpu;
        _state.ActiveChassisCurve = set.Chassis;
        _state.GpuCurveLinked = set.GpuLinked;
    }

    public async Task OnPerformanceModeChangedAsync()
    {
        RefreshActiveCurveState();
        _runtimeState.ResetAnchors();
        await ApplyCurrentCurveImmediatelyAsync("performance mode change").ConfigureAwait(false);
    }

    public async Task ProcessTelemetryTickAsync(HardwareTelemetrySnapshot snapshot)
    {
        if (snapshot == null)
        {
            return;
        }

        _state.PooledTelemetryTimestampUtc = snapshot.TimestampUtc;
        AppendTemperature(_cpuTemperatureWindow, snapshot.CpuTemperatureC);
        AppendTemperature(_gpuTemperatureWindow, snapshot.GpuTemperatureC);
        AppendTemperature(_chassisTemperatureWindow, snapshot.ChassisTemperatureC);

        double avgCpu = GetAverage(_cpuTemperatureWindow, snapshot.CpuTemperatureC);
        double avgGpu = GetAverage(_gpuTemperatureWindow, snapshot.GpuTemperatureC);
        double avgChassis = GetAverage(_chassisTemperatureWindow, snapshot.ChassisTemperatureC);

        _state.AveragedCpuTemperatureC = avgCpu;
        _state.AveragedGpuTemperatureC = avgGpu;
        _state.AveragedChassisTemperatureC = avgChassis;

        FanCurveStore store = EnsureCurveStore();
        FanCurveSet set = store.GetForMode(_state.CurrentMode);
        FanCurveEvaluationResult evaluation = FanCurveEvaluator.Evaluate(
            set,
            avgCpu,
            avgGpu,
            avgChassis,
            _runtimeState,
            PerformanceModeFirmwareMap.FormatDisplayName(_state.CurrentMode));

        _state.CurveDesiredCpuRpm = evaluation.DesiredCpuRpm;
        _state.CurveDesiredGpuRpm = evaluation.DesiredGpuRpm;
        _state.CurveAppliedCpuRpm = evaluation.FinalCpuRpm;
        _state.CurveAppliedGpuRpm = evaluation.FinalGpuRpm;
        _state.CurveChassisOverrideUsed = evaluation.ChassisOverrideUsed;
        _state.CpuHysteresisAnchorTemperatureC = evaluation.CpuAnchorTemperatureC;
        _state.GpuHysteresisAnchorTemperatureC = evaluation.GpuAnchorTemperatureC;
        _state.ChassisHysteresisAnchorTemperatureC = evaluation.ChassisAnchorTemperatureC;
        _state.GpuCurveLinked = evaluation.GpuLinked;

        if (!store.Enabled)
        {
            _state.LastCurveWriteReason = "Curve runtime disabled.";
            return;
        }

        if (_state.MaxFanEnabled)
        {
            _state.LastCurveWriteReason = "Max fan enabled; curve runtime suspended.";
            return;
        }

        if (_state.FanMinimumOverrideRpm.HasValue)
        {
            _state.LastCurveWriteReason = "Debug fan minimum override active; curve runtime suspended.";
            return;
        }

        if (_runtimeState.LastWrittenCpuRpm == evaluation.FinalCpuRpm && _runtimeState.LastWrittenGpuRpm == evaluation.FinalGpuRpm)
        {
            _state.LastCurveWriteReason = "Curve target unchanged after hysteresis.";
            return;
        }

        await ApplyFanTargetsAsync(evaluation.FinalCpuRpm, evaluation.FinalGpuRpm, "curve tick").ConfigureAwait(false);
    }

    public async Task<bool> ApplyCurrentCurveImmediatelyAsync(string reason)
    {
        if (!_state.FanCurveRuntimeEnabled || _state.MaxFanEnabled || _state.FanMinimumOverrideRpm.HasValue)
        {
            return false;
        }

        double cpu = _state.AveragedCpuTemperatureC ?? _state.CpuTemperatureC ?? 50.0;
        double gpu = _state.AveragedGpuTemperatureC ?? _state.GpuTemperatureC ?? 50.0;
        double chassis = _state.AveragedChassisTemperatureC ?? _state.ChassisTemperatureC ?? 0.0;
        FanCurveEvaluationResult evaluation = FanCurveEvaluator.Evaluate(EnsureCurveStore().GetForMode(_state.CurrentMode), cpu, gpu, chassis, _runtimeState, PerformanceModeFirmwareMap.FormatDisplayName(_state.CurrentMode));
        _state.CurveDesiredCpuRpm = evaluation.DesiredCpuRpm;
        _state.CurveDesiredGpuRpm = evaluation.DesiredGpuRpm;
        _state.CurveAppliedCpuRpm = evaluation.FinalCpuRpm;
        _state.CurveAppliedGpuRpm = evaluation.FinalGpuRpm;
        _state.CurveChassisOverrideUsed = evaluation.ChassisOverrideUsed;
        _state.CpuHysteresisAnchorTemperatureC = evaluation.CpuAnchorTemperatureC;
        _state.GpuHysteresisAnchorTemperatureC = evaluation.GpuAnchorTemperatureC;
        _state.ChassisHysteresisAnchorTemperatureC = evaluation.ChassisAnchorTemperatureC;
        return await ApplyFanTargetsAsync(evaluation.FinalCpuRpm, evaluation.FinalGpuRpm, reason).ConfigureAwait(false);
    }

    public async Task<bool> ApplyFanMinimumBlobAsync(string source)
    {
        int rpm = _state.FanMinimumOverrideRpm ?? GetFanMinimumRpmForMode(_state.CurrentMode);
        return await ApplyFanTargetsAsync(rpm, rpm, source).ConfigureAwait(false);
    }

    public async Task<bool> ApplyCurrentFanTargetAsync(string source)
    {
        if (_state.MaxFanEnabled)
        {
            _state.LastCurveWriteReason = "Max fan enabled; current fan target write skipped.";
            return false;
        }

        int cpuRpm;
        int gpuRpm;
        string targetSource;

        if (_state.FanCurveRuntimeEnabled && _runtimeState.LastWrittenCpuRpm.HasValue && _runtimeState.LastWrittenGpuRpm.HasValue)
        {
            cpuRpm = _runtimeState.LastWrittenCpuRpm.Value;
            gpuRpm = _runtimeState.LastWrittenGpuRpm.Value;
            targetSource = "active fan curve";
        }
        else
        {
            int rpm = _state.FanMinimumOverrideRpm ?? GetFanMinimumRpmForMode(_state.CurrentMode);
            cpuRpm = rpm;
            gpuRpm = rpm;
            targetSource = _state.FanMinimumOverrideRpm.HasValue ? "fan minimum override" : "mode minimum";
        }

        return await ApplyFanTargetsAsync(cpuRpm, gpuRpm, source + " (" + targetSource + ")").ConfigureAwait(false);
    }

    public async Task<bool> MaintainFanTargetKeepaliveAsync()
    {
        if (_state.MaxFanEnabled || !_state.Initialized || !_state.Available)
        {
            return false;
        }

        DateTime now = DateTime.UtcNow;
        if (_state.LastCurveWriteTimestampUtc.HasValue && now - _state.LastCurveWriteTimestampUtc.Value < TimeSpan.FromSeconds(15))
        {
            return false;
        }

        await EnsureMaxFanOffAsync("fan target keepalive").ConfigureAwait(false);
        return await ApplyCurrentFanTargetAsync("fan target keepalive").ConfigureAwait(false);
    }

    public async Task<bool> EnsureMaxFanOffAsync(string source)
    {
        if (_state.MaxFanEnabled)
        {
            return false;
        }

        DateTime now = DateTime.UtcNow;
        if (_state.LastMaxFanExecuteResult.HasValue && _state.LastMaxFanReturnCode.HasValue && _state.LastMaxFanReturnCode.Value == 0)
        {
            // Reassert every heartbeat window when the firmware is otherwise healthy.
        }

        bool success = await TrySetMaxFanAsync(false, verifyReadback: false).ConfigureAwait(false);
        if (success)
        {
            _state.TrackEvent("Firmware", "Heartbeat reasserted MaxFan=Off source=" + source);
        }

        return success;
    }

    public async Task<bool> TrySetMaxFanAsync(bool enabled, bool verifyReadback = true)
    {
        try
        {
            bool requestedEnabled = enabled;
            bool ok = await _biosClient.SetMaxFanAsync(enabled).ConfigureAwait(false);
            _state.LastMaxFanExecuteResult = ok;
            _state.LastMaxFanReturnCode = ok ? 0 : -1;

            if (!ok)
            {
                return false;
            }

            if (!verifyReadback)
            {
                return true;
            }

            bool immediateReadback = await _biosClient.GetMaxFanEnabledAsync().ConfigureAwait(false);
            await Task.Delay(1200).ConfigureAwait(false);
            bool delayedReadback = await _biosClient.GetMaxFanEnabledAsync().ConfigureAwait(false);
            _state.TrackEvent("Firmware", "Set max fan requested=" + (requestedEnabled ? "On" : "Off") + ", readbackNow=" + (immediateReadback ? "On" : "Off") + ", readbackDelayed=" + (delayedReadback ? "On" : "Off"));
            if (delayedReadback != requestedEnabled)
            {
                _state.Log("Firmware MaxFan did not stick (requested=" + requestedEnabled + ", delayedReadback=" + delayedReadback + ").");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _state.Log("Firmware max fan change failed: " + ex.Message);
            return false;
        }
    }

    private FanCurveStore EnsureCurveStore()
    {
        lock (_curveSync)
        {
            if (_state.FanCurveStore == null)
            {
                _state.FanCurveStore = FanCurveDefaults.CreateDefaultStore();
            }

            return _state.FanCurveStore;
        }
    }

    private void ApplyFanCurveStore(FanCurveStore store, string source)
    {
        lock (_curveSync)
        {
            _state.FanCurveStore = store ?? FanCurveDefaults.CreateDefaultStore();
        }

        RefreshActiveCurveState();
        _state.TrackEvent("FanCurves", "Loaded fan curves source=" + source + ", enabled=" + _state.FanCurveRuntimeEnabled);
    }

    private void SaveFanCurveStore()
    {
        try
        {
            _stateStore.SaveFanCurveStore(EnsureCurveStore());
        }
        catch (Exception ex)
        {
            _state.Log("Fan curve store save failed: " + ex.Message);
        }
    }

    private async Task<bool> ApplyFanTargetsAsync(int cpuRpm, int gpuRpm, string source)
    {
        try
        {
            int normalizedCpu = FanCurveProfile.NormalizeRpm(cpuRpm);
            int normalizedGpu = FanCurveProfile.NormalizeRpm(gpuRpm);
            byte[] payload = BuildFanTargetBlob(normalizedCpu, normalizedGpu);
            _state.Log("Fan target blob write request (source=" + source + ", mode=" + FormatPerformanceMode(_state.CurrentMode) + ", cpuRpm=" + normalizedCpu + ", gpuRpm=" + normalizedGpu + ", payload=" + FormatInputData(payload) + ")");
            OmenBiosClient.BiosWmiResult result = await _biosClient.SetFanTargetsAsync(normalizedCpu, normalizedGpu).ConfigureAwait(false);

            byte[] previousBlob;
            string previousHash;
            lock (_state.DiagnosticsSync)
            {
                previousBlob = _state.LastFanTargetBlob ?? Array.Empty<byte>();
                previousHash = _state.LastFanTargetBlobHash;
                _state.PreviousFanTargetBlob = previousBlob;
                _state.PreviousFanTargetBlobHash = previousHash;
                _state.LastFanTargetBlob = payload;
                _state.LastFanTargetBlobHash = ComputeSha256Hex(payload);
                _state.LastFanTargetBlobPreview = FormatBytePreview(payload, 32);
                _state.LastFanTargetBlobChangedBytes = CountDifferentBytes(previousBlob, payload);
                _state.LastFanTargetBlobReturnCode = result.ReturnCode;
                _state.LastFanTargetBlobExecuteResult = result.ExecuteResult;
            }

            bool success = result.ExecuteResult && result.ReturnCode == 0;
            _state.LastCurveWriteTimestampUtc = DateTime.UtcNow;
            _state.LastCurveWriteReason = source + (success ? " succeeded." : " failed.");
            if (success)
            {
                _runtimeState.LastWrittenCpuRpm = normalizedCpu;
                _runtimeState.LastWrittenGpuRpm = normalizedGpu;
                _state.CurveAppliedCpuRpm = normalizedCpu;
                _state.CurveAppliedGpuRpm = normalizedGpu;
            }

            _state.TrackEvent(
                "Firmware",
                "Write fan target blob source=" + source +
                ", mode=" + FormatPerformanceMode(_state.CurrentMode) +
                ", cpu=" + normalizedCpu +
                ", gpu=" + normalizedGpu +
                ", rc=" + result.ReturnCode);
            _state.Log("Fan target blob write result (source=" + source + ", exec=" + result.ExecuteResult + ", rc=" + result.ReturnCode + ", out=" + FormatReturnData(result.ReturnData) + ")");

            return success;
        }
        catch (Exception ex)
        {
            _state.Log("Fan target blob write failed: " + ex.Message);
            _state.LastCurveWriteReason = source + " failed: " + ex.Message;
            return false;
        }
    }

    private async Task RefreshPerformanceStatusBlobAsync()
    {
        try
        {
            OmenBiosClient.BiosWmiResult result = await _biosClient.GetPerformanceStatusBlobAsync().ConfigureAwait(false);
            byte[] blob = result.ExecuteResult && result.ReturnCode == 0
                ? (result.ReturnData ?? Array.Empty<byte>())
                : Array.Empty<byte>();

            byte[] previousBlob;
            string previousHash;
            lock (_state.DiagnosticsSync)
            {
                previousBlob = _state.LastPerformanceStatusBlob ?? Array.Empty<byte>();
                previousHash = _state.LastPerformanceStatusBlobHash;
                _state.PreviousPerformanceStatusBlob = previousBlob;
                _state.PreviousPerformanceStatusBlobHash = previousHash;
                _state.LastPerformanceStatusBlob = blob;
                _state.LastPerformanceStatusBlobHash = ComputeSha256Hex(blob);
                _state.LastPerformanceStatusBlobPreview = FormatBytePreview(blob, 32);
                _state.LastPerformanceStatusBlobChangedBytes = CountDifferentBytes(previousBlob, blob);
                _state.LastPerformanceStatusBlobReturnCode = result.ReturnCode;
                _state.LastPerformanceStatusBlobExecuteResult = result.ExecuteResult;
            }
        }
        catch (Exception ex)
        {
            _state.Log("Performance status blob read failed: " + ex.Message);
        }
    }

    private static byte[] BuildFanTargetBlob(int cpuRpm, int gpuRpm)
    {
        byte[] payload = new byte[128];
        payload[0] = (byte)Math.Max(0, Math.Min(65, FanCurveProfile.NormalizeRpm(cpuRpm) / 100));
        payload[1] = (byte)Math.Max(0, Math.Min(65, FanCurveProfile.NormalizeRpm(gpuRpm) / 100));
        return payload;
    }

    private static void AppendTemperature(Queue<double> window, double? value)
    {
        if (!value.HasValue)
        {
            return;
        }

        window.Enqueue(value.Value);
        while (window.Count > 5)
        {
            window.Dequeue();
        }
    }

    private static double GetAverage(Queue<double> window, double? fallback)
    {
        if (window.Count > 0)
        {
            return window.Average();
        }

        return fallback ?? 0.0;
    }

    private static bool IsAllowedFanMinimumRpm(int rpm)
    {
        switch (rpm)
        {
            case 0:
            case 1300:
            case 1500:
            case 2500:
            case 3500:
            case 4500:
            case 5500:
            case 6500:
                return true;
            default:
                return false;
        }
    }

    private static int GetFanMinimumRpmForMode(PerformanceMode mode)
    {
        return PerformanceModeFirmwareMap.GetFanMinimumRpm(mode);
    }

    private static string FormatPerformanceMode(PerformanceMode mode)
    {
        return PerformanceModeFirmwareMap.FormatDisplayName(mode);
    }

    private static string FormatFanMinimumPreference(int? rpm)
    {
        return rpm.HasValue ? rpm.Value.ToString() : "None";
    }

    private static string FormatInputData(byte[] data)
    {
        if (data == null || data.Length == 0)
        {
            return "<empty>";
        }

        int take = Math.Min(16, data.Length);
        string prefix = BitConverter.ToString(data, 0, take);
        return data.Length <= take ? prefix : prefix + "...(len=" + data.Length + ")";
    }

    private static string FormatReturnData(byte[] data)
    {
        if (data == null || data.Length == 0)
        {
            return "<empty>";
        }

        int take = Math.Min(8, data.Length);
        string prefix = BitConverter.ToString(data, 0, take);
        return data.Length <= take ? prefix : (prefix + "...(len=" + data.Length + ")");
    }

    private static string ComputeSha256Hex(byte[] data)
    {
        if (data == null || data.Length == 0)
        {
            return "<empty>";
        }

        using (System.Security.Cryptography.SHA256 sha256 = System.Security.Cryptography.SHA256.Create())
        {
            byte[] hash = sha256.ComputeHash(data);
            return BitConverter.ToString(hash).Replace("-", string.Empty);
        }
    }

    private static string FormatBytePreview(byte[] data, int take)
    {
        if (data == null || data.Length == 0)
        {
            return "<empty>";
        }

        int count = Math.Min(take, data.Length);
        string prefix = BitConverter.ToString(data, 0, count);
        return data.Length <= count ? prefix : prefix + "...(len=" + data.Length + ")";
    }

    private static int? CountDifferentBytes(byte[] left, byte[] right)
    {
        if (left == null || right == null || left.Length != right.Length)
        {
            return null;
        }

        int diff = 0;
        for (int i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
            {
                diff++;
            }
        }

        return diff;
    }
}
