using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using Hp.Bridge.Client.SDKs.PerformanceControl.Enums;
using HP.Omen.Core.Common.Enums;
using HP.Omen.Core.Common.PowerControl;
using HP.Omen.Core.Common.PowerControl.Enum;
using HP.Omen.Core.Common.Utilities;
using HP.Omen.Core.Common.Utilities.SystemAdvPerformanceHelper.CommonUtilities;
using HP.Omen.Core.Model.DataStructure.Modules.FanControl.Enums;
using HP.Omen.Core.Model.DataStructure.Modules.GraphicsSwitcher.Enums;
using OmenHelper.Models;

namespace OmenHelper.Services;

internal sealed class OmenPerformanceController : IDisposable
{
    private readonly int _sessionId;
    private readonly OmenBiosClient _omenBiosClient = new OmenBiosClient();
    private readonly object _diagnosticsSync = new object();

    private bool _started;
    private bool _disposed;
    private bool _initialized = true;
    private bool _available = true;
    private PerformanceMode _currentMode = PerformanceMode.Default;
    private ThermalControl _currentThermalMode = ThermalControl.Auto;
    private FanMode _currentLegacyFanMode = FanMode.Normal;
    private GraphicsSwitcherMode _currentGraphicsMode = GraphicsSwitcherMode.Unknown;
    private bool _extremeUnlocked = true;
    private bool _unleashVisible = true;
    private ThermalModeOnUI _thermalModeUiType = (ThermalModeOnUI)0;
    private bool _graphicsSupportsUma;
    private bool _graphicsSupportsHybrid;
    private bool _graphicsModeSwitchSupported;
    private bool _graphicsNeedsReboot = true;
    private byte _graphicsModeSwitchBits;
    private bool _graphicsModeSwitchReadSucceeded;
    private int _helperGraphicsSupportedModes = -1;
    private bool _helperGraphicsSupportsUma;
    private bool _helperGraphicsSupportsHybrid;
    private bool _helperGraphicsSupportAvailable;
    private string _lastGraphicsRequestMode = string.Empty;
    private int? _lastGraphicsRequestReturnCode;
    private List<string> _supportModes = new List<string>();
    private string _lastPerformanceRequestMode = string.Empty;
    private string _lastPerformanceRequestPath = string.Empty;
    private int? _lastPerfType26ReturnCode;
    private bool? _lastPerfType26ExecuteResult;
    private int? _lastPerfType34ReturnCode;
    private bool? _lastPerfType34ExecuteResult;
    private int? _lastPerfType41ReturnCode;
    private bool? _lastPerfType41ExecuteResult;
    private int? _lastMaxFanReturnCode;
    private bool? _lastMaxFanExecuteResult;
    private readonly Queue<string> _recentEvents = new Queue<string>();

    public event EventHandler<PerformanceControlState> StateChanged;

    public event EventHandler<string> LogMessage;

    public OmenPerformanceController()
    {
        _sessionId = Process.GetCurrentProcess().SessionId;
    }

    public void Start()
    {
        if (_started)
        {
            return;
        }

        try
        {
            _omenBiosClient.Initialize();
            Log("BIOS WMI client initialized.");
        }
        catch (Exception ex)
        {
            _available = false;
            Log("BIOS WMI init failed: " + ex.Message);
        }

        _supportModes = new List<string>
        {
            nameof(PerformanceMode.Default),
            nameof(PerformanceMode.Performance),
            nameof(PerformanceMode.Eco),
            "Unleashed"
        };

        _started = true;

        _ = RequestInitializationAsync();
        RefreshGraphicsMode();
        RefreshGraphicsSupport();
        RaiseStateChanged();

        Log("Started control path: BIOS/WMI only. HP pipe support removed.");
        Log("Process: " + (Environment.Is64BitProcess ? "x64" : "x86") + ", OS: " + (Environment.Is64BitOperatingSystem ? "x64" : "x86"));
        Log("Elevation: " + (IsAdministrator() ? "admin" : "not-admin"));

        try
        {
            bool driverInstalled = CommonUtility.IsDriverInstalled();
            bool driverMeetsMin = CommonUtility.IsDriverMeetMinReq();
            Log("HpReadHWData: installed=" + driverInstalled + " meetsMin=" + driverMeetsMin);
        }
        catch (Exception ex)
        {
            Log("HpReadHWData check failed: " + ex.Message);
        }
    }

    private static bool IsAdministrator()
    {
        try
        {
            WindowsIdentity identity = WindowsIdentity.GetCurrent();
            WindowsPrincipal principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public async Task RequestInitializationAsync()
    {
        try
        {
            bool maxFanEnabled = await _omenBiosClient.GetMaxFanEnabledAsync().ConfigureAwait(false);
            _currentLegacyFanMode = maxFanEnabled ? FanMode.Turbo : FanMode.Normal;
            _currentThermalMode = maxFanEnabled ? ThermalControl.Max : ThermalControl.Auto;
            _initialized = true;
            _available = true;
            RefreshGraphicsMode();
            TrackEvent("Firmware", "Read max fan=" + (maxFanEnabled ? "On" : "Off"));
            RaiseStateChanged();
        }
        catch (Exception ex)
        {
            _initialized = false;
            _available = false;
            Log("Firmware state refresh failed: " + ex.Message);
            RaiseStateChanged();
        }
    }

    public async Task<string> BuildDiagnosticsReportAsync()
    {
        StringBuilder builder = new StringBuilder();

        builder.AppendLine("Session");
        builder.AppendLine("  SessionId: " + _sessionId);
        builder.AppendLine("  Initialized: " + _initialized);
        builder.AppendLine("  Available: " + _available);
        builder.AppendLine();

        builder.AppendLine("Current State");
        builder.AppendLine("  Mode: " + _currentMode);
        builder.AppendLine("  Thermal: " + _currentThermalMode);
        builder.AppendLine("  Legacy Fan: " + _currentLegacyFanMode);
        builder.AppendLine("  Graphics: " + _currentGraphicsMode);
        if (!string.IsNullOrWhiteSpace(_lastPerformanceRequestMode))
        {
            builder.AppendLine("  Last Perf Request: " + _lastPerformanceRequestMode + " (" + _lastPerformanceRequestPath + ")");
            builder.AppendLine("    BIOS 131080/26 exec: " + (_lastPerfType26ExecuteResult.HasValue ? _lastPerfType26ExecuteResult.Value.ToString() : "<none>") + " rc: " + (_lastPerfType26ReturnCode.HasValue ? _lastPerfType26ReturnCode.Value.ToString() : "<none>"));
            builder.AppendLine("    BIOS 131080/34 exec: " + (_lastPerfType34ExecuteResult.HasValue ? _lastPerfType34ExecuteResult.Value.ToString() : "<none>") + " rc: " + (_lastPerfType34ReturnCode.HasValue ? _lastPerfType34ReturnCode.Value.ToString() : "<none>"));
            builder.AppendLine("    BIOS 131080/41 exec: " + (_lastPerfType41ExecuteResult.HasValue ? _lastPerfType41ExecuteResult.Value.ToString() : "<none>") + " rc: " + (_lastPerfType41ReturnCode.HasValue ? _lastPerfType41ReturnCode.Value.ToString() : "<none>"));
        }
        builder.AppendLine("  Last MaxFan exec: " + (_lastMaxFanExecuteResult.HasValue ? _lastMaxFanExecuteResult.Value.ToString() : "<none>") + " rc: " + (_lastMaxFanReturnCode.HasValue ? _lastMaxFanReturnCode.Value.ToString() : "<none>"));
        builder.AppendLine("  Thermal UI Type: " + _thermalModeUiType);
        builder.AppendLine("  Extreme Unlocked: " + _extremeUnlocked);
        builder.AppendLine("  Unleash Visible: " + _unleashVisible);
        builder.AppendLine("  Support Modes: " + string.Join(", ", _supportModes));
        builder.AppendLine();

        try
        {
            byte[] systemDesignData = await _omenBiosClient.GetSystemDesignDataAsync().ConfigureAwait(false);
            SystemDesignDataInfo systemDesignDataInfo = await _omenBiosClient.GetSystemDesignDataInfoAsync().ConfigureAwait(false);
            _graphicsModeSwitchBits = systemDesignDataInfo.RawGpuModeSwitch;
            _graphicsModeSwitchReadSucceeded = systemDesignDataInfo.ReadSucceeded;
            RefreshGraphicsSupportFromProbe(systemDesignDataInfo);
            builder.AppendLine("BIOS / Platform");
            builder.AppendLine("  SystemDesignData: " + ((systemDesignData != null && systemDesignData.Length > 0) ? BitConverter.ToString(systemDesignData) : "<empty>"));
            builder.AppendLine("  ShippingAdapterPowerRating: " + PowerControlHelper.ShippingAdapterPowerRating);
            builder.AppendLine("  IsBiosPerformanceModeSupport: " + PowerControlHelper.IsBiosPerformanceModeSupport);
            builder.AppendLine("  IsSwFanControlSupport: " + PowerControlHelper.IsSwFanControlSupport);
            builder.AppendLine("  IsExtremeModeSupport: " + PowerControlHelper.IsExtremeModeSupport);
            builder.AppendLine("  IsExtremeModeUnlock: " + PowerControlHelper.IsExtremeModeUnlock);
            builder.AppendLine("  GraphicsModeSwitchBits: 0x" + systemDesignDataInfo.RawGpuModeSwitch.ToString("X2"));
            builder.AppendLine("  GraphicsModeSwitchReadSucceeded: " + systemDesignDataInfo.ReadSucceeded);
            builder.AppendLine("  GraphicsModeSwitchSupported: " + systemDesignDataInfo.SupportsGraphicsSwitching);
            builder.AppendLine("  GraphicsModeSwitchRawSlots: " + systemDesignDataInfo.RawGraphicsModeSlots);
            builder.AppendLine("  GraphicsModeSwitchSlots: " + systemDesignDataInfo.GraphicsModeSlots);
            builder.AppendLine("  GraphicsModeSwitchHasIntegratedSlot: " + systemDesignDataInfo.HasIntegratedSlot);
            builder.AppendLine("  GraphicsModeSwitchHasHybridSlot: " + systemDesignDataInfo.HasHybridSlot);
            builder.AppendLine("  GraphicsModeSwitchHasDedicatedSlot: " + systemDesignDataInfo.HasDedicatedSlot);
            builder.AppendLine("  GraphicsModeSwitchHasOptimusSlot: " + systemDesignDataInfo.HasOptimusSlot);
            builder.AppendLine("  HelperGraphicsSupportedModes: " + (_helperGraphicsSupportedModes >= 0 ? _helperGraphicsSupportedModes.ToString() : "<unavailable>"));
            builder.AppendLine("  HelperGraphicsSupportAvailable: " + _helperGraphicsSupportAvailable);
            builder.AppendLine("  HelperGraphicsSupportsHybrid: " + _helperGraphicsSupportsHybrid);
            builder.AppendLine("  HelperGraphicsSupportsUma: " + _helperGraphicsSupportsUma);
            RefreshGraphicsMode();
            builder.AppendLine("  GraphicsMode: " + _currentGraphicsMode);
            builder.AppendLine("  GraphicsModeSwitchSupported(State): " + _graphicsModeSwitchSupported);
            builder.AppendLine("  GraphicsSupportsHybrid: " + _graphicsSupportsHybrid);
            builder.AppendLine("  GraphicsSupportsUma: " + _graphicsSupportsUma);
            builder.AppendLine("  GraphicsNeedsReboot: " + _graphicsNeedsReboot);
            builder.AppendLine("  LastGraphicsRequestMode: " + (string.IsNullOrWhiteSpace(_lastGraphicsRequestMode) ? "<none>" : _lastGraphicsRequestMode));
            builder.AppendLine("  LastGraphicsRequestReturnCode: " + (_lastGraphicsRequestReturnCode.HasValue ? _lastGraphicsRequestReturnCode.Value.ToString() : "<none>"));
            try
            {
                builder.AppendLine("  MaxFan(BIOS): " + ((await _omenBiosClient.GetMaxFanEnabledAsync().ConfigureAwait(false)) ? "On" : "Off"));
            }
            catch (Exception ex)
            {
                builder.AppendLine("  MaxFan(BIOS): error: " + ex.Message);
            }
            builder.AppendLine();
        }
        catch (Exception ex)
        {
            builder.AppendLine("BIOS / Platform");
            builder.AppendLine("  error: " + ex.Message);
            builder.AppendLine();
        }

        lock (_diagnosticsSync)
        {
            builder.AppendLine("Recent Events");
            if (_recentEvents.Count == 0)
            {
                builder.AppendLine("<none>");
            }
            else
            {
                foreach (string entry in _recentEvents)
                {
                    builder.AppendLine(entry);
                }
            }
        }

        return builder.ToString();
    }

    public async Task SetPerformanceModeAsync(PerformanceMode mode)
    {
        _lastPerformanceRequestMode = mode.ToString();
        _lastPerformanceRequestPath = string.Empty;
        _lastPerfType26ReturnCode = null;
        _lastPerfType26ExecuteResult = null;
        _lastPerfType34ReturnCode = null;
        _lastPerfType34ExecuteResult = null;
        _lastPerfType41ReturnCode = null;
        _lastPerfType41ExecuteResult = null;

        if (await TrySetPerformanceModeFirmwareAsync(mode).ConfigureAwait(false))
        {
            _lastPerformanceRequestPath = "FirmwareThruDriver";
            _currentMode = mode;
            await RefreshThermalFromMaxFanAsync().ConfigureAwait(false);
            RaiseStateChanged();
            return;
        }

        _lastPerformanceRequestPath = "Failed";
        Log("Performance mode change failed (firmware only).");
    }

    public async Task SetThermalModeAsync(ThermalControl thermalControl)
    {
        if (thermalControl == ThermalControl.Manual)
        {
            Log("ThermalControl.Manual is not implemented (would require custom fan curve control).");
            return;
        }

        bool enableMaxFan = thermalControl == ThermalControl.Max;
        bool success = await TrySetMaxFanAsync(enableMaxFan).ConfigureAwait(false);
        if (!success)
        {
            Log("Thermal mode change failed (firmware only).");
            return;
        }

        _currentThermalMode = thermalControl;
        _currentLegacyFanMode = enableMaxFan ? FanMode.Turbo : FanMode.Normal;
        RaiseStateChanged();
    }

    public async Task SetLegacyFanModeAsync(FanMode mode)
    {
        bool enableMaxFan = mode == FanMode.Turbo;
        bool success = await TrySetMaxFanAsync(enableMaxFan).ConfigureAwait(false);
        if (!success)
        {
            Log("Legacy fan mode change failed (firmware only).");
            return;
        }

        _currentLegacyFanMode = mode;
        _currentThermalMode = enableMaxFan ? ThermalControl.Max : _currentThermalMode == ThermalControl.Max ? ThermalControl.Auto : _currentThermalMode;
        RaiseStateChanged();
    }

    public async Task<bool> SetGraphicsModeAsync(GraphicsSwitcherMode mode)
    {
        try
        {
            _lastGraphicsRequestMode = mode.ToString();
            _lastGraphicsRequestReturnCode = null;

            if (!IsGraphicsModeSupported(mode))
            {
                Log("Graphics mode " + mode + " is not supported on this platform.");
                RaiseStateChanged();
                return false;
            }

            int returnCode = await _omenBiosClient.SetGraphicsModeAsync(mode).ConfigureAwait(false);
            _lastGraphicsRequestReturnCode = returnCode;
            Log("Requested graphics mode change to " + mode + ". BIOS return code: " + returnCode + ".");
            RefreshGraphicsMode();
            RaiseStateChanged();
            return returnCode == 0;
        }
        catch (Exception ex)
        {
            Log("Graphics mode change failed: " + ex.Message);
            return false;
        }
    }

    private void RaiseStateChanged()
    {
        StateChanged?.Invoke(this, new PerformanceControlState
        {
            Initialized = _initialized,
            Available = _available,
            CurrentMode = _currentMode.ToString(),
            CurrentThermalMode = _currentThermalMode.ToString(),
            CurrentLegacyFanMode = _currentLegacyFanMode.ToString(),
            CurrentGraphicsMode = _currentGraphicsMode.ToString(),
            GraphicsModeSwitchSupported = _graphicsModeSwitchSupported,
            GraphicsSupportsUma = _graphicsSupportsUma,
            GraphicsSupportsHybrid = _graphicsSupportsHybrid,
            GraphicsNeedsReboot = _graphicsNeedsReboot,
            GraphicsModeSwitchBits = _graphicsModeSwitchBits,
            LastGraphicsRequestMode = _lastGraphicsRequestMode,
            LastGraphicsRequestReturnCode = _lastGraphicsRequestReturnCode,
            ExtremeUnlocked = _extremeUnlocked,
            UnleashVisible = _unleashVisible,
            ThermalUiType = _thermalModeUiType.ToString(),
            SupportModes = _supportModes.ToArray()
        });
    }

    private static bool IsUnleashedMode(PerformanceMode mode)
    {
        return mode == PerformanceMode.Extreme || (int)mode == 4;
    }

    private static byte MapPerformanceModeToType26Value(PerformanceMode mode)
    {
        switch (mode)
        {
            case PerformanceMode.Eco:
            case PerformanceMode.Default:
                return 48;
            case PerformanceMode.Performance:
                return 49;
            default:
                return IsUnleashedMode(mode) ? (byte)4 : (byte)0;
        }
    }

    private static byte[] MapPerformanceModeToType34Payload(PerformanceMode mode)
    {
        bool isEco = mode == PerformanceMode.Eco;
        bool isPerfLike = mode == PerformanceMode.Performance || IsUnleashedMode(mode);

        byte tgpEnable = isPerfLike ? (byte)1 : (byte)0;
        byte ppabEnable = isEco ? (byte)0 : (byte)1;
        byte dState = 1;
        byte gps = 87;
        return new[] { tgpEnable, ppabEnable, dState, gps };
    }

    private async Task<bool> TrySetPerformanceModeFirmwareAsync(PerformanceMode mode)
    {
        try
        {
            byte type26Value = MapPerformanceModeToType26Value(mode);
            if (type26Value == 0)
            {
                Log("Firmware mode mapping is unknown for " + mode + "; no BIOS-only write path available.");
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
                    command: 131080,
                    commandType: 26,
                    payload: payload,
                    returnDataSize: 4,
                    logPrefix: "Firmware SetMode",
                    onResult: r =>
                    {
                        _lastPerfType26ExecuteResult = r.ExecuteResult;
                        _lastPerfType26ReturnCode = r.ReturnCode;
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
                command: 131080,
                commandType: 34,
                payload: type34,
                returnDataSize: 4,
                logPrefix: "Firmware GPU status",
                onResult: r =>
                {
                    _lastPerfType34ExecuteResult = r.ExecuteResult;
                    _lastPerfType34ReturnCode = r.ReturnCode;
                }).ConfigureAwait(false);

            if (!type34Ok)
            {
                return false;
            }

            if (mode == PerformanceMode.Performance || IsUnleashedMode(mode))
            {
                byte[] type41Payload = new byte[4]
                {
                    255,
                    255,
                    255,
                    45
                };

                bool type41Ok = await TryFirmwareSetAsync(
                    command: 131080,
                    commandType: 41,
                    payload: type41Payload,
                    returnDataSize: 4,
                    logPrefix: "Firmware TPP/TDP",
                    onResult: r =>
                    {
                        _lastPerfType41ExecuteResult = r.ExecuteResult;
                        _lastPerfType41ReturnCode = r.ReturnCode;
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
            Log("Firmware performance mode change failed: " + ex.Message);
            return false;
        }
    }

    private async Task<bool> TrySetMaxFanAsync(bool enabled)
    {
        try
        {
            bool requestedEnabled = enabled;
            bool ok = await _omenBiosClient.SetMaxFanAsync(enabled).ConfigureAwait(false);
            _lastMaxFanExecuteResult = ok;
            _lastMaxFanReturnCode = ok ? 0 : -1;

            if (!ok)
            {
                return false;
            }

            bool immediateReadback = await _omenBiosClient.GetMaxFanEnabledAsync().ConfigureAwait(false);
            await Task.Delay(1200).ConfigureAwait(false);
            bool delayedReadback = await _omenBiosClient.GetMaxFanEnabledAsync().ConfigureAwait(false);
            TrackEvent("Firmware", "Set max fan requested=" + (requestedEnabled ? "On" : "Off") + ", readbackNow=" + (immediateReadback ? "On" : "Off") + ", readbackDelayed=" + (delayedReadback ? "On" : "Off"));
            if (delayedReadback != requestedEnabled)
            {
                Log("Firmware MaxFan did not stick (requested=" + requestedEnabled + ", delayedReadback=" + delayedReadback + ").");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Log("Firmware max fan change failed: " + ex.Message);
            return false;
        }
    }

    private async Task<bool> TryFirmwareSetAsync(int command, int commandType, byte[] payload, int returnDataSize, string logPrefix, Action<OmenBiosClient.BiosWmiResult> onResult)
    {
        OmenBiosClient.BiosWmiResult result = await _omenBiosClient.ExecuteAsync(
            command: command,
            commandType: commandType,
            inputData: payload,
            returnDataSize: returnDataSize).ConfigureAwait(false);

        onResult?.Invoke(result);
        Log(logPrefix + ": cmd=" + command + " type=" + commandType + " input=[" + string.Join(",", payload ?? Array.Empty<byte>()) + "] exec=" + result.ExecuteResult + " rc=" + result.ReturnCode + " out=" + FormatReturnData(result.ReturnData));

        if (result.ExecuteResult && result.ReturnCode == 0)
        {
            return true;
        }

        Log(logPrefix + " failed: cmd=" + command + " type=" + commandType + " biosError=" + _omenBiosClient.LastError);
        return false;
    }

    private async Task RefreshThermalFromMaxFanAsync()
    {
        try
        {
            bool maxFanEnabled = await _omenBiosClient.GetMaxFanEnabledAsync().ConfigureAwait(false);
            _currentLegacyFanMode = maxFanEnabled ? FanMode.Turbo : FanMode.Normal;
            _currentThermalMode = maxFanEnabled ? ThermalControl.Max : ThermalControl.Auto;
            TrackEvent("Firmware", "Refresh max fan=" + (maxFanEnabled ? "On" : "Off"));
        }
        catch (Exception ex)
        {
            Log("Firmware max fan refresh failed: " + ex.Message);
        }
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

    private void RefreshGraphicsMode()
    {
        try
        {
            _currentGraphicsMode = _omenBiosClient.GetGraphicsModeAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log("Graphics mode read failed: " + ex.Message);
            _currentGraphicsMode = GraphicsSwitcherMode.Unknown;
        }
    }

    private void RefreshGraphicsSupport()
    {
        try
        {
            SystemDesignDataInfo systemDesignDataInfo = _omenBiosClient.GetSystemDesignDataInfoAsync().GetAwaiter().GetResult();
            _graphicsModeSwitchBits = systemDesignDataInfo.RawGpuModeSwitch;
            _graphicsModeSwitchReadSucceeded = systemDesignDataInfo.ReadSucceeded;
            RefreshGraphicsSupportFromProbe(systemDesignDataInfo);
        }
        catch (Exception ex)
        {
            Log("Graphics support read failed: " + ex.Message);
            _graphicsModeSwitchBits = 0;
            _graphicsModeSwitchReadSucceeded = false;
            RefreshGraphicsSupportFromFallback();
        }
    }

    private void RefreshGraphicsSupportFromProbe(SystemDesignDataInfo systemDesignDataInfo)
    {
        if (systemDesignDataInfo.ReadSucceeded && systemDesignDataInfo.SupportsGraphicsSwitching)
        {
            _graphicsModeSwitchSupported = true;
            _graphicsSupportsHybrid = true;
            _graphicsSupportsUma = true;
            _graphicsNeedsReboot = true;
            _helperGraphicsSupportAvailable = false;
            _helperGraphicsSupportedModes = -1;
            _helperGraphicsSupportsHybrid = false;
            _helperGraphicsSupportsUma = false;
            return;
        }

        if (TryReadHelperGraphicsSupport(out int supportedModes, out bool supportsHybrid, out bool supportsUma))
        {
            _helperGraphicsSupportAvailable = true;
            _helperGraphicsSupportedModes = supportedModes;
            _helperGraphicsSupportsHybrid = supportsHybrid;
            _helperGraphicsSupportsUma = supportsUma;

            bool helperSupportsGraphicsSwitching = supportedModes > 0 || supportsHybrid || supportsUma;
            _graphicsModeSwitchSupported = helperSupportsGraphicsSwitching;
            _graphicsSupportsHybrid = helperSupportsGraphicsSwitching;
            _graphicsSupportsUma = helperSupportsGraphicsSwitching;
            _graphicsNeedsReboot = true;

            Log("Graphics support BIOS probe was ambiguous; using HP helper fallback for button gating (SupportedModes=" + supportedModes + ", SupportedUMAmode=" + supportsUma + ").");
            return;
        }

        _helperGraphicsSupportAvailable = false;
        _helperGraphicsSupportedModes = -1;
        _helperGraphicsSupportsHybrid = false;
        _helperGraphicsSupportsUma = false;
        _graphicsModeSwitchSupported = false;
        _graphicsSupportsHybrid = false;
        _graphicsSupportsUma = false;
        _graphicsNeedsReboot = false;
    }

    private void RefreshGraphicsSupportFromFallback()
    {
        if (TryReadHelperGraphicsSupport(out int supportedModes, out bool supportsHybrid, out bool supportsUma))
        {
            _helperGraphicsSupportAvailable = true;
            _helperGraphicsSupportedModes = supportedModes;
            _helperGraphicsSupportsHybrid = supportsHybrid;
            _helperGraphicsSupportsUma = supportsUma;

            bool helperSupportsGraphicsSwitching = supportedModes > 0 || supportsHybrid || supportsUma;
            _graphicsModeSwitchSupported = helperSupportsGraphicsSwitching;
            _graphicsSupportsHybrid = helperSupportsGraphicsSwitching;
            _graphicsSupportsUma = helperSupportsGraphicsSwitching;
            _graphicsNeedsReboot = true;
            return;
        }

        _graphicsModeSwitchSupported = false;
        _graphicsSupportsHybrid = false;
        _graphicsSupportsUma = false;
        _graphicsNeedsReboot = false;
    }

    private bool TryReadHelperGraphicsSupport(out int supportedModes, out bool supportsHybrid, out bool supportsUma)
    {
        supportedModes = -1;
        supportsHybrid = false;
        supportsUma = false;

        try
        {
            Assembly assembly = TryLoadHpAssembly("HP.Omen.Core.Model.Device.dll");
            Type helperType = assembly?.GetType("HP.Omen.Core.Model.Device.Models.GraphicsSwitcherHelper");
            if (helperType == null)
            {
                return false;
            }

            supportedModes = GetStaticValue<int>(helperType, "SupportedModes", -1);
            supportsHybrid = (supportedModes & 0x02) != 0;
            supportsUma = GetStaticValue<bool>(helperType, "SupportedUMAmode", false);
            return supportedModes >= 0;
        }
        catch
        {
            return false;
        }
    }

    private static Assembly TryLoadHpAssembly(string fileName)
    {
        try
        {
            string simpleName = Path.GetFileNameWithoutExtension(fileName);
            return Assembly.Load(simpleName);
        }
        catch
        {
        }

        string[] searchRoots =
        {
            @"C:\Program Files\HP\SystemOptimizer",
            @"C:\Program Files\HP\Overlay",
            @"C:\Program Files\WindowsApps"
        };

        foreach (string root in searchRoots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            try
            {
                string path = Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories).FirstOrDefault();
                if (!string.IsNullOrEmpty(path))
                {
                    return Assembly.LoadFrom(path);
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private static T GetStaticValue<T>(Type type, string propertyName, T fallback)
    {
        try
        {
            PropertyInfo property = type?.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static);
            if (property == null)
            {
                return fallback;
            }

            object value = property.GetValue(null, null);
            if (value is T typedValue)
            {
                return typedValue;
            }

            if (value != null)
            {
                return (T)Convert.ChangeType(value, typeof(T));
            }
        }
        catch
        {
        }

        return fallback;
    }

    private bool IsGraphicsModeSupported(GraphicsSwitcherMode mode)
    {
        switch (mode)
        {
            case GraphicsSwitcherMode.Hybrid:
                return _graphicsModeSwitchSupported;
            case GraphicsSwitcherMode.UMAMode:
                return _graphicsModeSwitchSupported;
            default:
                return false;
        }
    }

    private void Log(string message)
    {
        string formatted = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message;
        LogMessage?.Invoke(this, formatted);
    }

    private void TrackEvent(string channel, string detail)
    {
        lock (_diagnosticsSync)
        {
            _recentEvents.Enqueue("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + channel + " " + detail);
            while (_recentEvents.Count > 20)
            {
                _recentEvents.Dequeue();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _omenBiosClient?.Dispose();
    }
}
