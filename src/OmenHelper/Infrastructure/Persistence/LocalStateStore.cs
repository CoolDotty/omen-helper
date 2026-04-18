using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Hp.Bridge.Client.SDKs.PerformanceControl.Enums;
using OmenHelper.Domain.Firmware;

namespace OmenHelper.Infrastructure.Persistence;

internal sealed class LocalStateStore
{
    private readonly string _baseDirectory;
    private readonly string _performanceModeStatePath;
    private readonly string _powerModePreferencePath;
    private readonly string _fanMinimumPreferencePath;

    public LocalStateStore()
    {
        _baseDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OmenHelper");
        _performanceModeStatePath = Path.Combine(_baseDirectory, "performance-mode.txt");
        _powerModePreferencePath = Path.Combine(_baseDirectory, "power-mode-preferences.txt");
        _fanMinimumPreferencePath = Path.Combine(_baseDirectory, "fan-minimum-preference.txt");
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
}
