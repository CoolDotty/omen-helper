using System;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using Hp.Bridge.Client.SDKs.PerformanceControl.Enums;
using OmenHelper.Domain.Fan;
using OmenHelper.Domain.Firmware;

namespace OmenHelper.Infrastructure.Persistence;

internal sealed class LocalStateStore
{
    private readonly string _baseDirectory;
    private readonly string _performanceModeStatePath;
    private readonly string _powerModePreferencePath;
    private readonly string _fanMinimumPreferencePath;
    private readonly string _fanCurveStatePath;

    public LocalStateStore()
    {
        _baseDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OmenHelper");
        _performanceModeStatePath = Path.Combine(_baseDirectory, "performance-mode.txt");
        _powerModePreferencePath = Path.Combine(_baseDirectory, "power-mode-preferences.txt");
        _fanMinimumPreferencePath = Path.Combine(_baseDirectory, "fan-minimum-preference.txt");
        _fanCurveStatePath = Path.Combine(_baseDirectory, "fan-curves.json");
    }

    public bool TryLoadRememberedPerformanceMode(out PerformanceMode mode)
    {
        mode = default(PerformanceMode);

        if (!File.Exists(_performanceModeStatePath))
        {
            return false;
        }

        string persisted = File.ReadAllText(_performanceModeStatePath).Trim();
        if (!PerformanceModeFirmwareMap.TryParseDisplayName(persisted, out mode))
        {
            return false;
        }

        return true;
    }

    public void SaveRememberedPerformanceMode(PerformanceMode mode)
    {
        EnsureBaseDirectory();
        File.WriteAllText(_performanceModeStatePath, mode.ToString());
    }

    public bool TryLoadPowerModePreferences(out PerformanceMode? batteryMode, out PerformanceMode? pluggedInMode)
    {
        batteryMode = null;
        pluggedInMode = null;

        if (!File.Exists(_powerModePreferencePath))
        {
            return false;
        }

        string[] lines = File.ReadAllLines(_powerModePreferencePath);
        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            string[] parts = line.Split(new[] { '=' }, 2);
            if (parts.Length != 2)
            {
                continue;
            }

            string key = parts[0].Trim();
            string value = parts[1].Trim();
            if (!TryParsePreferenceMode(value, out PerformanceMode? parsed))
            {
                continue;
            }

            if (string.Equals(key, "battery", StringComparison.OrdinalIgnoreCase))
            {
                batteryMode = parsed;
            }
            else if (string.Equals(key, "pluggedin", StringComparison.OrdinalIgnoreCase) || string.Equals(key, "ac", StringComparison.OrdinalIgnoreCase))
            {
                pluggedInMode = parsed;
            }
        }

        return true;
    }

    public void SavePowerModePreferences(PerformanceMode? batteryMode, PerformanceMode? pluggedInMode)
    {
        EnsureBaseDirectory();
        File.WriteAllLines(_powerModePreferencePath, new[]
        {
            "battery=" + FormatPowerModePreference(batteryMode),
            "pluggedin=" + FormatPowerModePreference(pluggedInMode)
        });
    }

    public bool TryLoadFanMinimumPreference(out int? rpm)
    {
        rpm = null;

        if (!File.Exists(_fanMinimumPreferencePath))
        {
            return false;
        }

        string persisted = File.ReadAllText(_fanMinimumPreferencePath).Trim();
        if (!TryParseFanMinimumPreference(persisted, out rpm))
        {
            return false;
        }

        return true;
    }

    public void SaveFanMinimumPreference(int? rpm)
    {
        EnsureBaseDirectory();
        File.WriteAllText(_fanMinimumPreferencePath, FormatFanMinimumPreference(rpm));
    }

    public bool TryLoadFanCurveStore(out FanCurveStore store)
    {
        store = null;
        if (!File.Exists(_fanCurveStatePath))
        {
            return false;
        }

        using (FileStream stream = File.OpenRead(_fanCurveStatePath))
        {
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(FanCurveStoreDto));
            FanCurveStoreDto dto = serializer.ReadObject(stream) as FanCurveStoreDto;
            if (dto == null)
            {
                return false;
            }

            store = dto.ToDomain();
            return true;
        }
    }

    public void SaveFanCurveStore(FanCurveStore store)
    {
        if (store == null)
        {
            throw new ArgumentNullException(nameof(store));
        }

        EnsureBaseDirectory();
        using (FileStream stream = File.Create(_fanCurveStatePath))
        {
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(FanCurveStoreDto));
            serializer.WriteObject(stream, FanCurveStoreDto.FromDomain(store));
        }
    }

    private void EnsureBaseDirectory()
    {
        Directory.CreateDirectory(_baseDirectory);
    }

    private static bool TryParsePreferenceMode(string value, out PerformanceMode? mode)
    {
        if (string.Equals(value, "None", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(value))
        {
            mode = null;
            return true;
        }

        PerformanceMode parsed;
        if (PerformanceModeFirmwareMap.TryParseDisplayName(value, out parsed))
        {
            mode = parsed;
            return true;
        }

        mode = null;
        return false;
    }

    private static string FormatPowerModePreference(PerformanceMode? mode)
    {
        return mode.HasValue ? PerformanceModeFirmwareMap.FormatDisplayName(mode.Value) : "None";
    }

    private static bool TryParseFanMinimumPreference(string value, out int? rpm)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "None", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "Mode default", StringComparison.OrdinalIgnoreCase))
        {
            rpm = null;
            return true;
        }

        int parsed;
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) && IsAllowedFanMinimumRpm(parsed))
        {
            rpm = parsed;
            return true;
        }

        rpm = null;
        return false;
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

    private static string FormatFanMinimumPreference(int? rpm)
    {
        return rpm.HasValue ? rpm.Value.ToString(CultureInfo.InvariantCulture) : "None";
    }

    [DataContract]
    private sealed class FanCurveStoreDto
    {
        [DataMember(Order = 1)]
        public bool Enabled { get; set; }

        [DataMember(Order = 2)]
        public FanCurveSetDto Eco { get; set; }

        [DataMember(Order = 3)]
        public FanCurveSetDto Balanced { get; set; }

        [DataMember(Order = 4)]
        public FanCurveSetDto Performance { get; set; }

        [DataMember(Order = 5)]
        public FanCurveSetDto Unleashed { get; set; }

        [DataMember(Order = 6)]
        public int HysteresisRiseDeltaC { get; set; } = 5;

        [DataMember(Order = 7)]
        public int HysteresisDropDeltaC { get; set; } = 10;

        public FanCurveStore ToDomain()
        {
            return new FanCurveStore(
                Enabled,
                (Eco ?? FanCurveSetDto.FromDomain(FanCurveDefaults.CreateDefaultSet(PerformanceMode.Eco))).ToDomain(),
                (Balanced ?? FanCurveSetDto.FromDomain(FanCurveDefaults.CreateDefaultSet(PerformanceMode.Default))).ToDomain(),
                (Performance ?? FanCurveSetDto.FromDomain(FanCurveDefaults.CreateDefaultSet(PerformanceMode.Performance))).ToDomain(),
                (Unleashed ?? FanCurveSetDto.FromDomain(FanCurveDefaults.CreateDefaultSet(PerformanceMode.Extreme))).ToDomain(),
                HysteresisRiseDeltaC,
                HysteresisDropDeltaC);
        }

        public static FanCurveStoreDto FromDomain(FanCurveStore store)
        {
            return new FanCurveStoreDto
            {
                Enabled = store.Enabled,
                Eco = FanCurveSetDto.FromDomain(store.Eco),
                Balanced = FanCurveSetDto.FromDomain(store.Balanced),
                Performance = FanCurveSetDto.FromDomain(store.Performance),
                Unleashed = FanCurveSetDto.FromDomain(store.Unleashed),
                HysteresisRiseDeltaC = store.HysteresisRiseDeltaC,
                HysteresisDropDeltaC = store.HysteresisDropDeltaC
            };
        }
    }

    [DataContract]
    private sealed class FanCurveSetDto
    {
        [DataMember(Order = 1)]
        public int[] Cpu { get; set; }

        [DataMember(Order = 2)]
        public int[] Gpu { get; set; }

        [DataMember(Order = 3)]
        public int[] Chassis { get; set; }

        [DataMember(Order = 4)]
        public bool GpuLinked { get; set; }

        public FanCurveSet ToDomain()
        {
            return new FanCurveSet(
                new FanCurveProfile(FanCurveProfile.CpuGpuTemperaturePoints, Cpu ?? FanCurveDefaults.BuildCpuDefault(PerformanceMode.Default).RpmPoints),
                new FanCurveProfile(FanCurveProfile.CpuGpuTemperaturePoints, Gpu ?? FanCurveDefaults.BuildLinkedGpuProfile(FanCurveDefaults.BuildCpuDefault(PerformanceMode.Default)).RpmPoints),
                new FanCurveProfile(FanCurveProfile.ChassisTemperaturePoints, Chassis ?? FanCurveDefaults.BuildChassisDefault().RpmPoints),
                GpuLinked);
        }

        public static FanCurveSetDto FromDomain(FanCurveSet set)
        {
            return new FanCurveSetDto
            {
                Cpu = ToArray(set.Cpu),
                Gpu = ToArray(set.Gpu),
                Chassis = ToArray(set.Chassis),
                GpuLinked = set.GpuLinked
            };
        }

        private static int[] ToArray(FanCurveProfile profile)
        {
            int[] result = new int[profile.RpmPoints.Count];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = profile.RpmPoints[i];
            }

            return result;
        }
    }
}
