using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Hp.Bridge.Client.SDKs.PerformanceControl.Enums;
using HP.Omen.Core.Common.Enums;
using HP.Omen.Core.Common.PipeUtility;
using HP.Omen.Core.Common.PowerControl.Enum;
using HP.Omen.Core.Common.Utilities;
using HP.Omen.Core.Model.DataStructure.Modules.FanControl.Enums;
using HP.Omen.Core.Model.DataStructure.Structs;
using OmenHelper.Models;

namespace OmenHelper.Services;

internal sealed class OmenPerformanceController : IDisposable
{
    private const int InitializationIntervalMs = 3000;

    private readonly int _sessionId;
    private readonly JavaScriptSerializer _json = new JavaScriptSerializer();

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
    private bool _extremeUnlocked;
    private bool _unleashVisible;
    private ThermalModeOnUI _thermalModeUiType;
    private List<string> _supportModes = new List<string>();
    private PerformanceMonitorSample _cpuSample = new PerformanceMonitorSample();
    private PerformanceMonitorSample _gpuSample = new PerformanceMonitorSample();

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

        Log("Started HP pipe servers. If the OMEN overlay is open, close it so this app can own the plugin return pipe.");
    }

    public Task RequestInitializationAsync()
    {
        return SendControlMessageAsync((int)PerformanceControlCmd.Initialization, string.Empty);
    }

    public async Task SetPerformanceModeAsync(PerformanceMode mode)
    {
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
            }
            else if (message.FuncType == (int)PerformanceMonitorRegisterType.CPU_SIMPLE)
            {
                _cpuSample = mappedSample;
            }

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

    private void Log(string message)
    {
        string formatted = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message;
        LogMessage?.Invoke(this, formatted);
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
