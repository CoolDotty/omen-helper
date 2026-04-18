using System;
using System.Collections.Generic;
using LibreHardwareMonitor.Hardware;

namespace OmenHelper.Services
{
    internal sealed class HardwareMonitorCpuTemperatureReader : IDisposable
    {
        private readonly object _sync = new object();
        private Computer _computer;
        private ISensor _selectedSensor;
        private bool _disposed;

        public bool TryGetTemperature(out double temperature)
        {
            lock (_sync)
            {
                temperature = double.NaN;

                if (_disposed)
                {
                    return false;
                }

                try
                {
                    EnsureInitialized();
                    if (_computer == null)
                    {
                        return false;
                    }

                    UpdateHardwareTree();

                    if (TryReadCpuCoreAverage(out temperature))
                    {
                        return true;
                    }

                    if (!TryReadSelectedSensor(out temperature))
                    {
                        _selectedSensor = FindBestSensor();
                        if (!TryReadSelectedSensor(out temperature))
                        {
                            temperature = double.NaN;
                            return false;
                        }
                    }

                    return true;
                }
                catch
                {
                    temperature = double.NaN;
                    return false;
                }
            }
        }

        public bool TryGetTemperatureSnapshot(out double cpuCoreAverage, out double gpuTemperature, out double chassisTemperature)
        {
            lock (_sync)
            {
                cpuCoreAverage = double.NaN;
                gpuTemperature = double.NaN;
                chassisTemperature = double.NaN;

                if (_disposed)
                {
                    return false;
                }

                try
                {
                    EnsureInitialized();
                    if (_computer == null)
                    {
                        return false;
                    }

                    UpdateHardwareTree();

                    bool hasAny = false;
                    if (TryReadCpuCoreAverage(out cpuCoreAverage)) hasAny = true;
                    if (TryReadGpuTemperature(out gpuTemperature)) hasAny = true;
                    if (TryReadChassisTemperature(out chassisTemperature)) hasAny = true;

                    return hasAny;
                }
                catch
                {
                    cpuCoreAverage = double.NaN;
                    gpuTemperature = double.NaN;
                    chassisTemperature = double.NaN;
                    return false;
                }
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;

                if (_computer != null)
                {
                    try
                    {
                        _computer.Close();
                    }
                    catch
                    {
                    }

                    _computer = null;
                }

                _selectedSensor = null;
            }
        }

        private void EnsureInitialized()
        {
            if (_computer != null)
            {
                return;
            }

            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = false,
                IsMotherboardEnabled = true,
                IsPowerMonitorEnabled = false,
                IsStorageEnabled = false,
                IsNetworkEnabled = false,
                IsControllerEnabled = false,
                IsBatteryEnabled = false,
                IsPsuEnabled = false
            };

            _computer.Open();
            _selectedSensor = FindBestSensor();
        }

        private void UpdateHardwareTree()
        {
            if (_computer == null)
            {
                return;
            }

            foreach (IHardware hardware in _computer.Hardware)
            {
                UpdateHardwareRecursive(hardware);
            }
        }

        private static void UpdateHardwareRecursive(IHardware hardware)
        {
            if (hardware == null)
            {
                return;
            }

            hardware.Update();
            foreach (IHardware subHardware in hardware.SubHardware)
            {
                UpdateHardwareRecursive(subHardware);
            }
        }

        private bool TryReadSelectedSensor(out double temperature)
        {
            temperature = double.NaN;

            if (_selectedSensor == null)
            {
                return false;
            }

            double? value = _selectedSensor.Value;
            if (!value.HasValue)
            {
                return false;
            }

            temperature = value.Value;
            return true;
        }

        private bool TryReadCpuCoreAverage(out double temperature)
        {
            temperature = double.NaN;

            if (_computer == null)
            {
                return false;
            }

            List<ISensor> coreSensors = new List<ISensor>();
            foreach (IHardware hardware in _computer.Hardware)
            {
                CollectCpuCoreSensorsRecursive(hardware, coreSensors);
            }

            if (coreSensors.Count == 0)
            {
                return false;
            }

            double sum = 0;
            int count = 0;
            foreach (ISensor sensor in coreSensors)
            {
                if (sensor == null)
                {
                    continue;
                }

                double? value = sensor.Value;
                if (!value.HasValue)
                {
                    continue;
                }

                sum += value.Value;
                count++;
            }

            if (count == 0)
            {
                return false;
            }

            temperature = sum / count;
            return true;
        }

        private bool TryReadGpuTemperature(out double temperature)
        {
            temperature = double.NaN;
            return TryReadBestTemperature(
                out temperature,
                hardware => IsGpuHardware(hardware),
                sensor => ScoreGpuSensor(sensor));
        }

        private bool TryReadChassisTemperature(out double temperature)
        {
            temperature = double.NaN;
            return TryReadBestTemperature(
                out temperature,
                hardware => !IsCpuHardware(hardware) && !IsGpuHardware(hardware),
                sensor => ScoreChassisSensor(sensor));
        }

        private bool TryReadBestTemperature(out double temperature, Func<IHardware, bool> hardwareFilter, Func<ISensor, int> scoreSelector)
        {
            temperature = double.NaN;

            if (_computer == null)
            {
                return false;
            }

            ISensor bestSensor = null;
            int bestScore = int.MinValue;

            foreach (IHardware hardware in _computer.Hardware)
            {
                FindBestTemperatureSensorRecursive(hardware, hardwareFilter, scoreSelector, ref bestSensor, ref bestScore);
            }

            if (bestSensor == null)
            {
                return false;
            }

            double? value = bestSensor.Value;
            if (!value.HasValue)
            {
                return false;
            }

            temperature = value.Value;
            return true;
        }

        private ISensor FindBestSensor()
        {
            ISensor bestSensor = null;
            int bestScore = int.MinValue;

            if (_computer == null)
            {
                return null;
            }

            foreach (IHardware hardware in _computer.Hardware)
            {
                FindBestSensorRecursive(hardware, ref bestSensor, ref bestScore);
            }

            return bestSensor;
        }

        private static void FindBestSensorRecursive(IHardware hardware, ref ISensor bestSensor, ref int bestScore)
        {
            if (hardware == null)
            {
                return;
            }

            int hardwareBonus = hardware.HardwareType == HardwareType.Cpu ? 10000 : 0;

            foreach (ISensor sensor in hardware.Sensors)
            {
                if (sensor == null || sensor.SensorType != SensorType.Temperature)
                {
                    continue;
                }

                int score = hardwareBonus + ScoreSensor(sensor);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestSensor = sensor;
                }
            }

            foreach (IHardware subHardware in hardware.SubHardware)
            {
                FindBestSensorRecursive(subHardware, ref bestSensor, ref bestScore);
            }
        }

        private static int ScoreSensor(ISensor sensor)
        {
            string name = sensor.Name ?? string.Empty;
            string lower = name.ToLowerInvariant();

            if (lower.Contains("cpu package")) return 1000;
            if (lower.Contains("package")) return 900;
            if (lower.Contains("tctl")) return 850;
            if (lower.Contains("tdie")) return 840;
            if (lower.Contains("core (max)")) return 830;
            if (lower.Contains("core max")) return 820;
            if (lower.Contains("cpu")) return 700;
            if (lower.Contains("core")) return 600;
            return 100;
        }

        private static void CollectCpuCoreSensorsRecursive(IHardware hardware, List<ISensor> sensors)
        {
            if (hardware == null || sensors == null)
            {
                return;
            }

            if (IsCpuHardware(hardware))
            {
                foreach (ISensor sensor in hardware.Sensors)
                {
                    if (IsCpuCoreTemperatureSensor(sensor))
                    {
                        sensors.Add(sensor);
                    }
                }
            }

            foreach (IHardware subHardware in hardware.SubHardware)
            {
                CollectCpuCoreSensorsRecursive(subHardware, sensors);
            }
        }

        private static void FindBestTemperatureSensorRecursive(IHardware hardware, Func<IHardware, bool> hardwareFilter, Func<ISensor, int> scoreSelector, ref ISensor bestSensor, ref int bestScore)
        {
            if (hardware == null)
            {
                return;
            }

            bool includeHardware = hardwareFilter == null || hardwareFilter(hardware);
            if (includeHardware)
            {
                foreach (ISensor sensor in hardware.Sensors)
                {
                    if (sensor == null || sensor.SensorType != SensorType.Temperature)
                    {
                        continue;
                    }

                    int score = scoreSelector != null ? scoreSelector(sensor) : 0;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestSensor = sensor;
                    }
                }
            }

            foreach (IHardware subHardware in hardware.SubHardware)
            {
                FindBestTemperatureSensorRecursive(subHardware, hardwareFilter, scoreSelector, ref bestSensor, ref bestScore);
            }
        }

        private static bool IsCpuCoreTemperatureSensor(ISensor sensor)
        {
            if (sensor == null || sensor.SensorType != SensorType.Temperature)
            {
                return false;
            }

            string name = sensor.Name ?? string.Empty;
            string lower = name.ToLowerInvariant();

            if (!lower.Contains("core"))
            {
                return false;
            }

            if (lower.Contains("package") || lower.Contains("max"))
            {
                return false;
            }

            return true;
        }

        private static bool IsCpuHardware(IHardware hardware)
        {
            return hardware != null && (hardware.HardwareType == HardwareType.Cpu || ContainsHardwareType(hardware, "cpu"));
        }

        private static bool IsGpuHardware(IHardware hardware)
        {
            return hardware != null && ContainsHardwareType(hardware, "gpu");
        }

        private static bool ContainsHardwareType(IHardware hardware, string token)
        {
            string text = hardware != null ? hardware.HardwareType.ToString() : string.Empty;
            return text.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int ScoreGpuSensor(ISensor sensor)
        {
            if (sensor == null)
            {
                return int.MinValue;
            }

            string lower = (sensor.Name ?? string.Empty).ToLowerInvariant();
            if (lower.Contains("gpu core")) return 1000;
            if (lower.Contains("gpu")) return 900;
            if (lower.Contains("temperature")) return 700;
            if (lower.Contains("hot spot")) return 600;
            return 100;
        }

        private static int ScoreChassisSensor(ISensor sensor)
        {
            if (sensor == null)
            {
                return int.MinValue;
            }

            string lower = (sensor.Name ?? string.Empty).ToLowerInvariant();
            if (lower.Contains("chassis")) return 1000;
            if (lower.Contains("system")) return 900;
            if (lower.Contains("ambient")) return 850;
            if (lower.Contains("board")) return 800;
            if (lower.Contains("motherboard")) return 750;
            if (lower.Contains("case")) return 700;
            return 100;
        }
    }
}
