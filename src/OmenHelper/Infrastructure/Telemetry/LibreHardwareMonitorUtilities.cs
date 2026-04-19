using System;
using System.Collections.Generic;
using System.Linq;
using LibreHardwareMonitor.Hardware;

namespace OmenHelper.Infrastructure.Telemetry;

internal static class LibreHardwareMonitorUtilities
{
    internal const string CpuHardwareId = "/intelcpu/0";
    internal const string CpuTemperatureSensorId = "/intelcpu/0/temperature/1";
    internal const string GpuHardwareId = "/gpu-nvidia/0";
    internal const string GpuTemperatureSensorId = "/gpu-nvidia/0/temperature/0";

    internal static readonly string[] CpuPreferredHints = { "Core Average", "package", "tctl", "tdie", "core max", "cpu" };
    internal static readonly string[] GpuPreferredHints = { "GPU Core", "gpu core", "core", "edge", "hot spot", "hotspot", "temperature" };

    internal sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer)
        {
            computer.Traverse(this);
        }

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (IHardware subHardware in hardware.SubHardware)
            {
                subHardware.Accept(this);
            }
        }

        public void VisitSensor(ISensor sensor)
        {
        }

        public void VisitParameter(IParameter parameter)
        {
        }
    }

    internal sealed class TemperatureReading
    {
        public TemperatureReading(string hardwareIdentifier, string sensorIdentifier, string hardwareType, string hardwareName, string sensorName, float valueC)
        {
            HardwareIdentifier = hardwareIdentifier ?? string.Empty;
            SensorIdentifier = sensorIdentifier ?? string.Empty;
            Identifier = HardwareIdentifier + " | " + SensorIdentifier;
            HardwareType = hardwareType ?? string.Empty;
            HardwareName = hardwareName ?? string.Empty;
            SensorName = sensorName ?? string.Empty;
            ValueC = valueC;
        }

        public string HardwareIdentifier { get; }
        public string SensorIdentifier { get; }
        public string Identifier { get; }
        public string HardwareType { get; }
        public string HardwareName { get; }
        public string SensorName { get; }
        public double ValueC { get; }
    }

    internal static List<TemperatureReading> CollectTemperatureReadings(Computer computer)
    {
        if (computer == null)
        {
            throw new ArgumentNullException(nameof(computer));
        }

        List<TemperatureReading> readings = new List<TemperatureReading>();
        foreach (IHardware hardware in computer.Hardware)
        {
            CollectTemperatureReadings(hardware, readings);
        }

        return readings;
    }

    internal static double? SelectCpuTemperature(IReadOnlyCollection<TemperatureReading> readings)
    {
        TemperatureReading exact = readings.FirstOrDefault(reading =>
            string.Equals(reading.SensorIdentifier, CpuTemperatureSensorId, StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(reading.HardwareIdentifier, CpuHardwareId, StringComparison.OrdinalIgnoreCase) &&
             string.Equals(reading.SensorName, "Core Average", StringComparison.OrdinalIgnoreCase)));
        if (exact != null)
        {
            return exact.ValueC;
        }

        return SelectTemperature(
            readings,
            reading => reading.HardwareName.IndexOf("Intel Core Ultra 7 255H", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       reading.HardwareType.IndexOf("cpu", StringComparison.OrdinalIgnoreCase) >= 0,
            CpuPreferredHints);
    }

    internal static double? SelectGpuTemperature(IReadOnlyCollection<TemperatureReading> readings)
    {
        TemperatureReading exact = readings.FirstOrDefault(reading =>
            string.Equals(reading.SensorIdentifier, GpuTemperatureSensorId, StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(reading.HardwareIdentifier, GpuHardwareId, StringComparison.OrdinalIgnoreCase) &&
             string.Equals(reading.SensorName, "GPU Core", StringComparison.OrdinalIgnoreCase)));
        if (exact != null)
        {
            return exact.ValueC;
        }

        return SelectTemperature(
            readings,
            reading => reading.HardwareName.IndexOf("NVIDIA GeForce RTX 5060 Laptop GPU", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       reading.HardwareName.IndexOf("Nvidia GeForce RTX 5060 Laptop GPU", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       reading.HardwareType.IndexOf("gpu", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       reading.HardwareType.IndexOf("radeon", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       reading.HardwareType.IndexOf("nvidia", StringComparison.OrdinalIgnoreCase) >= 0,
            GpuPreferredHints);
    }

    internal static double? SelectAnyTemperature(IReadOnlyCollection<TemperatureReading> readings, IReadOnlyList<string> preferredNameHints)
    {
        return SelectTemperature(readings, _ => true, preferredNameHints);
    }

    internal static IReadOnlyList<string> BuildTemperatureLines(string source, double? cpuTemperatureC, double? gpuTemperatureC, double? chassisTemperatureC, IReadOnlyCollection<TemperatureReading> readings)
    {
        List<string> lines = new List<string>
        {
            "[Temps] source: " + source,
            "[Temps] CPU: " + FormatTemperature(cpuTemperatureC),
            "[Temps] GPU: " + FormatTemperature(gpuTemperatureC),
            "[Temps] Chassis: " + FormatTemperature(chassisTemperatureC),
            "[Temps] sensors found: " + (readings != null ? readings.Count : 0)
        };

        if (readings != null)
        {
            foreach (TemperatureReading reading in readings.Take(8))
            {
                lines.Add("[Temps] " + reading.Identifier + " | " + reading.HardwareName + " | " + reading.SensorName + " = " + reading.ValueC.ToString("0.0") + " °C");
            }
        }

        return lines;
    }

    internal static string FormatTemperature(double? temperatureC)
    {
        return temperatureC.HasValue ? temperatureC.Value.ToString("0.0") + " °C" : "<unavailable>";
    }

    private static void CollectTemperatureReadings(IHardware hardware, ICollection<TemperatureReading> readings)
    {
        string hardwareIdentifier = hardware.Identifier != null ? hardware.Identifier.ToString() : string.Empty;

        foreach (ISensor sensor in hardware.Sensors)
        {
            if (sensor.SensorType == SensorType.Temperature && sensor.Value.HasValue)
            {
                readings.Add(new TemperatureReading(
                    hardwareIdentifier,
                    sensor.Identifier != null ? sensor.Identifier.ToString() : string.Empty,
                    hardware.HardwareType.ToString(),
                    hardware.Name,
                    sensor.Name,
                    sensor.Value.Value));
            }
        }

        foreach (IHardware subHardware in hardware.SubHardware)
        {
            CollectTemperatureReadings(subHardware, readings);
        }
    }

    private static double? SelectTemperature(IReadOnlyCollection<TemperatureReading> readings, Func<TemperatureReading, bool> filter, IReadOnlyList<string> preferredNameHints)
    {
        List<TemperatureReading> candidates = readings.Where(filter).ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        foreach (string hint in preferredNameHints)
        {
            TemperatureReading match = candidates.FirstOrDefault(reading =>
                reading.SensorName.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0 ||
                reading.HardwareName.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0 ||
                reading.Identifier.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0 ||
                reading.SensorIdentifier.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0);
            if (match != null)
            {
                return match.ValueC;
            }
        }

        return candidates.OrderByDescending(reading => reading.ValueC).First().ValueC;
    }
}
