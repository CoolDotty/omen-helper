using System;
using System.Collections.Generic;
using OmenHelper.Domain.Firmware;
using OmenHelper.Infrastructure.Bios;

namespace OmenHelper.Infrastructure.Telemetry;

internal sealed class BiosFanTelemetryService : IDisposable
{
    private readonly OmenBiosClient _biosClient;
    private readonly object _sync = new object();

    public sealed class FanTelemetrySnapshot
    {
        public bool IsAvailable { get; set; }
        public string Source { get; set; }
        public int? CpuFanRpm { get; set; }
        public int? GpuFanRpm { get; set; }
        public IReadOnlyList<string> Lines { get; set; }
        public string Error { get; set; }
    }

    public BiosFanTelemetryService(OmenBiosClient biosClient)
    {
        _biosClient = biosClient ?? throw new ArgumentNullException(nameof(biosClient));
    }

    public FanTelemetrySnapshot GetFanTelemetrySnapshot()
    {
        lock (_sync)
        {
            FanTelemetrySnapshot snapshot = new FanTelemetrySnapshot
            {
                Source = "BIOS/WMI (command 131080/45, x100)",
                Lines = Array.Empty<string>()
            };

            try
            {
                OmenBiosClient.BiosWmiResult result = _biosClient.Execute(
                    BiosCommandCatalog.PerformancePlatformCommand,
                    BiosCommandCatalog.PerformanceStatusReadType,
                    new byte[4],
                    128);

                if (!result.ExecuteResult)
                {
                    snapshot.Error = string.IsNullOrWhiteSpace(_biosClient.LastError)
                        ? "BIOS fan RPM read failed."
                        : _biosClient.LastError;
                    snapshot.Lines = new[] { "[BIOS/WMI] fan RPM read failed: " + snapshot.Error };
                    return snapshot;
                }

                if (result.ReturnCode != 0)
                {
                    snapshot.Error = "BIOS returned code " + result.ReturnCode + ".";
                    snapshot.Lines = new[] { "[BIOS/WMI] fan RPM read failed: " + snapshot.Error };
                    return snapshot;
                }

                if (result.ReturnData == null || result.ReturnData.Length < 2)
                {
                    snapshot.Error = "BIOS did not return both fan RPM bytes.";
                    snapshot.Lines = new[] { "[BIOS/WMI] fan RPM read failed: " + snapshot.Error };
                    return snapshot;
                }

                snapshot.CpuFanRpm = result.ReturnData[0] * 100;
                snapshot.GpuFanRpm = result.ReturnData[1] * 100;
                snapshot.IsAvailable = true;
                snapshot.Lines = new[]
                {
                    "[BIOS/WMI] source: " + snapshot.Source,
                    "[BIOS/WMI] CPU fan RPM: " + snapshot.CpuFanRpm.Value.ToString("N0"),
                    "[BIOS/WMI] GPU fan RPM: " + snapshot.GpuFanRpm.Value.ToString("N0")
                };
                return snapshot;
            }
            catch (Exception ex)
            {
                snapshot.Error = ex.Message;
                snapshot.Lines = new[] { "[BIOS/WMI] fan RPM read failed: " + ex.Message };
                return snapshot;
            }
        }
    }

    public IReadOnlyList<string> GetFanSensorLines()
    {
        return GetFanTelemetrySnapshot().Lines;
    }

    public void Dispose()
    {
    }
}
