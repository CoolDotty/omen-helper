using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Hp.Bridge.Client.SDKs.PerformanceControl.Enums;
using HP.Omen.Core.Common;
using HP.Omen.Core.Common.Enums;
using HP.Omen.Core.Common.PipeUtility;
using HP.Omen.Core.Common.PowerControl;
using HP.Omen.Core.Common.PowerControl.Enum;
using HP.Omen.Core.Common.Utilities;
using HP.Omen.Core.Model.DataStructure.Modules.GraphicsSwitcher.Enums;
using HP.Omen.Core.Model.DataStructure.Modules.FanControl.Enums;
using HP.Omen.Core.Model.DataStructure.Structs;
using OmenHelper.Models;

namespace OmenHelper.Services;

internal sealed class OmenPerformanceController : IDisposable
{
    private readonly int _sessionId;
    private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
    private readonly OmenHsaClient _omenHsaClient = new OmenHsaClient();
    private readonly object _diagnosticsSync = new object();

    private PipeServerV2 _performanceMonitorServer;

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
    private int _graphicsSupportedModes;
    private bool _graphicsSupportsUma;
    private bool _graphicsSupportsHybrid;
    private bool _graphicsSupportsDiscrete;
    private bool _graphicsNeedsReboot = true;
    private string _lastGraphicsRequestMode = string.Empty;
    private int? _lastGraphicsRequestReturnCode;
    private List<string> _supportModes = new List<string>();
    private PerformanceMonitorSample _cpuSample = new PerformanceMonitorSample();
    private PerformanceMonitorSample _gpuSample = new PerformanceMonitorSample();
    private string _lastCpuMonitorJson = string.Empty;
    private string _lastGpuMonitorJson = string.Empty;
    private string _lastPerformanceRequestMode = string.Empty;
    private string _lastPerformanceRequestPath = string.Empty;
    private int? _lastPerfType26ReturnCode;
    private int? _lastPerfType34ReturnCode;
    private int? _lastPerfType41ReturnCode;
    private readonly Queue<string> _recentEvents = new Queue<string>();

    public event EventHandler<PerformanceControlState> StateChanged;

    public event EventHandler<TelemetrySnapshot> TelemetryChanged;

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

        string performanceMonitorServerName = GetPerformanceMonitorServerName();
        _performanceMonitorServer = new PipeServerV2(performanceMonitorServerName, ReceivePerformanceMonitorMessage);
        _performanceMonitorServer.StartServer();
        PerformanceMonitorHelper.RegisterPerformanceMonitoring(register: true, performanceMonitorServerName, PerformanceMonitorRegisterType.CPU_SIMPLE, runByTask: true);
        PerformanceMonitorHelper.RegisterPerformanceMonitoring(register: true, performanceMonitorServerName, PerformanceMonitorRegisterType.GPU_SIMPLE, runByTask: true);

        _started = true;

        _supportModes = new List<string>
        {
            nameof(PerformanceMode.Default),
            nameof(PerformanceMode.Performance),
            nameof(PerformanceMode.Cool),
            nameof(PerformanceMode.Quiet),
            nameof(PerformanceMode.Eco),
            "Unleash"
        };

        _ = RequestInitializationAsync();
        RefreshGraphicsMode();
        RefreshGraphicsSupport();
        RaiseStateChanged();
        Log("Started firmware control path (BIOS/WMI) for performance/thermal/fan. HP control pipe is not used.");
    }

    public async Task RequestInitializationAsync()
    {
        try
        {
            MaxFanMode maxFan = await _omenHsaClient.GetMaxFanAsync().ConfigureAwait(false);
            bool maxFanEnabled = maxFan == MaxFanMode.On;
            _currentLegacyFanMode = maxFanEnabled ? FanMode.Turbo : FanMode.Normal;
            _currentThermalMode = maxFanEnabled ? ThermalControl.Max : ThermalControl.Auto;
            _initialized = true;
            _available = true;
            RefreshGraphicsMode();
            TrackEvent("Firmware", "Read max fan=" + maxFan);
            RaiseStateChanged();
        }
        catch (Exception ex)
        {
            Log("Firmware state refresh failed: " + ex.Message);
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
            builder.AppendLine("    BIOS 131080/26 rc: " + (_lastPerfType26ReturnCode.HasValue ? _lastPerfType26ReturnCode.Value.ToString() : "<none>"));
            builder.AppendLine("    BIOS 131080/34 rc: " + (_lastPerfType34ReturnCode.HasValue ? _lastPerfType34ReturnCode.Value.ToString() : "<none>"));
            builder.AppendLine("    BIOS 131080/41 rc: " + (_lastPerfType41ReturnCode.HasValue ? _lastPerfType41ReturnCode.Value.ToString() : "<none>"));
        }
        builder.AppendLine("  Thermal UI Type: " + _thermalModeUiType);
        builder.AppendLine("  Extreme Unlocked: " + _extremeUnlocked);
        builder.AppendLine("  Unleash Visible: " + _unleashVisible);
        builder.AppendLine("  Support Modes: " + string.Join(", ", _supportModes));
        builder.AppendLine();

        try
        {
            byte[] systemDesignData = _omenHsaClient.SystemDesignData;
            RefreshGraphicsSupport();
            builder.AppendLine("BIOS / Platform");
            builder.AppendLine("  SystemDesignData: " + ((systemDesignData != null && systemDesignData.Length > 0) ? BitConverter.ToString(systemDesignData) : "<empty>"));
            builder.AppendLine("  ShippingAdapterPowerRating: " + PowerControlHelper.ShippingAdapterPowerRating);
            builder.AppendLine("  IsBiosPerformanceModeSupport: " + PowerControlHelper.IsBiosPerformanceModeSupport);
            builder.AppendLine("  IsSwFanControlSupport: " + PowerControlHelper.IsSwFanControlSupport);
            builder.AppendLine("  IsExtremeModeSupport: " + PowerControlHelper.IsExtremeModeSupport);
            builder.AppendLine("  IsExtremeModeUnlock: " + PowerControlHelper.IsExtremeModeUnlock);
            RefreshGraphicsMode();
            builder.AppendLine("  GraphicsMode: " + _currentGraphicsMode);
            builder.AppendLine("  GraphicsSupportedModesRaw: " + _graphicsSupportedModes);
            builder.AppendLine("  GraphicsSupportsHybrid: " + _graphicsSupportsHybrid);
            builder.AppendLine("  GraphicsSupportsDiscrete: " + _graphicsSupportsDiscrete);
            builder.AppendLine("  GraphicsSupportsUMA: " + _graphicsSupportsUma);
            builder.AppendLine("  GraphicsNeedsReboot: " + _graphicsNeedsReboot);
            builder.AppendLine("  LastGraphicsRequestMode: " + (string.IsNullOrWhiteSpace(_lastGraphicsRequestMode) ? "<none>" : _lastGraphicsRequestMode));
            builder.AppendLine("  LastGraphicsRequestReturnCode: " + (_lastGraphicsRequestReturnCode.HasValue ? _lastGraphicsRequestReturnCode.Value.ToString() : "<none>"));
            try
            {
                builder.AppendLine("  MaxFan(BIOS): " + await _omenHsaClient.GetMaxFanAsync().ConfigureAwait(false));
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
            builder.AppendLine("Last CPU Monitor Payload");
            builder.AppendLine(string.IsNullOrWhiteSpace(_lastCpuMonitorJson) ? "<none>" : _lastCpuMonitorJson);
            builder.AppendLine();

            builder.AppendLine("Last GPU Monitor Payload");
            builder.AppendLine(string.IsNullOrWhiteSpace(_lastGpuMonitorJson) ? "<none>" : _lastGpuMonitorJson);
            builder.AppendLine();

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
        _lastPerfType34ReturnCode = null;
        _lastPerfType41ReturnCode = null;

        if (await TrySetPerformanceModeFirmwareAsync(mode).ConfigureAwait(false))
        {
            _lastPerformanceRequestPath = "Firmware";
            _currentMode = mode;
            RaiseStateChanged();
            return;
        }

        _lastPerformanceRequestPath = "FirmwareFailed";
        Log("Performance mode change failed through firmware path.");
    }

    public async Task SetThermalModeAsync(ThermalControl thermalControl)
    {
        // Confirmed firmware control for fan thermals is max-fan on/off (131080/39).
        bool enableMaxFan = thermalControl == ThermalControl.Max;
        bool success = await TrySetMaxFanAsync(enableMaxFan).ConfigureAwait(false);
        if (!success)
        {
            return;
        }

        _currentThermalMode = thermalControl;
        _currentLegacyFanMode = enableMaxFan ? FanMode.Turbo : FanMode.Normal;
        RaiseStateChanged();
    }

    public async Task SetLegacyFanModeAsync(FanMode mode)
    {
        // FanMode mapping observed in HP enum: Quiet=0, Normal=1, Turbo=2.
        // Firmware command 131080/39 only exposes max-fan toggle, so map Turbo->on and others->off.
        bool enableMaxFan = mode == FanMode.Turbo;
        bool success = await TrySetMaxFanAsync(enableMaxFan).ConfigureAwait(false);
        if (!success)
        {
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
            RefreshGraphicsSupport();
            _lastGraphicsRequestMode = mode.ToString();
            _lastGraphicsRequestReturnCode = null;

            if (!IsGraphicsModeSupported(mode))
            {
                Log("Graphics mode " + mode + " is not supported on this platform.");
                RaiseStateChanged();
                return false;
            }

            int returnCode = await _omenHsaClient.BiosWmiCmd_Set(2, 82, new byte[4]
            {
                Convert.ToByte(mode),
                0,
                0,
                0
            }).ConfigureAwait(false);

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

    private static bool IsUnleashedMode(PerformanceMode mode)
    {
        // On this platform, OMEN labels the mode value 4 as "Unleashed". HP's enum also calls it Extreme/L8.
        return mode == PerformanceMode.Extreme || (int)mode == 4;
    }

    private static byte MapPerformanceModeToType26Value(PerformanceMode mode)
    {
        // Observed in OMEN background logs (Transcend 14):
        // - Eco (256) -> 48
        // - Default/Balanced (0) -> 48
        // - Performance (1) -> 49
        // - Unleashed (4) -> 4
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
        // Maps to PowerControlBase.SetTgpPpabAsync(tgpEnable, ppabEnable, dState=1, gps=87) observed in logs.
        // - Eco:        0,0,1,87
        // - Default:    0,1,1,87
        // - Performance 1,1,1,87
        // - Unleashed:  1,1,1,87
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
                Log("Firmware mode mapping is unknown for " + mode + "; falling back to HP pipe control.");
                return false;
            }

            _lastPerfType26ReturnCode = await _omenHsaClient.BiosWmiCmd_Set(131080, 26, new byte[4]
            {
                255,
                type26Value,
                0,
                0
            }).ConfigureAwait(false);

            Log("Firmware SetMode: cmd=131080 type=26 input=[255," + type26Value + ",0,0] rc=" + _lastPerfType26ReturnCode);
            if (_lastPerfType26ReturnCode != 0)
            {
                return false;
            }

            byte[] type34 = MapPerformanceModeToType34Payload(mode);
            _lastPerfType34ReturnCode = await _omenHsaClient.BiosWmiCmd_Set(131080, 34, type34).ConfigureAwait(false);
            Log("Firmware GPU status: cmd=131080 type=34 input=[" + string.Join(",", type34) + "] rc=" + _lastPerfType34ReturnCode);
            if (_lastPerfType34ReturnCode != 0)
            {
                return false;
            }

            if (mode == PerformanceMode.Performance || IsUnleashedMode(mode))
            {
                // Observed OMEN background uses type=41 input=[255,255,255,45] during Performance/Unleashed.
                _lastPerfType41ReturnCode = await _omenHsaClient.BiosWmiCmd_Set(131080, 41, new byte[4]
                {
                    255,
                    255,
                    255,
                    45
                }).ConfigureAwait(false);

                Log("Firmware TPP/TDP: cmd=131080 type=41 input=[255,255,255,45] rc=" + _lastPerfType41ReturnCode);
                if (_lastPerfType41ReturnCode != 0)
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
            byte value = enabled ? (byte)1 : (byte)0;
            int returnCode = await _omenHsaClient.BiosWmiCmd_Set(131080, 39, new[] { value }).ConfigureAwait(false);
            Log("Firmware MaxFan: cmd=131080 type=39 input=[" + value + "] rc=" + returnCode);
            if (returnCode != 0)
            {
                return false;
            }

            MaxFanMode readback = await _omenHsaClient.GetMaxFanAsync().ConfigureAwait(false);
            bool readbackEnabled = readback == MaxFanMode.On;
            TrackEvent("Firmware", "Set max fan requested=" + enabled + ", readback=" + readback);
            return readbackEnabled == enabled;
        }
        catch (Exception ex)
        {
            Log("Firmware max fan change failed: " + ex.Message);
            return false;
        }
    }

    private void ReceivePerformanceMonitorMessage(object payload)
    {
        try
        {
            PerseusRevMsg message = payload as PerseusRevMsg;
            if (message == null)
            {
                return;
            }

            PerformanceMonitorPayload sample = _json.Deserialize<PerformanceMonitorPayload>(message.SendParameter as string);
            if (sample == null)
            {
                return;
            }

            PerformanceMonitorSample mappedSample = new PerformanceMonitorSample
            {
                TemperatureString = sample.TemperatureString,
                UsageString = sample.UsageString,
                TemperatureState = sample.TemperatureState
            };

            if (message.FuncType == (int)PerformanceMonitorRegisterType.GPU_SIMPLE)
            {
                _gpuSample = mappedSample;
                lock (_diagnosticsSync)
                {
                    _lastGpuMonitorJson = message.SendParameter as string ?? string.Empty;
                }
            }
            else if (message.FuncType == (int)PerformanceMonitorRegisterType.CPU_SIMPLE)
            {
                _cpuSample = mappedSample;
                lock (_diagnosticsSync)
                {
                    _lastCpuMonitorJson = message.SendParameter as string ?? string.Empty;
                }
            }

            TrackEvent("Monitor", "FuncType=" + message.FuncType + ", Payload=" + (message.SendParameter ?? "<null>"));
            TelemetryChanged?.Invoke(this, new TelemetrySnapshot
            {
                Cpu = CloneSample(_cpuSample),
                Gpu = CloneSample(_gpuSample)
            });
        }
        catch (Exception ex)
        {
            Log("Monitor receive failed: " + ex.Message);
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
            GraphicsSupportsUma = _graphicsSupportsUma,
            GraphicsSupportsHybrid = _graphicsSupportsHybrid,
            GraphicsSupportsDiscrete = _graphicsSupportsDiscrete,
            GraphicsNeedsReboot = _graphicsNeedsReboot,
            LastGraphicsRequestMode = _lastGraphicsRequestMode,
            LastGraphicsRequestReturnCode = _lastGraphicsRequestReturnCode,
            ExtremeUnlocked = _extremeUnlocked,
            UnleashVisible = _unleashVisible,
            ThermalUiType = _thermalModeUiType.ToString(),
            SupportModes = _supportModes.ToArray()
        });
    }

    private static PerformanceMonitorSample CloneSample(PerformanceMonitorSample source)
    {
        return new PerformanceMonitorSample
        {
            TemperatureString = source?.TemperatureString,
            UsageString = source?.UsageString,
            TemperatureState = source?.TemperatureState ?? 0
        };
    }

    private string GetPerformanceMonitorServerName()
    {
        return "HP.Omen.Overlay.PerformanceControl.PerformanceMonitor" + _sessionId;
    }

    private void RefreshGraphicsMode()
    {
        try
        {
            _currentGraphicsMode = _omenHsaClient.GetGraphicsMode();
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
            Assembly assembly = TryLoadHpAssembly("HP.Omen.Core.Model.Device.dll");
            Type helperType = assembly?.GetType("HP.Omen.Core.Model.Device.Models.GraphicsSwitcherHelper");
            Type deviceModelType = assembly?.GetType("HP.Omen.Core.Model.Device.Models.DeviceModel");

            int supportedModes = GetStaticValue<int>(helperType, "SupportedModes", 0);
            bool supportsUma = GetStaticValue<bool>(helperType, "SupportedUMAmode", false);
            bool platformAtOrAfter26C1 = InvokeStaticBool(deviceModelType, "IsCurrentPlatformAtOrAfter", "26C1");

            _graphicsSupportedModes = supportedModes;
            _graphicsSupportsHybrid = (supportedModes & 2) != 0;
            _graphicsSupportsDiscrete = (supportedModes & 4) != 0;
            _graphicsSupportsUma = supportsUma;
            _graphicsNeedsReboot = !platformAtOrAfter26C1;
        }
        catch (Exception ex)
        {
            Log("Graphics support read failed: " + ex.Message);
        }
    }

    private bool IsGraphicsModeSupported(GraphicsSwitcherMode mode)
    {
        switch (mode)
        {
            case GraphicsSwitcherMode.Hybrid:
                return _graphicsSupportsHybrid;
            case GraphicsSwitcherMode.Discrete:
                return _graphicsSupportsDiscrete;
            case GraphicsSwitcherMode.UMAMode:
                return _graphicsSupportsUma;
            default:
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

    private static bool InvokeStaticBool(Type type, string methodName, string argument)
    {
        try
        {
            MethodInfo method = type?.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
            if (method == null)
            {
                return false;
            }

            object value = method.Invoke(null, new object[] { argument });
            if (value is bool result)
            {
                return result;
            }
        }
        catch
        {
        }

        return false;
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

        try
        {
            string performanceMonitorServerName = GetPerformanceMonitorServerName();
            PerformanceMonitorHelper.RegisterPerformanceMonitoring(register: false, performanceMonitorServerName, PerformanceMonitorRegisterType.CPU_SIMPLE, runByTask: false);
            PerformanceMonitorHelper.RegisterPerformanceMonitoring(register: false, performanceMonitorServerName, PerformanceMonitorRegisterType.GPU_SIMPLE, runByTask: false);
        }
        catch
        {
        }

        _performanceMonitorServer?.Dispose();
    }

    private sealed class PerformanceMonitorPayload
    {
        public string TemperatureString { get; set; }

        public string UsageString { get; set; }

        public int TemperatureState { get; set; }
    }
}
