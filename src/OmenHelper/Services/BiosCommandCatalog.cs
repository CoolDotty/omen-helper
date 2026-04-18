using System;

namespace OmenHelper.Services;

internal static class BiosCommandCatalog
{
    internal const int PerformancePlatformCommand = 131080;
    internal const int GraphicsModeCommand = 82;

    internal const int PerformanceModeType = 26;
    internal const int PerformanceGpuPowerType = 34;
    internal const int TemperatureType = 35;
    internal const int MaxFanReadType = 38;
    internal const int MaxFanWriteType = 39;
    internal const int SystemDesignDataType = 40;
    internal const int PerformanceTpptdpType = 41;
    internal const int PerformanceTpptdpPayload = 45;
    internal const int PerformanceStatusReadType = 45;
    internal const int PerformanceStatusWriteType = 46;
    internal const int GraphicsModeReadCommand = 1;
    internal const int GraphicsModeWriteCommand = 2;

    internal static readonly byte[] SharedSign = { 0x53, 0x45, 0x43, 0x55 };

    internal static byte[] BuildGraphicsModePayload(HP.Omen.Core.Model.DataStructure.Modules.GraphicsSwitcher.Enums.GraphicsSwitcherMode mode)
    {
        return new byte[4] { (byte)mode, 0, 0, 0 };
    }
}
