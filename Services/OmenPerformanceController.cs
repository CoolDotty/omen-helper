using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
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
    private const int InitializationIntervalMs = 3000;

    private readonly int _sessionId;
    private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
    private readonly OmenHsaClient _omenHsaClient = new OmenHsaClient();
    private readonly object _diagnosticsSync = new object();

    private PipeClientV2 _controlPipe;
    private PipeServerV3 _stateServer;
    private PipeServerV2 _performanceMonitorServer;
    private Timer _initializationTimer;

    private bool _started;
    private bool _disposed;
    private bool _initialized;
    private bool _available;
    private PerformanceMode _currentMode = PerformanceMode.Default;
    private ThermalControl _currentThermalMode = ThermalControl.Auto;
    private FanMode _currentLegacyFanMode = FanMode.Normal;
    private GraphicsSwitcherMode _currentGraphicsMode = GraphicsSwitcherMode.Unknown;
    private bool _extremeUnlocked;
    private bool _unleashVisible;
    private ThermalModeOnUI _thermalModeUiType;
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
    private string _lastInitializationJson = string.Empty;
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

        _stateServer = new PipeServerV3(CommonStr.PerformanceControlPluginStr + _sessionId, ReceiveStateMessage);
        _stateServer.StartServer();

        string performanceMonitorServerName = GetPerformanceMonitorServerName();
        _performanceMonitorServer = new PipeServerV2(performanceMonitorServerName, ReceivePerformanceMonitorMessage);
        _performanceMonitorServer.StartServer();

        _controlPipe = new PipeClientV2(CommonStr.PerformanceControlFgStr + _sessionId);

        PerformanceMonitorHelper.RegisterPerformanceMonitoring(register: true, performanceMonitorServerName, PerformanceMonitorRegisterType.CPU_SIMPLE, runByTask: true);
        PerformanceMonitorHelper.RegisterPerformanceMonitoring(register: true, performanceMonitorServerName, PerformanceMonitorRegisterType.GPU_SIMPLE, runByTask: true);

        _initializationTimer = new Timer(async _ => await RequestInitializationAsync().ConfigureAwait(false), null, 300, InitializationIntervalMs);
        _started = true;

        RefreshGraphicsMode();
        RefreshGraphicsSupport();
        RaiseStateChanged();
        Log("Started HP pipe servers. If the OMEN overlay is open, close it so this app can own the plugin return pipe.");
    }

    public Task RequestInitializationAsync()
    {
        return SendControlMessageAsync((int)PerformanceControlCmd.Initialization, string.Empty);
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
            builder.AppendLine("Last Initialization Payload");
            builder.AppendLine(string.IsNullOrWhiteSpace(_lastInitializationJson) ? "<none>" : _lastInitializationJson);
            builder.AppendLine();

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

        _lastPerformanceRequestPath = "Pipe";
        _currentMode = mode;
        await SendControlMessageAsync((int)PerformanceControlCmd.SetModeFromOverlay, mode).ConfigureAwait(false);
    }

    public Task SetThermalModeAsync(ThermalControl thermalControl)
    {
        double packedValue = (int)_currentMode * 1000 + (int)thermalControl;
        return SendControlMessageAsync((int)PerformanceControlCmd.SetThermalModeFromOverlay, packedValue);
    }

    public async Task SetLegacyFanModeAsync(FanMode mode)
    {
        _currentLegacyFanMode = mode;
        await SendControlMessageAsync((int)PerformanceControlCmd.SetLegacyFanModeFromOverlay, mode).ConfigureAwait(false);
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

    private async Task SendControlMessageAsync(int command, object data)
    {
        try
        {
            if (_controlPipe == null)
            {
                return;
            }

            PerseusRevMsg message = new PerseusRevMsg
            {
                SendParameter = new PerformanceControlMsg
                {
                    Command = command,
                    Data = data
                }
            };

            if (!_controlPipe.IsServerExist())
            {
                Log("PerformanceControlFg pipe is not ready yet.");
                return;
            }

            bool success = await _controlPipe.SendMsgAsync(message).ConfigureAwait(false);
            Log(success
                ? $"Sent command {command} to PerformanceControlFg{_sessionId}."
                : $"Failed to send command {command} to PerformanceControlFg{_sessionId}.");
        }
        catch (Exception ex)
        {
            Log("Control send failed: " + ex.Message);
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

    private void ReceiveStateMessage(object payload)
    {
        try
        {
            PerseusRevMsg message = payload as PerseusRevMsg;
            if (message == null)
            {
                return;
            }

            switch (message.FuncType)
            {
                case (int)PerformanceControlCmd.UpdateModeToOverlay:
                    _currentMode = (PerformanceMode)Convert.ToInt32(message.SendParameter.ToString());
                    break;
                case (int)PerformanceControlCmd.UpdateThermalControlToOverlay:
                    _currentThermalMode = (ThermalControl)Convert.ToInt32(message.SendParameter.ToString());
                    break;
                case (int)PerformanceControlCmd.UpdateLegacyFanToOverlay:
                    _currentLegacyFanMode = (FanMode)Convert.ToInt32(message.SendParameter.ToString());
                    break;
                case (int)PerformanceControlCmd.UpdateIsAvailableToOverlay:
                    _available = Convert.ToBoolean(message.SendParameter.ToString());
                    break;
                case (int)PerformanceControlCmd.Initialization:
                    ApplyInitialization(message.SendParameter as string);
                    break;
            }

            RefreshGraphicsMode();
            TrackEvent("State", "FuncType=" + message.FuncType + ", Payload=" + (message.SendParameter ?? "<null>"));
            RaiseStateChanged();
        }
        catch (Exception ex)
        {
            Log("State receive failed: " + ex.Message);
        }
    }

    private void ApplyInitialization(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        InitializationPayload payload = _json.Deserialize<InitializationPayload>(json);
        if (payload == null)
        {
            return;
        }

        _currentMode = (PerformanceMode)payload.CurrentMode;
        _currentThermalMode = (ThermalControl)payload.CurrentThermalMode;
        _currentLegacyFanMode = (FanMode)payload.CurrentLegacyFanMode;
        _extremeUnlocked = payload.IsExtremeModeUnlock;
        _available = payload.IsAvalalbe;
        _thermalModeUiType = (ThermalModeOnUI)payload.ThermalModeUIType;
        _supportModes = new List<string>();

        if (payload.SupportModeList != null)
        {
            foreach (int supportMode in payload.SupportModeList)
            {
                string name = Enum.GetName(typeof(PerformanceModeOnUI), supportMode);
                if (!string.IsNullOrEmpty(name))
                {
                    _supportModes.Add(name);
                }
            }
        }

        _unleashVisible = _supportModes.Contains(nameof(PerformanceModeOnUI.Unleash));
        _initialized = true;
        _initializationTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        RefreshGraphicsMode();
        lock (_diagnosticsSync)
        {
            _lastInitializationJson = json;
        }
        Log("Initialization payload received from HP background.");
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

        _initializationTimer?.Dispose();
        _stateServer?.Dispose();
        _performanceMonitorServer?.Dispose();
    }

    private sealed class InitializationPayload
    {
        public List<int> SupportModeList { get; set; }

        public int CurrentMode { get; set; }

        public int CurrentThermalMode { get; set; }

        public int ThermalModeUIType { get; set; }

        public int CurrentLegacyFanMode { get; set; }

        public bool IsExtremeModeUnlock { get; set; }

        public bool IsAvalalbe { get; set; }
    }

    private sealed class PerformanceMonitorPayload
    {
        public string TemperatureString { get; set; }

        public string UsageString { get; set; }

        public int TemperatureState { get; set; }
    }
}
