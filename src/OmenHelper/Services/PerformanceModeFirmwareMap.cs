using System;
using Hp.Bridge.Client.SDKs.PerformanceControl.Enums;

namespace OmenHelper.Services;

internal static class PerformanceModeFirmwareMap
{
    internal static string FormatDisplayName(PerformanceMode mode)
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

    internal static bool TryParseDisplayName(string value, out PerformanceMode mode)
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

        return Enum.TryParse(value, out mode);
    }

    internal static bool IsUnleashedMode(PerformanceMode mode)
    {
        return mode == PerformanceMode.Extreme;
    }

    internal static byte GetType26Value(PerformanceMode mode)
    {
        switch (mode)
        {
            case PerformanceMode.Eco:
                return 48;
            case PerformanceMode.Default:
                return 48;
            case PerformanceMode.Performance:
                return 49;
            case PerformanceMode.Extreme:
                return 4;
            default:
                return 0;
        }
    }

    internal static byte[] GetType34Payload(PerformanceMode mode)
    {
        bool isPerfLike = mode == PerformanceMode.Performance || IsUnleashedMode(mode);
        byte tgpEnable = isPerfLike ? (byte)1 : (byte)0;
        byte ppabEnable = mode == PerformanceMode.Eco ? (byte)0 : (byte)1;
        return new byte[4] { tgpEnable, ppabEnable, 1, 87 };
    }

    internal static int GetFanMinimumRpm(PerformanceMode mode)
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
}
