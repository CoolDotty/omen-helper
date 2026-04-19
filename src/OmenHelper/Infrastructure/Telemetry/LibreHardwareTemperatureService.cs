using System;
using System.Collections.Generic;
using LibreHardwareMonitor.Hardware;
using OmenHelper.Infrastructure.Bios;

namespace OmenHelper.Infrastructure.Telemetry;

internal sealed class LibreHardwareTemperatureService : IDisposable
{
    private readonly object _sync = new object();
    private readonly Computer _computer;
    private bool _opened;

    public sealed class TemperatureTelemetrySnapshot
    {
        public bool IsAvailable { get; set; }
        public string Source { get; set; }
        public double? CpuTemperatureC { get; set; }
        public double? GpuTemperatureC { get; set; }
        public double? ChassisTemperatureC { get; set; }
        public IReadOnlyList<string> Lines { get; set; }
        public string Error { get; set; }
    }

    public LibreHardwareTemperatureService()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMotherboardEnabled = true,
            IsControllerEnabled = false,
            IsMemoryEnabled = false,
            IsStorageEnabled = false,
            IsNetworkEnabled = false
        };
    }

    public TemperatureTelemetrySnapshot GetTemperatureTelemetrySnapshot(OmenBiosClient biosClient)
    {
        if (biosClient == null)
        {
            throw new ArgumentNullException(nameof(biosClient));
        }

        lock (_sync)
        {
            TemperatureTelemetrySnapshot snapshot = new TemperatureTelemetrySnapshot
            {
                Source = "LibreHardwareMonitor + BIOS/WMI",
                Lines = Array.Empty<string>()
            };

            try
            {
                EnsureOpened();
                _computer.Accept(new LibreHardwareMonitorUtilities.UpdateVisitor());

                List<LibreHardwareMonitorUtilities.TemperatureReading> readings = LibreHardwareMonitorUtilities.CollectTemperatureReadings(_computer);
                snapshot.CpuTemperatureC = LibreHardwareMonitorUtilities.SelectCpuTemperature(readings);
                snapshot.GpuTemperatureC = LibreHardwareMonitorUtilities.SelectGpuTemperature(readings);

                if (biosClient.TryGetTemperature(out double chassisTemperatureC))
                {
                    snapshot.ChassisTemperatureC = chassisTemperatureC;
                }

                if (!snapshot.CpuTemperatureC.HasValue)
                {
                    snapshot.CpuTemperatureC = LibreHardwareMonitorUtilities.SelectAnyTemperature(readings, LibreHardwareMonitorUtilities.CpuPreferredHints);
                }

                if (!snapshot.GpuTemperatureC.HasValue)
                {
                    snapshot.GpuTemperatureC = LibreHardwareMonitorUtilities.SelectAnyTemperature(readings, LibreHardwareMonitorUtilities.GpuPreferredHints);
                }

                snapshot.IsAvailable = readings.Count > 0 || snapshot.CpuTemperatureC.HasValue || snapshot.GpuTemperatureC.HasValue || snapshot.ChassisTemperatureC.HasValue;
                snapshot.Lines = LibreHardwareMonitorUtilities.BuildTemperatureLines(snapshot.Source, snapshot.CpuTemperatureC, snapshot.GpuTemperatureC, snapshot.ChassisTemperatureC, readings);
                return snapshot;
            }
            catch (Exception ex)
            {
                snapshot.Error = ex.Message;
                snapshot.Lines = new[] { "[Temps] refresh failed: " + ex.Message };
                return snapshot;
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            try
            {
                if (_opened)
                {
                    _computer.Close();
                }
            }
            catch
            {
            }
            finally
            {
                _opened = false;
            }
        }
    }

    private void EnsureOpened()
    {
        if (_opened)
        {
            return;
        }

        _computer.Open();
        _opened = true;
    }
}
