using System;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using HP.Omen.Core.Model.DataStructure.Modules.GraphicsSwitcher.Enums;
using OmenHelper.Domain.Firmware;
using OmenHelper.Domain.Graphics;

namespace OmenHelper.Infrastructure.Bios;

internal sealed class OmenBiosClient : IDisposable
{
    private const string BiosDataClassName = "hpqBDataIn";
    private const string BiosMethodsClassName = "hpqBIntM";
    private const string BiosMethodsInstanceName = @"ACPI\PNP0C14\0_0";
    private readonly object _sync = new object();
    private ManagementScope _scope;
    private ManagementClass _biosDataClass;
    private ManagementObject _biosMethodsObject;
    private bool _initialized;

    public bool IsInitialized
    {
        get
        {
            lock (_sync)
            {
                return _initialized;
            }
        }
    }

    public string LastError { get; private set; } = string.Empty;

    public void Initialize()
    {
        lock (_sync)
        {
            if (_initialized)
            {
                return;
            }

            _scope = new ManagementScope(@"\\.\root\wmi", new ConnectionOptions
            {
                EnablePrivileges = true,
                Impersonation = ImpersonationLevel.Impersonate
            });
            _scope.Connect();

            _biosDataClass = new ManagementClass(_scope, new ManagementPath(BiosDataClassName), null);

            using (ManagementObjectCollection instances = new ManagementClass(_scope, new ManagementPath(BiosMethodsClassName), null).GetInstances())
            {
                foreach (ManagementObject instance in instances)
                {
                    string instanceName = Convert.ToString(instance["InstanceName"]);
                    if (string.Equals(instanceName, BiosMethodsInstanceName, StringComparison.OrdinalIgnoreCase))
                    {
                        _biosMethodsObject = instance;
                        break;
                    }

                    instance.Dispose();
                }
            }

            if (_biosMethodsObject == null)
            {
                throw new InvalidOperationException("Failed to locate hpqBIntM instance.");
            }

            _initialized = true;
            LastError = string.Empty;
        }
    }

    public BiosWmiResult Execute(int command, int commandType, byte[] inputData, int returnDataSize)
    {
        lock (_sync)
        {
            EnsureInitialized();

            try
            {
                using (ManagementObject input = _biosDataClass.CreateInstance())
                {
                    input["Sign"] = BiosCommandCatalog.SharedSign;
                    input["Command"] = (uint)command;
                    input["CommandType"] = (uint)commandType;
                    input["Size"] = (uint)(inputData != null ? inputData.Length : 0);
                    input["hpqBData"] = inputData ?? Array.Empty<byte>();

                    string methodName = GetMethodName(returnDataSize);
                    ManagementBaseObject inParameters = _biosMethodsObject.GetMethodParameters(methodName);
                    inParameters["InData"] = input;
                    ManagementBaseObject outParameters = _biosMethodsObject.InvokeMethod(methodName, inParameters, null);
                    ManagementBaseObject outData = outParameters?["OutData"] as ManagementBaseObject;

                    if (outData == null)
                    {
                        LastError = "Missing OutData.";
                        return BiosWmiResult.Failure(returnDataSize);
                    }

                    int returnCode = Convert.ToInt32(outData["rwReturnCode"]);
                    byte[] returnData = CopyReturnData(outData["Data"] as byte[], returnDataSize);

                    LastError = string.Empty;
                    return new BiosWmiResult(true, returnCode, returnData);
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return BiosWmiResult.Failure(returnDataSize);
            }
        }
    }

    public Task<BiosWmiResult> ExecuteAsync(int command, int commandType, byte[] inputData, int returnDataSize)
    {
        return Task.Run(() => Execute(command, commandType, inputData, returnDataSize));
    }

    public Task<bool> GetMaxFanEnabledAsync()
    {
        return Task.Run(() =>
        {
            BiosWmiResult result = Execute(BiosCommandCatalog.PerformancePlatformCommand, BiosCommandCatalog.MaxFanReadType, new byte[4], 4);
            if (!result.ExecuteResult || result.ReturnData.Length == 0)
            {
                throw new InvalidOperationException(LastError.Length > 0 ? LastError : "MaxFan read failed.");
            }

            return result.ReturnData[0] != 0;
        });
    }

    public Task<bool> SetMaxFanAsync(bool enabled)
    {
        return Task.Run(() =>
        {
            byte mode = enabled ? (byte)1 : (byte)0;
            BiosWmiResult result = Execute(BiosCommandCatalog.PerformancePlatformCommand, BiosCommandCatalog.MaxFanWriteType, new[] { mode }, 4);
            return result.ExecuteResult && result.ReturnCode == 0;
        });
    }

    public Task<BiosWmiResult> SetPerformanceStatusBlobAsync(byte[] blob)
    {
        return Task.Run(() => Execute(BiosCommandCatalog.PerformancePlatformCommand, BiosCommandCatalog.PerformanceStatusWriteType, NormalizeBlob(blob), 4));
    }

    public Task<BiosWmiResult> SetFanTargetsAsync(int cpuRpm, int gpuRpm)
    {
        return Task.Run(() =>
        {
            byte[] blob = new byte[128];
            blob[0] = (byte)Math.Max(0, Math.Min(65, NormalizeRpm(cpuRpm) / 100));
            blob[1] = (byte)Math.Max(0, Math.Min(65, NormalizeRpm(gpuRpm) / 100));
            return Execute(BiosCommandCatalog.PerformancePlatformCommand, BiosCommandCatalog.PerformanceStatusWriteType, blob, 4);
        });
    }

    public Task<GraphicsSwitcherMode> GetGraphicsModeAsync()
    {
        return Task.Run(() =>
        {
            BiosWmiResult result = Execute(BiosCommandCatalog.GraphicsModeReadCommand, BiosCommandCatalog.GraphicsModeCommand, null, 4);
            if (!result.ExecuteResult || result.ReturnData.Length == 0)
            {
                return GraphicsSwitcherMode.Unknown;
            }

            return (GraphicsSwitcherMode)result.ReturnData[0];
        });
    }

    public Task<int> SetGraphicsModeAsync(GraphicsSwitcherMode mode)
    {
        return Task.Run(() =>
        {
            BiosWmiResult result = Execute(BiosCommandCatalog.GraphicsModeWriteCommand, BiosCommandCatalog.GraphicsModeCommand, BiosCommandCatalog.BuildGraphicsModePayload(mode), 4);
            return result.ExecuteResult ? result.ReturnCode : -1;
        });
    }

    public Task<BiosWmiResult> GetPerformanceStatusBlobAsync()
    {
        return Task.Run(() => Execute(BiosCommandCatalog.PerformancePlatformCommand, BiosCommandCatalog.PerformanceStatusReadType, new byte[4], 128));
    }

    // Read small temperature-ish value (observed HP method dtGetTemperature uses commandType=35).
    // The input byte[1] selects the observed chassis sensor; returns the first byte as reported.
    public bool TryGetTemperature(out double temperature)
    {
        temperature = double.NaN;

        // Observed input used by HP: input[1] = 1.
        byte[] input = new byte[4] { 0, 1, 0, 0 };
        BiosWmiResult result = Execute(BiosCommandCatalog.PerformancePlatformCommand, BiosCommandCatalog.TemperatureType, input, 4);
        if (!result.ExecuteResult || result.ReturnData == null || result.ReturnData.Length == 0)
        {
            LastError = string.IsNullOrEmpty(LastError) ? "Temperature read failed." : LastError;
            return false;
        }

        temperature = result.ReturnData[0];
        return true;
    }

    public Task<int> GetTemperatureAsync()
    {
        return Task.Run(() =>
        {
            return TryGetTemperature(out double temperature) ? (int)temperature : -1;
        });
    }

    public Task<BiosWmiResult> ProbeTemperatureCommandAsync(int commandType, byte[] inputData, int returnDataSize = 4)
    {
        return Task.Run(() => Execute(BiosCommandCatalog.PerformancePlatformCommand, commandType, inputData, returnDataSize));
    }

    public Task<byte[]> GetSystemDesignDataAsync()
    {
        return Task.Run(() =>
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                BiosWmiResult result = Execute(BiosCommandCatalog.PerformancePlatformCommand, BiosCommandCatalog.SystemDesignDataType, null, 128);
                if (result.ExecuteResult && result.ReturnCode == 0)
                {
                    return result.ReturnData;
                }

                Thread.Sleep(100);
            }

            return Array.Empty<byte>();
        });
    }

    public Task<SystemDesignDataInfo> GetSystemDesignDataInfoAsync()
    {
        return Task.Run(() =>
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                BiosWmiResult result = Execute(BiosCommandCatalog.PerformancePlatformCommand, BiosCommandCatalog.SystemDesignDataType, null, 128);
                if (result.ExecuteResult && result.ReturnCode == 0 && result.ReturnData.Length >= 9)
                {
                    byte rawGpuModeSwitch = result.ReturnData[7];
                    return SystemDesignDataInfo.FromRaw(rawGpuModeSwitch, true);
                }

                Thread.Sleep(100);
            }

            return SystemDesignDataInfo.Empty;
        });
    }

    public void Close()
    {
        lock (_sync)
        {
            _initialized = false;

            try
            {
                _biosMethodsObject?.Dispose();
            }
            catch
            {
            }

            try
            {
                _biosDataClass?.Dispose();
            }
            catch
            {
            }

            _biosMethodsObject = null;
            _biosDataClass = null;
            _scope = null;
        }
    }

    public void Dispose()
    {
        Close();
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
        {
            Initialize();
        }

        if (!_initialized)
        {
            throw new InvalidOperationException("BIOS WMI client is not initialized.");
        }
    }

    private static string GetMethodName(int returnDataSize)
    {
        if (returnDataSize <= 0)
        {
            return "hpqBIOSInt0";
        }

        if (returnDataSize <= 4)
        {
            return "hpqBIOSInt4";
        }

        if (returnDataSize <= 128)
        {
            return "hpqBIOSInt128";
        }

        if (returnDataSize <= 1024)
        {
            return "hpqBIOSInt1024";
        }

        return "hpqBIOSInt4096";
    }

    private static byte[] CopyReturnData(byte[] source, int returnDataSize)
    {
        if (returnDataSize <= 0)
        {
            return Array.Empty<byte>();
        }

        byte[] result = new byte[returnDataSize];
        if (source == null || source.Length == 0)
        {
            return result;
        }

        Array.Copy(source, result, Math.Min(source.Length, result.Length));
        return result;
    }

    private static byte[] NormalizeBlob(byte[] blob)
    {
        byte[] result = new byte[128];
        if (blob == null || blob.Length == 0)
        {
            return result;
        }

        Array.Copy(blob, result, Math.Min(blob.Length, result.Length));
        return result;
    }

    private static int NormalizeRpm(int rpm)
    {
        int clamped = Math.Max(0, Math.Min(6500, rpm));
        int quantized = (int)(Math.Round(clamped / 100.0) * 100.0);
        if (quantized > 0 && quantized < 1300)
        {
            return 0;
        }

        return quantized;
    }

    internal readonly struct BiosWmiResult
    {
        public BiosWmiResult(bool executeResult, int returnCode, byte[] returnData)
        {
            ExecuteResult = executeResult;
            ReturnCode = returnCode;
            ReturnData = returnData ?? Array.Empty<byte>();
        }

        public bool ExecuteResult { get; }

        public int ReturnCode { get; }

        public byte[] ReturnData { get; }

        public static BiosWmiResult Failure(int returnDataSize)
        {
            return new BiosWmiResult(false, -1, returnDataSize > 0 ? new byte[returnDataSize] : Array.Empty<byte>());
        }
    }
}
