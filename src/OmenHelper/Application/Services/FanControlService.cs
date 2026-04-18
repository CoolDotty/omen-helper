using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Hp.Bridge.Client.SDKs.PerformanceControl.Enums;
using HP.Omen.Core.Common.PowerControl.Enum;
using HP.Omen.Core.Model.DataStructure.Modules.FanControl.Enums;
using OmenHelper.Application.State;
using OmenHelper.Domain.Firmware;
using OmenHelper.Infrastructure.Bios;
using OmenHelper.Infrastructure.Persistence;

namespace OmenHelper.Application.Services;

internal sealed class FanControlService
{
    private readonly SharedSessionState _state;
    private readonly OmenBiosClient _biosClient;
    private readonly LocalStateStore _stateStore;

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
        return GetConfiguredFanMinimumRpm();
    }

    public IReadOnlyList<int> GetFanMinimumOptions()
    {
        return new[] { 0, 1500, 2500, 3500, 4500, 5500, 6500 };
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

        if (!await ApplyFanMinimumBlobAsync("fan minimum selection").ConfigureAwait(false))
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

    public async Task<bool> ApplyFanMinimumBlobAsync(string source)
    {
        try
        {
            byte[] payload = BuildFanMinimumBlobForCurrentMode();
            _state.Log("Fan minimum blob write request (source=" + source + ", mode=" + FormatPerformanceMode(_state.CurrentMode) + ", minRpm=" + GetConfiguredFanMinimumRpm() + ", payload=" + FormatInputData(payload) + ")");
            OmenBiosClient.BiosWmiResult result = await _biosClient.SetPerformanceStatusBlobAsync(payload).ConfigureAwait(false);

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

            _state.TrackEvent(
                "Firmware",
                "Write fan minimum blob source=" + source +
                ", mode=" + FormatPerformanceMode(_state.CurrentMode) +
                ", minRpm=" + GetConfiguredFanMinimumRpm() +
                ", rc=" + result.ReturnCode);
            _state.Log("Fan minimum blob write result (source=" + source + ", exec=" + result.ExecuteResult + ", rc=" + result.ReturnCode + ", out=" + FormatReturnData(result.ReturnData) + ")");

            return result.ExecuteResult && result.ReturnCode == 0;
        }
        catch (Exception ex)
        {
            _state.Log("Fan target blob write failed: " + ex.Message);
            return false;
        }
    }

    public async Task<bool> TrySetMaxFanAsync(bool enabled)
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

    private byte[] BuildFanMinimumBlobForCurrentMode()
    {
        int minimumRpm = RoundToStep100(GetConfiguredFanMinimumRpm());
        byte minimumValue = (byte)Math.Max(0, Math.Min(255, minimumRpm / 100));
        byte[] payload = new byte[128];
        payload[0] = minimumValue;
        payload[1] = minimumValue;
        return payload;
    }

    private int GetConfiguredFanMinimumRpm()
    {
        return _state.FanMinimumOverrideRpm.HasValue ? _state.FanMinimumOverrideRpm.Value : GetFanMinimumRpmForMode(_state.CurrentMode);
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

    private static bool IsAllowedFanMinimumRpm(int rpm)
    {
        switch (rpm)
        {
            case 0:
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

    private static int RoundToStep100(int value)
    {
        return (int)(Math.Round(value / 100.0) * 100.0);
    }

    private static int GetFanMinimumRpmForMode(Hp.Bridge.Client.SDKs.PerformanceControl.Enums.PerformanceMode mode)
    {
        return PerformanceModeFirmwareMap.GetFanMinimumRpm(mode);
    }

    private static string FormatPerformanceMode(Hp.Bridge.Client.SDKs.PerformanceControl.Enums.PerformanceMode mode)
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
