using System;
using System.Threading.Tasks;
using Hp.Bridge.Client.SDKs.PerformanceControl.Enums;
using HP.Omen.Core.Common.PowerControl.Enum;
using HP.Omen.Core.Model.DataStructure.Modules.FanControl.Enums;
using OmenHelper.Application.State;
using OmenHelper.Domain.Firmware;
using OmenHelper.Infrastructure.Bios;
using OmenHelper.Infrastructure.Persistence;

namespace OmenHelper.Application.Services;

internal sealed class PerformanceModeService
{
    private readonly SharedSessionState _state;
    private readonly OmenBiosClient _biosClient;
    private readonly LocalStateStore _stateStore;
    private readonly FanControlService _fanControl;

    public PerformanceModeService(
        SharedSessionState state,
        OmenBiosClient biosClient,
        LocalStateStore stateStore,
        FanControlService fanControl)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _biosClient = biosClient ?? throw new ArgumentNullException(nameof(biosClient));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _fanControl = fanControl ?? throw new ArgumentNullException(nameof(fanControl));
    }

    public async Task SetPerformanceModeAsync(PerformanceMode mode)
    {
        ThermalControl thermalControlToRestore = _state.CurrentThermalMode;

        _state.LastPerformanceRequestMode = FormatPerformanceMode(mode);
        _state.LastPerformanceRequestPath = string.Empty;
        _state.LastPerfType26ReturnCode = null;
        _state.LastPerfType26ExecuteResult = null;
        _state.LastPerfType34ReturnCode = null;
        _state.LastPerfType34ExecuteResult = null;
        _state.LastPerfType41ReturnCode = null;
        _state.LastPerfType41ExecuteResult = null;

        if (await TrySetPerformanceModeFirmwareAsync(mode).ConfigureAwait(false))
        {
            _state.LastPerformanceRequestPath = "FirmwareThruDriver";
            _state.CurrentMode = mode;
            _state.CurrentModeIsInferred = true;
            SaveRememberedPerformanceMode(mode);

            bool thermalRestoreOk = await TrySetThermalModeStateAsync(thermalControlToRestore).ConfigureAwait(false);
            if (!thermalRestoreOk && thermalControlToRestore != ThermalControl.Manual)
            {
                _state.Log("Thermal mode restore after performance mode change failed.");
            }

            bool fanTargetOk = await _fanControl.ApplyFanMinimumBlobAsync("performance mode change").ConfigureAwait(false);
            if (!fanTargetOk)
            {
                _state.Log("Fan target blob restore after performance mode change failed.");
            }

            await Task.Delay(250).ConfigureAwait(false);
            await RefreshPerformanceStatusBlobAsync().ConfigureAwait(false);
            _state.RaiseStateChanged();
            return;
        }

        _state.LastPerformanceRequestPath = "Failed";
        _state.Log("Performance mode change failed (firmware only).");
    }

    public async Task SetThermalModeAsync(ThermalControl thermalControl)
    {
        bool success = await TrySetThermalModeStateAsync(thermalControl).ConfigureAwait(false);
        if (!success)
        {
            if (thermalControl != ThermalControl.Manual)
            {
                _state.Log("Thermal mode change failed (firmware only).");
            }

            return;
        }

        bool fanTargetOk = await _fanControl.ApplyFanMinimumBlobAsync("thermal mode change").ConfigureAwait(false);
        if (!fanTargetOk)
        {
            _state.Log("Fan target blob restore after thermal mode change failed.");
        }

        await RefreshPerformanceStatusBlobAsync().ConfigureAwait(false);
        _state.RaiseStateChanged();
    }

    public PerformanceMode? GetBatteryPowerModePreference()
    {
        return _state.BatteryPowerMode;
    }

    public PerformanceMode? GetPluggedInPowerModePreference()
    {
        return _state.PluggedInPowerMode;
    }

    public void SetBatteryPowerModePreference(PerformanceMode? mode)
    {
        _state.BatteryPowerMode = NormalizePreferenceMode(mode);
        SavePowerModePreferences();
    }

    public void SetPluggedInPowerModePreference(PerformanceMode? mode)
    {
        _state.PluggedInPowerMode = NormalizePreferenceMode(mode);
        SavePowerModePreferences();
    }

    public async Task SyncPowerSourcePerformanceModeAsync(bool pluggedIn)
    {
        bool sourceChanged = !_state.LastKnownPluggedIn.HasValue || _state.LastKnownPluggedIn.Value != pluggedIn;
        _state.LastKnownPluggedIn = pluggedIn;

        // Only react when the detected power source actually changes.
        // Preference edits should update the stored target, not immediately
        // force a mode switch on the current source.
        if (!sourceChanged)
        {
            return;
        }

        PerformanceMode? targetMode = pluggedIn ? _state.PluggedInPowerMode : _state.BatteryPowerMode;
        if (!targetMode.HasValue)
        {
            _state.Log("Power source is " + (pluggedIn ? "AC" : "battery") + "; no automatic performance mode switch configured.");
            return;
        }

        if ((_state.CurrentModeKnown || _state.CurrentModeIsInferred) && _state.CurrentMode == targetMode.Value)
        {
            _state.Log("Power source is " + (pluggedIn ? "AC" : "battery") + "; performance mode already set to " + FormatPerformanceMode(targetMode.Value) + ".");
            return;
        }

        _state.Log("Power source changed to " + (pluggedIn ? "AC" : "battery") + "; switching to " + FormatPerformanceMode(targetMode.Value) + ".");
        await SetPerformanceModeAsync(targetMode.Value).ConfigureAwait(false);
    }

    public void LoadRememberedPerformanceMode()
    {
        try
        {
            PerformanceMode mode;
            if (!_stateStore.TryLoadRememberedPerformanceMode(out mode))
            {
                return;
            }

            _state.CurrentMode = mode;
            _state.CurrentModeIsInferred = true;
            _state.Log("Loaded remembered performance mode: " + PerformanceModeFirmwareMap.FormatDisplayName(mode));
        }
        catch (Exception ex)
        {
            _state.Log("Performance mode memory load failed: " + ex.Message);
        }
    }

    public void SaveRememberedPerformanceMode(PerformanceMode mode)
    {
        try
        {
            _stateStore.SaveRememberedPerformanceMode(mode);
        }
        catch (Exception ex)
        {
            _state.Log("Performance mode memory save failed: " + ex.Message);
        }
    }

    public void LoadPowerModePreferences()
    {
        try
        {
            PerformanceMode? batteryMode;
            PerformanceMode? pluggedInMode;
            if (!_stateStore.TryLoadPowerModePreferences(out batteryMode, out pluggedInMode))
            {
                return;
            }

            _state.BatteryPowerMode = batteryMode;
            _state.PluggedInPowerMode = pluggedInMode;
            _state.Log("Loaded power mode preferences: battery=" + FormatPowerModePreference(_state.BatteryPowerMode) + ", pluggedIn=" + FormatPowerModePreference(_state.PluggedInPowerMode));
        }
        catch (Exception ex)
        {
            _state.Log("Power mode preference load failed: " + ex.Message);
        }
    }

    public void SavePowerModePreferences()
    {
        try
        {
            _stateStore.SavePowerModePreferences(_state.BatteryPowerMode, _state.PluggedInPowerMode);
        }
        catch (Exception ex)
        {
            _state.Log("Power mode preference save failed: " + ex.Message);
        }
    }

    private async Task<bool> TrySetPerformanceModeFirmwareAsync(PerformanceMode mode)
    {
        try
        {
            byte type26Value = MapPerformanceModeToType26Value(mode);
            if (type26Value == 0)
            {
                _state.Log("Firmware mode mapping is unknown for " + mode + "; no BIOS-only write path available.");
                return false;
            }

            byte[][] type26Candidates =
            {
                new byte[4] { 255, type26Value, 1, 0 },
                new byte[4] { 255, type26Value, 0, 0 }
            };

            bool type26Succeeded = false;
            foreach (byte[] payload in type26Candidates)
            {
                if (await TryFirmwareSetAsync(
                    command: BiosCommandCatalog.PerformancePlatformCommand,
                    commandType: BiosCommandCatalog.PerformanceModeType,
                    payload: payload,
                    returnDataSize: 4,
                    logPrefix: "Firmware SetMode",
                    onResult: r =>
                    {
                        _state.LastPerfType26ExecuteResult = r.ExecuteResult;
                        _state.LastPerfType26ReturnCode = r.ReturnCode;
                    }).ConfigureAwait(false))
                {
                    type26Succeeded = true;
                    break;
                }
            }

            if (!type26Succeeded)
            {
                return false;
            }

            byte[] type34 = MapPerformanceModeToType34Payload(mode);
            bool type34Ok = await TryFirmwareSetAsync(
                command: BiosCommandCatalog.PerformancePlatformCommand,
                commandType: BiosCommandCatalog.PerformanceGpuPowerType,
                payload: type34,
                returnDataSize: 4,
                logPrefix: "Firmware GPU status",
                onResult: r =>
                {
                    _state.LastPerfType34ExecuteResult = r.ExecuteResult;
                    _state.LastPerfType34ReturnCode = r.ReturnCode;
                }).ConfigureAwait(false);

            if (!type34Ok)
            {
                return false;
            }

            if (mode == PerformanceMode.Performance || PerformanceModeFirmwareMap.IsUnleashedMode(mode))
            {
                byte[] type41Payload = new byte[4]
                {
                    255,
                    255,
                    255,
                    BiosCommandCatalog.PerformanceTpptdpPayload
                };

                bool type41Ok = await TryFirmwareSetAsync(
                    command: BiosCommandCatalog.PerformancePlatformCommand,
                    commandType: BiosCommandCatalog.PerformanceTpptdpType,
                    payload: type41Payload,
                    returnDataSize: 4,
                    logPrefix: "Firmware TPP/TDP",
                    onResult: r =>
                    {
                        _state.LastPerfType41ExecuteResult = r.ExecuteResult;
                        _state.LastPerfType41ReturnCode = r.ReturnCode;
                    }).ConfigureAwait(false);

                if (!type41Ok)
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _state.Log("Firmware performance mode change failed: " + ex.Message);
            return false;
        }
    }

    private async Task<bool> TrySetThermalModeStateAsync(ThermalControl thermalControl)
    {
        if (thermalControl == ThermalControl.Manual)
        {
            _state.Log("ThermalControl.Manual is not supported.");
            return false;
        }

        bool enableMaxFan = thermalControl == ThermalControl.Max;
        bool success = await _fanControl.TrySetMaxFanAsync(enableMaxFan).ConfigureAwait(false);
        if (!success)
        {
            return false;
        }

        _state.CurrentThermalMode = thermalControl;
        _state.CurrentLegacyFanMode = enableMaxFan ? FanMode.Turbo : FanMode.Normal;
        return true;
    }

    private async Task<bool> TryFirmwareSetAsync(int command, int commandType, byte[] payload, int returnDataSize, string logPrefix, Action<OmenBiosClient.BiosWmiResult> onResult)
    {
        OmenBiosClient.BiosWmiResult result = await _biosClient.ExecuteAsync(
            command: command,
            commandType: commandType,
            inputData: payload,
            returnDataSize: returnDataSize).ConfigureAwait(false);

        onResult?.Invoke(result);
        _state.Log(logPrefix + ": cmd=" + command + " type=" + commandType + " input=" + FormatInputData(payload) + " exec=" + result.ExecuteResult + " rc=" + result.ReturnCode + " out=" + FormatReturnData(result.ReturnData));

        if (result.ExecuteResult && result.ReturnCode == 0)
        {
            return true;
        }

        _state.Log(logPrefix + " failed: cmd=" + command + " type=" + commandType + " biosError=" + _biosClient.LastError);
        return false;
    }

    public async Task RefreshPerformanceStatusBlobAsync()
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

            _state.TrackEvent(
                "Firmware",
                "Read performance status blob hash=" + _state.LastPerformanceStatusBlobHash +
                ", changedBytes=" + (_state.LastPerformanceStatusBlobChangedBytes.HasValue ? _state.LastPerformanceStatusBlobChangedBytes.Value.ToString() : "<n/a>") +
                ", rc=" + result.ReturnCode);
        }
        catch (Exception ex)
        {
            _state.Log("Performance status blob read failed: " + ex.Message);
        }
    }

    private static PerformanceMode? NormalizePreferenceMode(PerformanceMode? mode)
    {
        if (!mode.HasValue)
        {
            return null;
        }

        if (mode.Value == PerformanceMode.Eco || mode.Value == PerformanceMode.Default || mode.Value == PerformanceMode.Performance || PerformanceModeFirmwareMap.IsUnleashedMode(mode.Value))
        {
            return mode;
        }

        return PerformanceMode.Default;
    }

    private static string FormatPerformanceMode(PerformanceMode mode)
    {
        return PerformanceModeFirmwareMap.FormatDisplayName(mode);
    }

    private static string FormatPowerModePreference(PerformanceMode? mode)
    {
        return mode.HasValue ? PerformanceModeFirmwareMap.FormatDisplayName(mode.Value) : "None";
    }

    private static byte MapPerformanceModeToType26Value(PerformanceMode mode)
    {
        return PerformanceModeFirmwareMap.GetType26Value(mode);
    }

    private static byte[] MapPerformanceModeToType34Payload(PerformanceMode mode)
    {
        return PerformanceModeFirmwareMap.GetType34Payload(mode);
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
