using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
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
    private readonly string _performanceModeStatePath;

    private bool _started;
    private bool _disposed;
    private bool _initialized;
    private bool _available;
    private PerformanceMode _currentMode = PerformanceMode.Default;
    private bool _currentModeKnown;
    private bool _currentModeIsInferred;
    private ThermalControl _currentThermalMode = ThermalControl.Auto;
    private FanMode _currentLegacyFanMode = FanMode.Normal;
    private GraphicsSwitcherMode _currentGraphicsMode = GraphicsSwitcherMode.Unknown;
    private bool _extremeUnlocked = true;
    private bool _unleashVisible = true;
    private ThermalModeOnUI _thermalModeUiType = (ThermalModeOnUI)0;
    private bool _graphicsSupportsUma;
    private bool _graphicsSupportsHybrid;
    private bool _graphicsModeSwitchSupported;
    private bool _graphicsNeedsReboot;
    private byte _graphicsModeSwitchBits;
    private bool _graphicsModeSwitchReadSucceeded;
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
    private byte[] _lastPerformanceStatusBlob = Array.Empty<byte>();
    private byte[] _previousPerformanceStatusBlob = Array.Empty<byte>();
    private string _lastPerformanceStatusBlobHash = string.Empty;
    private string _previousPerformanceStatusBlobHash = string.Empty;
    private string _lastPerformanceStatusBlobPreview = string.Empty;
    private int? _lastPerformanceStatusBlobChangedBytes;
    private int? _lastPerformanceStatusBlobReturnCode;
    private bool? _lastPerformanceStatusBlobExecuteResult;
    private byte[] _lastFanTargetBlob = Array.Empty<byte>();
    private byte[] _previousFanTargetBlob = Array.Empty<byte>();
    private string _lastFanTargetBlobHash = string.Empty;
    private string _previousFanTargetBlobHash = string.Empty;
    private string _lastFanTargetBlobPreview = string.Empty;
    private int? _lastFanTargetBlobChangedBytes;
    private int? _lastFanTargetBlobReturnCode;
    private bool? _lastFanTargetBlobExecuteResult;
    private readonly Queue<string> _recentEvents = new Queue<string>();

    public event EventHandler<PerformanceControlState> StateChanged;

    public event EventHandler<string> LogMessage;

    public OmenPerformanceController()
    {
        _sessionId = Process.GetCurrentProcess().SessionId;
        _performanceModeStatePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OmenHelper",
            "performance-mode.txt");
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
            FormatPerformanceMode(PerformanceMode.Default),
            FormatPerformanceMode(PerformanceMode.Performance),
            FormatPerformanceMode(PerformanceMode.Eco),
            "Unleashed"
        };
        _currentModeKnown = false;
        LoadRememberedPerformanceMode();

        _started = true;

        _ = RequestInitializationAsync();

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
            RefreshGraphicsMode();
            RefreshGraphicsSupport();
            await RefreshPerformanceStatusBlobAsync().ConfigureAwait(false);
            _initialized = true;
            _available = true;
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
        builder.AppendLine("  Mode: " + DescribeCurrentMode());
        builder.AppendLine("  Mode Inferred: " + _currentModeIsInferred);
        builder.AppendLine("  Thermal: " + _currentThermalMode);
        builder.AppendLine("  Legacy Fan: " + _currentLegacyFanMode);
        builder.AppendLine("  Fan Minimum RPM: " + GetFanMinimumRpmForMode(_currentMode));
        builder.AppendLine("  Graphics: " + _currentGraphicsMode);
        if (!string.IsNullOrWhiteSpace(_lastPerformanceRequestMode))
        {
            builder.AppendLine("  Last Perf Request: " + _lastPerformanceRequestMode + " (" + _lastPerformanceRequestPath + ")");
            builder.AppendLine("    BIOS 131080/26 exec: " + (_lastPerfType26ExecuteResult.HasValue ? _lastPerfType26ExecuteResult.Value.ToString() : "<none>") + " rc: " + (_lastPerfType26ReturnCode.HasValue ? _lastPerfType26ReturnCode.Value.ToString() : "<none>"));
            builder.AppendLine("    BIOS 131080/34 exec: " + (_lastPerfType34ExecuteResult.HasValue ? _lastPerfType34ExecuteResult.Value.ToString() : "<none>") + " rc: " + (_lastPerfType34ReturnCode.HasValue ? _lastPerfType34ReturnCode.Value.ToString() : "<none>"));
            builder.AppendLine("    BIOS 131080/41 exec: " + (_lastPerfType41ExecuteResult.HasValue ? _lastPerfType41ExecuteResult.Value.ToString() : "<none>") + " rc: " + (_lastPerfType41ReturnCode.HasValue ? _lastPerfType41ReturnCode.Value.ToString() : "<none>"));
        }
        builder.AppendLine("  Last MaxFan exec: " + (_lastMaxFanExecuteResult.HasValue ? _lastMaxFanExecuteResult.Value.ToString() : "<none>") + " rc: " + (_lastMaxFanReturnCode.HasValue ? _lastMaxFanReturnCode.Value.ToString() : "<none>"));
        builder.AppendLine("  Performance Status Blob exec: " + (_lastPerformanceStatusBlobExecuteResult.HasValue ? _lastPerformanceStatusBlobExecuteResult.Value.ToString() : "<none>") + " rc: " + (_lastPerformanceStatusBlobReturnCode.HasValue ? _lastPerformanceStatusBlobReturnCode.Value.ToString() : "<none>"));
        builder.AppendLine("  Performance Status Blob hash: " + (string.IsNullOrWhiteSpace(_lastPerformanceStatusBlobHash) ? "<none>" : _lastPerformanceStatusBlobHash));
        builder.AppendLine("  Performance Status Blob first 32 bytes: " + (string.IsNullOrWhiteSpace(_lastPerformanceStatusBlobPreview) ? "<none>" : _lastPerformanceStatusBlobPreview));
        builder.AppendLine("  Performance Status Blob sensors: " + DescribePerformanceStatusBlobSensors(_lastPerformanceStatusBlob));
        builder.AppendLine("  Performance Status Blob changed bytes: " + (_lastPerformanceStatusBlobChangedBytes.HasValue ? _lastPerformanceStatusBlobChangedBytes.Value.ToString() : "<n/a>"));
        builder.AppendLine("  Performance Status Blob previous hash: " + (string.IsNullOrWhiteSpace(_previousPerformanceStatusBlobHash) ? "<none>" : _previousPerformanceStatusBlobHash));
        builder.AppendLine("  Fan Minimum Blob (BIOS 131080/46) exec: " + (_lastFanTargetBlobExecuteResult.HasValue ? _lastFanTargetBlobExecuteResult.Value.ToString() : "<none>") + " rc: " + (_lastFanTargetBlobReturnCode.HasValue ? _lastFanTargetBlobReturnCode.Value.ToString() : "<none>"));
        builder.AppendLine("  Fan Minimum Blob (BIOS 131080/46) hash: " + (string.IsNullOrWhiteSpace(_lastFanTargetBlobHash) ? "<none>" : _lastFanTargetBlobHash));
        builder.AppendLine("  Fan Minimum Blob (BIOS 131080/46) preview: " + (string.IsNullOrWhiteSpace(_lastFanTargetBlobPreview) ? "<none>" : _lastFanTargetBlobPreview));
        builder.AppendLine("  Fan Minimum Blob (BIOS 131080/46) changed bytes: " + (_lastFanTargetBlobChangedBytes.HasValue ? _lastFanTargetBlobChangedBytes.Value.ToString() : "<n/a>"));
        builder.AppendLine("  Fan Minimum Blob (BIOS 131080/46) previous hash: " + (string.IsNullOrWhiteSpace(_previousFanTargetBlobHash) ? "<none>" : _previousFanTargetBlobHash));
        builder.AppendLine("  Thermal UI Type: " + _thermalModeUiType);
        builder.AppendLine("  Extreme Unlocked: " + _extremeUnlocked);
        builder.AppendLine("  Unleash Visible: " + _unleashVisible);
        builder.AppendLine("  Support Modes: " + string.Join(", ", _supportModes));
        builder.AppendLine();

        await RefreshPerformanceStatusBlobAsync().ConfigureAwait(false);

        try
        {
            byte[] systemDesignData = await _omenBiosClient.GetSystemDesignDataAsync().ConfigureAwait(false);
            SystemDesignDataInfo systemDesignDataInfo = await _omenBiosClient.GetSystemDesignDataInfoAsync().ConfigureAwait(false);
            _graphicsModeSwitchBits = systemDesignDataInfo.RawGpuModeSwitch;
            _graphicsModeSwitchReadSucceeded = systemDesignDataInfo.ReadSucceeded;
            RefreshGraphicsSupportFromProbe(systemDesignDataInfo);
            RaiseStateChanged();
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
        ThermalControl thermalControlToRestore = _currentThermalMode;

        _lastPerformanceRequestMode = FormatPerformanceMode(mode);
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
            _currentModeIsInferred = true;
            SaveRememberedPerformanceMode(mode);

            bool thermalRestoreOk = await TrySetThermalModeStateAsync(thermalControlToRestore).ConfigureAwait(false);
            if (!thermalRestoreOk && thermalControlToRestore != ThermalControl.Manual)
            {
                Log("Thermal mode restore after performance mode change failed.");
            }

            bool fanTargetOk = await ApplyFanMinimumBlobAsync("performance mode change").ConfigureAwait(false);
            if (!fanTargetOk)
            {
                Log("Fan target blob restore after performance mode change failed.");
            }

            await Task.Delay(250).ConfigureAwait(false);
            await RefreshPerformanceStatusBlobAsync().ConfigureAwait(false);
            RaiseStateChanged();
            return;
        }

        _lastPerformanceRequestPath = "Failed";
        Log("Performance mode change failed (firmware only).");
    }

    public async Task SetThermalModeAsync(ThermalControl thermalControl)
    {
        bool success = await TrySetThermalModeStateAsync(thermalControl).ConfigureAwait(false);
        if (!success)
        {
            if (thermalControl != ThermalControl.Manual)
            {
                Log("Thermal mode change failed (firmware only).");
            }

            return;
        }

        bool fanTargetOk = await ApplyFanMinimumBlobAsync("thermal mode change").ConfigureAwait(false);
        if (!fanTargetOk)
        {
            Log("Fan target blob restore after thermal mode change failed.");
        }

        await RefreshPerformanceStatusBlobAsync().ConfigureAwait(false);
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

        bool fanTargetOk = await ApplyFanMinimumBlobAsync("legacy fan mode change").ConfigureAwait(false);
        if (!fanTargetOk)
        {
            Log("Fan target blob restore after legacy fan mode change failed.");
        }

        await RefreshPerformanceStatusBlobAsync().ConfigureAwait(false);
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
            CurrentModeKnown = _currentModeKnown,
            CurrentMode = DescribeCurrentMode(),
            CurrentModeIsInferred = _currentModeIsInferred,
            CurrentThermalMode = _currentThermalMode.ToString(),
            CurrentLegacyFanMode = _currentLegacyFanMode.ToString(),
            CurrentFanMinimumRpm = GetFanMinimumRpmForMode(_currentMode),
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

    private static string FormatPerformanceMode(PerformanceMode mode)
    {
        if (mode == PerformanceMode.Default)
        {
            return "Balanced";
        }

        if (mode == PerformanceMode.Extreme)
        {
            return "Unleashed";
        }

        return mode.ToString();
    }

    private string DescribeCurrentMode()
    {
        if (_currentModeKnown || _currentModeIsInferred)
        {
            return FormatPerformanceMode(_currentMode);
        }

        return "Unknown";
    }

    private void LoadRememberedPerformanceMode()
    {
        try
        {
            if (!File.Exists(_performanceModeStatePath))
            {
                return;
            }

            string persisted = File.ReadAllText(_performanceModeStatePath).Trim();
            if (!TryParseRememberedPerformanceMode(persisted, out PerformanceMode mode))
            {
                Log("Ignored persisted performance mode value: " + persisted);
                return;
            }

            _currentMode = mode;
            _currentModeIsInferred = true;
            Log("Loaded remembered performance mode: " + FormatPerformanceMode(mode));
        }
        catch (Exception ex)
        {
            Log("Performance mode memory load failed: " + ex.Message);
        }
    }

    private void SaveRememberedPerformanceMode(PerformanceMode mode)
    {
        try
        {
            string directory = Path.GetDirectoryName(_performanceModeStatePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_performanceModeStatePath, mode.ToString());
        }
        catch (Exception ex)
        {
            Log("Performance mode memory save failed: " + ex.Message);
        }
    }

    private static bool TryParseRememberedPerformanceMode(string value, out PerformanceMode mode)
    {
        if (string.Equals(value, "Balanced", StringComparison.OrdinalIgnoreCase))
        {
            mode = PerformanceMode.Default;
            return true;
        }

        if (string.Equals(value, "Unleashed", StringComparison.OrdinalIgnoreCase))
        {
            mode = PerformanceMode.Extreme;
            return true;
        }

        return Enum.TryParse(value, true, out mode);
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
        Log(logPrefix + ": cmd=" + command + " type=" + commandType + " input=" + FormatInputData(payload) + " exec=" + result.ExecuteResult + " rc=" + result.ReturnCode + " out=" + FormatReturnData(result.ReturnData));

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

    private async Task<bool> ApplyFanMinimumBlobAsync(string source)
    {
        try
        {
            byte[] payload = BuildFanMinimumBlobForCurrentMode();
            OmenBiosClient.BiosWmiResult result = await _omenBiosClient.SetPerformanceStatusBlobAsync(payload).ConfigureAwait(false);

            byte[] previousBlob;
            string previousHash;
            lock (_diagnosticsSync)
            {
                previousBlob = _lastFanTargetBlob ?? Array.Empty<byte>();
                previousHash = _lastFanTargetBlobHash;
                _previousFanTargetBlob = previousBlob;
                _previousFanTargetBlobHash = previousHash;
                _lastFanTargetBlob = payload;
                _lastFanTargetBlobHash = ComputeSha256Hex(payload);
                _lastFanTargetBlobPreview = FormatBytePreview(payload, 32);
                _lastFanTargetBlobChangedBytes = CountDifferentBytes(previousBlob, payload);
                _lastFanTargetBlobReturnCode = result.ReturnCode;
                _lastFanTargetBlobExecuteResult = result.ExecuteResult;
            }

            TrackEvent(
                "Firmware",
                "Write fan minimum blob source=" + source +
                ", mode=" + FormatPerformanceMode(_currentMode) +
                ", minRpm=" + GetFanMinimumRpmForMode(_currentMode) +
                ", rc=" + result.ReturnCode);

            return result.ExecuteResult && result.ReturnCode == 0;
        }
        catch (Exception ex)
        {
            Log("Fan target blob write failed: " + ex.Message);
            return false;
        }
    }

    private async Task<bool> TrySetThermalModeStateAsync(ThermalControl thermalControl)
    {
        if (thermalControl == ThermalControl.Manual)
        {
            Log("ThermalControl.Manual is not implemented (would require custom fan curve control).");
            return false;
        }

        bool enableMaxFan = thermalControl == ThermalControl.Max;
        bool success = await TrySetMaxFanAsync(enableMaxFan).ConfigureAwait(false);
        if (!success)
        {
            return false;
        }

        _currentThermalMode = thermalControl;
        _currentLegacyFanMode = enableMaxFan ? FanMode.Turbo : FanMode.Normal;
        return true;
    }

    private byte[] BuildFanMinimumBlobForCurrentMode()
    {
        int minimumRpm = GetFanMinimumRpmForMode(_currentMode);
        byte minimumValue = (byte)Math.Max(0, Math.Min(255, minimumRpm / 100));
        byte[] payload = new byte[128];
        payload[0] = minimumValue;
        payload[1] = minimumValue;
        return payload;
    }

    private static int GetFanMinimumRpmForMode(PerformanceMode mode)
    {
        switch (mode)
        {
            case PerformanceMode.Eco:
                return 0;
            case PerformanceMode.Performance:
            case PerformanceMode.Extreme:
                return 2800;
            case PerformanceMode.Default:
            default:
                return 2200;
        }
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

    private async Task RefreshPerformanceStatusBlobAsync()
    {
        try
        {
            OmenBiosClient.BiosWmiResult result = await _omenBiosClient.GetPerformanceStatusBlobAsync().ConfigureAwait(false);
            byte[] blob = result.ExecuteResult && result.ReturnCode == 0
                ? (result.ReturnData ?? Array.Empty<byte>())
                : Array.Empty<byte>();

            byte[] previousBlob;
            string previousHash;
            lock (_diagnosticsSync)
            {
                previousBlob = _lastPerformanceStatusBlob ?? Array.Empty<byte>();
                previousHash = _lastPerformanceStatusBlobHash;
                _previousPerformanceStatusBlob = previousBlob;
                _previousPerformanceStatusBlobHash = previousHash;
                _lastPerformanceStatusBlob = blob;
                _lastPerformanceStatusBlobHash = ComputeSha256Hex(blob);
                _lastPerformanceStatusBlobPreview = FormatBytePreview(blob, 32);
                _lastPerformanceStatusBlobChangedBytes = CountDifferentBytes(previousBlob, blob);
                _lastPerformanceStatusBlobReturnCode = result.ReturnCode;
                _lastPerformanceStatusBlobExecuteResult = result.ExecuteResult;
            }

            TrackEvent(
                "Firmware",
                "Read performance status blob hash=" + _lastPerformanceStatusBlobHash +
                ", changedBytes=" + (_lastPerformanceStatusBlobChangedBytes.HasValue ? _lastPerformanceStatusBlobChangedBytes.Value.ToString() : "<n/a>") +
                ", rc=" + result.ReturnCode);
        }
        catch (Exception ex)
        {
            Log("Performance status blob read failed: " + ex.Message);
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
            Log(
                "Graphics support refresh: BIOS bits=0x" + _graphicsModeSwitchBits.ToString("X2") +
                ", readSucceeded=" + _graphicsModeSwitchReadSucceeded +
                ", supported=" + _graphicsModeSwitchSupported +
                ", uma=" + _graphicsSupportsUma +
                ", hybrid=" + _graphicsSupportsHybrid + ".");
        }
        catch (Exception ex)
        {
            Log("Graphics support read failed: " + ex.Message);
            _graphicsModeSwitchBits = 0;
            _graphicsModeSwitchReadSucceeded = false;
            RefreshGraphicsSupportFromFallback();
            Log("Graphics support refresh: BIOS bits=0x00, readSucceeded=False, supported=False, uma=False, hybrid=False.");
        }
    }

    private void RefreshGraphicsSupportFromProbe(SystemDesignDataInfo systemDesignDataInfo)
    {
        if (systemDesignDataInfo.ReadSucceeded && systemDesignDataInfo.SupportsGraphicsSwitching)
        {
            _graphicsModeSwitchSupported = true;
            _graphicsSupportsHybrid = systemDesignDataInfo.HasHybridSlot;
            _graphicsSupportsUma = systemDesignDataInfo.HasIntegratedSlot;
            _graphicsNeedsReboot = true;
            return;
        }
        _graphicsModeSwitchSupported = false;
        _graphicsSupportsHybrid = false;
        _graphicsSupportsUma = false;
        _graphicsNeedsReboot = false;
    }

    private void RefreshGraphicsSupportFromFallback()
    {
        _graphicsModeSwitchSupported = false;
        _graphicsSupportsHybrid = false;
        _graphicsSupportsUma = false;
        _graphicsNeedsReboot = false;
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

    private static string ComputeSha256Hex(byte[] data)
    {
        if (data == null || data.Length == 0)
        {
            return "<empty>";
        }

        using (SHA256 sha256 = SHA256.Create())
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
