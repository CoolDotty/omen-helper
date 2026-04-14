using System;
using System.Management;
using System.Threading.Tasks;
using HP.Omen.Core.Model.DataStructure.Modules.GraphicsSwitcher.Enums;
using OmenHelper.Models;

namespace OmenHelper.Services;

internal sealed class OmenBiosClient : IDisposable
{
    private const string BiosDataClassName = "hpqBDataIn";
    private const string BiosMethodsClassName = "hpqBIntM";
    private const string BiosMethodsInstanceName = @"ACPI\PNP0C14\0_0";
    private static readonly byte[] SharedSign = { 0x53, 0x45, 0x43, 0x55 };

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
                    input["Sign"] = SharedSign;
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
            BiosWmiResult result = Execute(131080, 38, new byte[4], 4);
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
            BiosWmiResult result = Execute(131080, 39, new[] { mode }, 4);
            return result.ExecuteResult && result.ReturnCode == 0;
        });
    }

    public Task<GraphicsSwitcherMode> GetGraphicsModeAsync()
    {
        return Task.Run(() =>
        {
            BiosWmiResult result = Execute(1, 82, null, 4);
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
            BiosWmiResult result = Execute(2, 82, new byte[4] { (byte)mode, 0, 0, 0 }, 4);
            return result.ExecuteResult ? result.ReturnCode : -1;
        });
    }

    public Task<byte[]> GetSystemDesignDataAsync()
    {
        return Task.Run(() =>
        {
            BiosWmiResult result = Execute(131080, 40, null, 128);
            return result.ExecuteResult ? result.ReturnData : Array.Empty<byte>();
        });
    }

    public Task<SystemDesignDataInfo> GetSystemDesignDataInfoAsync()
    {
        return Task.Run(() =>
        {
            BiosWmiResult result = Execute(131080, 40, null, 128);
            if (!result.ExecuteResult || result.ReturnData.Length < 9)
            {
                return SystemDesignDataInfo.Empty;
            }

            byte rawGpuModeSwitch = result.ReturnData[7];
            return SystemDesignDataInfo.FromRaw(rawGpuModeSwitch, true);
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
