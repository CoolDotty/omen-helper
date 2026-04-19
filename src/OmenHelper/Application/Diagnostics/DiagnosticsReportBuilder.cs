using System;
using System.Text;

namespace OmenHelper.Application.Diagnostics;

internal sealed class DiagnosticsReportBuilder
{
    public string Build(DiagnosticsReportSnapshot snapshot)
    {
        StringBuilder builder = new StringBuilder();

        builder.AppendLine("Session");
        builder.AppendLine("  SessionId: " + snapshot.SessionId);
        builder.AppendLine("  Initialized: " + snapshot.Initialized);
        builder.AppendLine("  Available: " + snapshot.Available);
        builder.AppendLine();

        builder.AppendLine("Current State");
        builder.AppendLine("  Mode: " + snapshot.CurrentMode);
        builder.AppendLine("  Mode Inferred: " + snapshot.CurrentModeIsInferred);
        builder.AppendLine("  Thermal: " + snapshot.CurrentThermalMode);
        builder.AppendLine("  Legacy Fan: " + snapshot.CurrentLegacyFanMode);
        builder.AppendLine("  Fan RPM Source: " + snapshot.FanRpmSource);
        builder.AppendLine("  Fan RPM Summary: " + snapshot.FanRpmSummary);
        builder.AppendLine("  Fan RPM Read Succeeded: " + snapshot.FanRpmReadSucceeded);
        builder.AppendLine("  CPU Fan RPM: " + snapshot.CpuFanRpm);
        builder.AppendLine("  GPU Fan RPM: " + snapshot.GpuFanRpm);
        builder.AppendLine("  Temperature Source: " + snapshot.TemperatureSource);
        builder.AppendLine("  Temperature Read Succeeded: " + snapshot.TemperatureReadSucceeded);
        builder.AppendLine("  CPU Temperature C: " + snapshot.CpuTemperatureC);
        builder.AppendLine("  GPU Temperature C: " + snapshot.GpuTemperatureC);
        builder.AppendLine("  Chassis Temperature C: " + snapshot.ChassisTemperatureC);
        builder.AppendLine("  Fan Minimum RPM: " + snapshot.FanMinimumRpm + (snapshot.FanMinimumOverrideRpm == "<none>" ? " (mode default)" : " (custom)"));
        builder.AppendLine("  Graphics: " + snapshot.CurrentGraphicsMode);
        if (!string.IsNullOrWhiteSpace(snapshot.LastPerformanceRequestMode))
        {
            builder.AppendLine("  Last Perf Request: " + snapshot.LastPerformanceRequestMode + " (" + snapshot.LastPerformanceRequestPath + ")");
            builder.AppendLine("    BIOS 131080/26 exec: " + snapshot.LastPerfType26ExecuteResult + " rc: " + snapshot.LastPerfType26ReturnCode);
            builder.AppendLine("    BIOS 131080/34 exec: " + snapshot.LastPerfType34ExecuteResult + " rc: " + snapshot.LastPerfType34ReturnCode);
            builder.AppendLine("    BIOS 131080/41 exec: " + snapshot.LastPerfType41ExecuteResult + " rc: " + snapshot.LastPerfType41ReturnCode);
        }
        builder.AppendLine("  Last MaxFan exec: " + snapshot.LastMaxFanExecuteResult + " rc: " + snapshot.LastMaxFanReturnCode);
        builder.AppendLine("  Performance Status Blob exec: " + snapshot.PerformanceStatusBlobExecuteResult + " rc: " + snapshot.PerformanceStatusBlobReturnCode);
        builder.AppendLine("  Performance Status Blob hash: " + snapshot.PerformanceStatusBlobHash);
        builder.AppendLine("  Performance Status Blob first 32 bytes: " + snapshot.PerformanceStatusBlobPreview);
        builder.AppendLine("  Performance Status Blob sensors: " + snapshot.PerformanceStatusBlobSensors);
        builder.AppendLine("  Performance Status Blob changed bytes: " + snapshot.PerformanceStatusBlobChangedBytes);
        builder.AppendLine("  Performance Status Blob previous hash: " + snapshot.PreviousPerformanceStatusBlobHash);
        builder.AppendLine("  Fan Minimum Override RPM: " + snapshot.FanMinimumOverrideRpm);
        builder.AppendLine("  Fan Minimum Effective RPM: " + snapshot.FanMinimumRpm);
        builder.AppendLine("  Fan Minimum Blob (BIOS 131080/46) exec: " + snapshot.FanMinimumBlobExecuteResult + " rc: " + snapshot.FanMinimumBlobReturnCode);
        builder.AppendLine("  Fan Minimum Blob (BIOS 131080/46) hash: " + snapshot.FanMinimumBlobHash);
        builder.AppendLine("  Fan Minimum Blob (BIOS 131080/46) preview: " + snapshot.FanMinimumBlobPreview);
        builder.AppendLine("  Fan Minimum Blob (BIOS 131080/46) changed bytes: " + snapshot.FanMinimumBlobChangedBytes);
        builder.AppendLine("  Fan Minimum Blob (BIOS 131080/46) previous hash: " + snapshot.PreviousFanMinimumBlobHash);
        builder.AppendLine("  Thermal UI Type: " + snapshot.ThermalUiType);
        builder.AppendLine("  Extreme Unlocked: " + snapshot.ExtremeUnlocked);
        builder.AppendLine("  Unleash Visible: " + snapshot.UnleashVisible);
        builder.AppendLine("  Support Modes: " + snapshot.SupportModes);
        builder.AppendLine();

        builder.AppendLine("BIOS / Platform");
        builder.AppendLine("  SystemDesignData: " + snapshot.SystemDesignData);
        builder.AppendLine("  ShippingAdapterPowerRating: " + snapshot.ShippingAdapterPowerRating);
        builder.AppendLine("  IsBiosPerformanceModeSupport: " + snapshot.IsBiosPerformanceModeSupport);
        builder.AppendLine("  IsSwFanControlSupport: " + snapshot.IsSwFanControlSupport);
        builder.AppendLine("  IsExtremeModeSupport: " + snapshot.IsExtremeModeSupport);
        builder.AppendLine("  IsExtremeModeUnlock: " + snapshot.IsExtremeModeUnlock);
        builder.AppendLine("  GraphicsModeSwitchBits: " + snapshot.GraphicsModeSwitchBits);
        builder.AppendLine("  GraphicsModeSwitchReadSucceeded: " + snapshot.GraphicsModeSwitchReadSucceeded);
        builder.AppendLine("  GraphicsModeSwitchSupported: " + snapshot.GraphicsModeSwitchSupported);
        builder.AppendLine("  GraphicsModeSwitchRawSlots: " + snapshot.GraphicsModeSwitchRawSlots);
        builder.AppendLine("  GraphicsModeSwitchSlots: " + snapshot.GraphicsModeSwitchSlots);
        builder.AppendLine("  GraphicsModeSwitchHasIntegratedSlot: " + snapshot.GraphicsModeSwitchHasIntegratedSlot);
        builder.AppendLine("  GraphicsModeSwitchHasHybridSlot: " + snapshot.GraphicsModeSwitchHasHybridSlot);
        builder.AppendLine("  GraphicsModeSwitchHasDedicatedSlot: " + snapshot.GraphicsModeSwitchHasDedicatedSlot);
        builder.AppendLine("  GraphicsModeSwitchHasOptimusSlot: " + snapshot.GraphicsModeSwitchHasOptimusSlot);
        builder.AppendLine("  GraphicsMode: " + snapshot.CurrentGraphicsMode);
        builder.AppendLine("  GraphicsModeSwitchSupported(State): " + snapshot.GraphicsModeSwitchSupported);
        builder.AppendLine("  GraphicsSupportsHybrid: " + snapshot.GraphicsSupportsHybrid);
        builder.AppendLine("  GraphicsSupportsUma: " + snapshot.GraphicsSupportsUma);
        builder.AppendLine("  GraphicsNeedsReboot: " + snapshot.GraphicsNeedsReboot);
        builder.AppendLine("  LastGraphicsRequestMode: " + snapshot.LastGraphicsRequestMode);
        builder.AppendLine("  LastGraphicsRequestReturnCode: " + snapshot.LastGraphicsRequestReturnCode);
        builder.AppendLine("  MaxFan(BIOS): " + snapshot.MaxFanBios);
        builder.AppendLine();

        builder.AppendLine("Fan RPM Readback");
        if (snapshot.FanSensorLines == null || snapshot.FanSensorLines.Count == 0)
        {
            builder.AppendLine("<none>");
        }
        else
        {
            foreach (string line in snapshot.FanSensorLines)
            {
                builder.AppendLine(line);
            }
        }
        builder.AppendLine();

        builder.AppendLine("Recent Events");
        if (snapshot.RecentEvents == null || snapshot.RecentEvents.Count == 0)
        {
            builder.AppendLine("<none>");
        }
        else
        {
            foreach (string entry in snapshot.RecentEvents)
            {
                builder.AppendLine(entry);
            }
        }

        return builder.ToString();
    }
}
