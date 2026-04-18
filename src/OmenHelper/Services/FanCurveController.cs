using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OmenHelper.Services
{
    internal sealed class FanCurve
    {
        public List<(int Temp, int Rpm)> Points { get; set; } = new List<(int, int)>();

        public int SpinUpWindowMs { get; set; }

        public int SpinDownWindowMs { get; set; }


        public int Evaluate(double temp)
        {
            if (Points == null || Points.Count == 0) return 0;
            Points.Sort((a, b) => a.Temp.CompareTo(b.Temp));
            if (temp <= Points[0].Temp) return Points[0].Rpm;
            if (temp >= Points[Points.Count - 1].Temp) return Points[Points.Count - 1].Rpm;
            for (int i = 0; i < Points.Count - 1; i++)
            {
                var a = Points[i];
                var b = Points[i + 1];
                double midpoint = (a.Temp + b.Temp) / 2.0;
                if (temp < midpoint)
                {
                    return a.Rpm;
                }
            }
            return Points[Points.Count - 1].Rpm;
        }
    }

    internal struct TemperatureSample
    {
        public DateTime TimestampUtc;
        public double Temperature;
    }

    internal sealed class FanCurveController : IDisposable
    {
        private readonly OmenBiosClient _biosClient;
        private readonly HardwareMonitorCpuTemperatureReader _temperatureReader;
        private readonly string _settingsPath;
        private readonly object _curveLock = new object();
        private FanCurve _curve;
        private CancellationTokenSource _cts;
        private Task _loopTask;
        private readonly object _writeLock = new object();

        // Tunables
        private const int MinCurveRpm = 0;
        private const int MaxCurveRpm = 6500;
        private readonly int _sampleIntervalMs = 1000;
        private readonly double _emaAlpha = 0.2;
        private readonly int _defaultSpinUpWindowMs = 5000;
        private readonly int _defaultSpinDownWindowMs = 30000;
        private readonly int _hysteresisRpm = 100;
        private readonly int _minWriteIntervalMs = 5000;
        private readonly int _forceWriteThresholdRpm = 400;

        private int _spinUpWindowMs;
        private int _spinDownWindowMs;

        private readonly List<TemperatureSample> _samples = new List<TemperatureSample>();
        private double _smoothedTemp = double.NaN;
        private int _lastWrittenRpm = -1;
        private DateTime _lastWriteTime = DateTime.MinValue;

        public bool Enabled { get; private set; } = false;

        public event Action<string> LogMessage;

        public FanCurveController(OmenBiosClient biosClient, string settingsPath = null)
        {
            _biosClient = biosClient ?? throw new ArgumentNullException(nameof(biosClient));
            _temperatureReader = new HardwareMonitorCpuTemperatureReader();
            _settingsPath = settingsPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OmenHelper", "fan-curve.json");
            _spinUpWindowMs = _defaultSpinUpWindowMs;
            _spinDownWindowMs = _defaultSpinDownWindowMs;
            LoadOrCreateDefaultCurve();
        }

        // Public helpers for UI integration
        public IReadOnlyList<(int Temp, int Rpm)> GetPoints()
        {
            lock (_curveLock)
            {
                return _curve == null ? null : new List<(int Temp, int Rpm)>(_curve.Points).AsReadOnly();
            }
        }

        public bool TryGetTargetRpm(double temp, out int rpm)
        {
            lock (_curveLock)
            {
                if (_curve == null)
                {
                    rpm = 0;
                    return false;
                }

                rpm = _curve.Evaluate(temp);
                return true;
            }
        }

        public bool TryGetTelemetry(out double currentTemp, out double spinUpAverageTemp, out double spinDownAverageTemp)
        {
            lock (_curveLock)
            {
                currentTemp = double.NaN;
                spinUpAverageTemp = double.NaN;
                spinDownAverageTemp = double.NaN;

                if (_samples.Count == 0)
                {
                    return false;
                }

                currentTemp = _samples[_samples.Count - 1].Temperature;
                spinUpAverageTemp = AverageTemperature(_spinUpWindowMs);
                spinDownAverageTemp = AverageTemperature(_spinDownWindowMs);
                return true;
            }
        }

        public void SetPoints(IEnumerable<(int Temp, int Rpm)> points)
        {
            if (points == null) throw new ArgumentNullException(nameof(points));

            List<(int Temp, int Rpm)> normalized = NormalizePoints(points);
            if (normalized.Count == 0)
            {
                throw new ArgumentException("Curve must contain at least one valid point.", nameof(points));
            }

            lock (_curveLock)
            {
                _curve = new FanCurve
                {
                    SpinUpWindowMs = _spinUpWindowMs,
                    SpinDownWindowMs = _spinDownWindowMs,
                };
                _curve.Points.AddRange(normalized);
                SaveCurveUnsafe();
            }
        }

        public void ResetToDefault()
        {
            lock (_curveLock)
            {
                CreateConservativeDefaultUnsafe();
                SaveCurveUnsafe();
            }
        }

        public string GetSettingsPath() => _settingsPath;

        public decimal GetSpinUpWindowSeconds()
        {
            lock (_curveLock)
            {
                return MsToSeconds(_curve != null ? _curve.SpinUpWindowMs : _spinUpWindowMs);
            }
        }

        public decimal GetSpinDownWindowSeconds()
        {
            lock (_curveLock)
            {
                return MsToSeconds(_curve != null ? _curve.SpinDownWindowMs : _spinDownWindowMs);
            }
        }

        public void SetSpinUpWindowSeconds(decimal value)
        {
            lock (_curveLock)
            {
                _spinUpWindowMs = ClampInt(SecondsToMs(value), 1000, 60000);
                if (_curve != null) _curve.SpinUpWindowMs = _spinUpWindowMs;
                if (_spinDownWindowMs < _spinUpWindowMs)
                {
                    _spinDownWindowMs = _spinUpWindowMs;
                    if (_curve != null) _curve.SpinDownWindowMs = _spinDownWindowMs;
                }
                SaveCurveUnsafe();
            }
        }

        public void SetSpinDownWindowSeconds(decimal value)
        {
            lock (_curveLock)
            {
                _spinDownWindowMs = ClampInt(SecondsToMs(value), _spinUpWindowMs, 60000);
                if (_curve != null) _curve.SpinDownWindowMs = _spinDownWindowMs;
                SaveCurveUnsafe();
            }
        }

        public event Action<int> FanMinimumBlobWritten;

        private void LoadOrCreateDefaultCurve()
        {
            try
            {
                string dir = Path.GetDirectoryName(_settingsPath);
                if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                if (File.Exists(_settingsPath))
                {
                    try
                    {
                        string[] lines = File.ReadAllLines(_settingsPath, Encoding.UTF8);
                        int spinUpWindowMs = _defaultSpinUpWindowMs;
                        int spinDownWindowMs = _defaultSpinDownWindowMs;
                        var points = new List<(int, int)>();
                        foreach (string line in lines)
                        {
                            string l = line.Trim();
                            if (string.IsNullOrEmpty(l)) continue;

                            if (TryParseSettingLine(l, out string key, out string value))
                            {
                                ApplySettingValue(key, value, ref spinUpWindowMs, ref spinDownWindowMs);
                                continue;
                            }

                            string[] parts = l.Split(',');
                            if (parts.Length >= 2 && int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int t) && int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int r))
                            {
                                points.Add((t, r));
                            }
                        }

                        List<(int Temp, int Rpm)> normalized = NormalizePoints(points);
                        if (normalized.Count > 0)
                        {
                            _curve = new FanCurve();
                            _curve.Points.AddRange(normalized);
                            _spinUpWindowMs = ClampInt(spinUpWindowMs, 1000, 60000);
                            _spinDownWindowMs = ClampInt(spinDownWindowMs, _spinUpWindowMs, 60000);
                        }
                        else
                        {
                            CreateConservativeDefaultUnsafe();
                            SaveCurveUnsafe();
                        }
                    }
                    catch
                    {
                        CreateConservativeDefaultUnsafe();
                        SaveCurveUnsafe();
                    }
                }
                else
                {
                    CreateConservativeDefaultUnsafe();
                    SaveCurveUnsafe();
                }
            }
            catch
            {
                CreateConservativeDefaultUnsafe();
            }
        }

        private void CreateConservativeDefaultUnsafe()
        {
            _curve = new FanCurve();
            _curve.SpinUpWindowMs = _defaultSpinUpWindowMs;
            _curve.SpinDownWindowMs = _defaultSpinDownWindowMs;
            // conservative example: ramp from 40C -> 2000rpm up to 85C -> 5200rpm
            _curve.Points.Add((40, 2000));
            _curve.Points.Add((55, 2800));
            _curve.Points.Add((70, 4200));
            _curve.Points.Add((85, 5200));
        }

        private void SaveCurveUnsafe()
        {
            try
            {
                if (_curve == null)
                {
                    return;
                }

                var sb = new StringBuilder();
                sb.AppendLine("#spinUpWindowSeconds=" + MsToSeconds(_spinUpWindowMs).ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("#spinDownWindowSeconds=" + MsToSeconds(_spinDownWindowMs).ToString(CultureInfo.InvariantCulture));
                foreach (var p in _curve.Points)
                {
                    sb.AppendLine(p.Temp.ToString(CultureInfo.InvariantCulture) + "," + p.Rpm.ToString(CultureInfo.InvariantCulture));
                }
                File.WriteAllText(_settingsPath, sb.ToString(), Encoding.UTF8);
            }
            catch
            {
                // ignore
            }
        }

        private void UpdateTemperatureWindow(double temp)
        {
            DateTime now = DateTime.UtcNow;
            _samples.Add(new TemperatureSample { TimestampUtc = now, Temperature = temp });

            DateTime cutoff = now.AddSeconds(-60);
            int removeCount = 0;
            while (removeCount < _samples.Count && _samples[removeCount].TimestampUtc < cutoff)
            {
                removeCount++;
            }

            if (removeCount > 0)
            {
                _samples.RemoveRange(0, removeCount);
            }
        }

        private bool TryGetDesiredRpm(out int rpm)
        {
            lock (_curveLock)
            {
                rpm = 0;
                if (_curve == null || _samples.Count == 0)
                {
                    return false;
                }

                double spinUpAverage = AverageTemperature(_spinUpWindowMs);
                double spinDownAverage = AverageTemperature(_spinDownWindowMs);

                int upTarget = NormalizeRpmForBios(_curve.Evaluate(spinUpAverage));
                int downTarget = NormalizeRpmForBios(_curve.Evaluate(spinDownAverage));

                if (_lastWrittenRpm < 0)
                {
                    rpm = upTarget;
                    return true;
                }

                if (upTarget > _lastWrittenRpm)
                {
                    rpm = upTarget;
                    return true;
                }

                if (downTarget < _lastWrittenRpm)
                {
                    rpm = downTarget;
                    return true;
                }

                rpm = _lastWrittenRpm;
                return true;
            }
        }

        private double AverageTemperature(int windowMs)
        {
            if (_samples.Count == 0)
            {
                return double.NaN;
            }

            DateTime cutoff = DateTime.UtcNow.AddMilliseconds(-Math.Max(1000, windowMs));
            double total = 0;
            int count = 0;
            for (int i = _samples.Count - 1; i >= 0; i--)
            {
                TemperatureSample sample = _samples[i];
                if (sample.TimestampUtc < cutoff)
                {
                    break;
                }

                total += sample.Temperature;
                count++;
            }

            if (count == 0)
            {
                TemperatureSample latest = _samples[_samples.Count - 1];
                return latest.Temperature;
            }

            return total / count;
        }

        public void SetEnabled(bool enabled)
        {
            if (enabled == Enabled) return;
            Enabled = enabled;
            if (Enabled) Start(); else Stop();
        }

        public void Start()
        {
            if (_loopTask != null && !_loopTask.IsCompleted) return;
            _cts = new CancellationTokenSource();
            _loopTask = Task.Run(() => LoopAsync(_cts.Token));
            Log("Fan curve controller started.");
        }

        public void Stop()
        {
            try
            {
                _cts?.Cancel();
                _loopTask?.Wait(2000);
            }
            catch { }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                _loopTask = null;
                Log("Fan curve controller stopped.");
            }
        }

        private async Task LoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    double? temp = await TryReadTemperatureAsync().ConfigureAwait(false);
                    if (!temp.HasValue)
                    {
                        await Task.Delay(_sampleIntervalMs, ct).ConfigureAwait(false);
                        continue;
                    }

                    _smoothedTemp = double.IsNaN(_smoothedTemp) ? temp.Value : (_emaAlpha * temp.Value + (1 - _emaAlpha) * _smoothedTemp);
                    UpdateTemperatureWindow(temp.Value);

                    int desiredRpm;
                    if (!TryGetDesiredRpm(out desiredRpm))
                    {
                        await Task.Delay(_sampleIntervalMs, ct).ConfigureAwait(false);
                        continue;
                    }

                    if (ShouldWrite(desiredRpm))
                    {
                        await ApplyBlobAsync(desiredRpm).ConfigureAwait(false);
                    }

                    await Task.Delay(_sampleIntervalMs, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log("Fan curve loop error: " + ex.Message);
            }
        }

        private async Task<double?> TryReadTemperatureAsync()
        {
            double temp;
            if (_temperatureReader.TryGetTemperature(out temp) && IsReasonableTemperature(temp))
            {
                return temp;
            }

            try
            {
                int biosRawTemp = await _biosClient.GetTemperatureAsync().ConfigureAwait(false);
                if (biosRawTemp >= 0)
                {
                    temp = biosRawTemp;
                    if (IsReasonableTemperature(temp))
                    {
                        return temp;
                    }
                }
            }
            catch (Exception ex)
            {
                Log("BIOS temperature fallback failed: " + ex.Message);
            }

            return null;
        }

        private static bool IsReasonableTemperature(double temp)
        {
            return !double.IsNaN(temp) && !double.IsInfinity(temp) && temp >= 0 && temp <= 125;
        }

        private bool ShouldWrite(int targetRpm)
        {
            var now = DateTime.UtcNow;
            int diff = Math.Abs(targetRpm - _lastWrittenRpm);
            bool spinUp = targetRpm > _lastWrittenRpm;
            bool bigChange = diff >= _forceWriteThresholdRpm;

            if (bigChange)
            {
                return true;
            }

            if (spinUp)
            {
                return diff >= _hysteresisRpm;
            }

            if ((now - _lastWriteTime).TotalMilliseconds < _minWriteIntervalMs)
            {
                return false;
            }

            return diff >= _hysteresisRpm;
        }

        private async Task ApplyBlobAsync(int cpuRpm)
        {
            lock (_writeLock)
            {
                // do synchronous within lock to avoid concurrent writes
                try
                {
                    // read current blob
                    var readTask = _biosClient.GetPerformanceStatusBlobAsync();
                    readTask.Wait();
                    var read = readTask.Result;
                    byte[] blob = (read.ExecuteResult && read.ReturnCode == 0 && read.ReturnData != null && read.ReturnData.Length == 128)
                        ? read.ReturnData
                        : new byte[128];

                    int normalizedRpm = NormalizeRpmForBios(cpuRpm);
                    byte cpuByte = RpmToByte(normalizedRpm);
                    byte gpuByte = cpuByte; // use same for GPU by default
                    blob[0] = cpuByte;
                    blob[1] = gpuByte;

                    var writeTask = _biosClient.SetPerformanceStatusBlobAsync(blob);
                    writeTask.Wait();
                    var write = writeTask.Result;

                    bool ok = write.ExecuteResult && write.ReturnCode == 0;
                    if (ok)
                    {
                        _lastWrittenRpm = normalizedRpm;
                        _lastWriteTime = DateTime.UtcNow;
                        try { FanMinimumBlobWritten?.Invoke(normalizedRpm); } catch { }
                        Log($"Wrote fan blob: CPU {normalizedRpm} rpm -> b{cpuByte}");
                    }
                    else
                    {
                        Log($"Fan blob write failed: exec={write.ExecuteResult} rc={write.ReturnCode}");
                    }
                }
                catch (Exception ex)
                {
                    Log("ApplyBlob failed: " + ex.Message);
                }
            }

            await Task.CompletedTask;
        }

        private static int NormalizeRpmForBios(int rpm)
        {
            return RoundToStep100(rpm);
        }

        private static byte RpmToByte(int rpm)
        {
            int v = (int)Math.Round(rpm / 100.0);
            if (v < 0) v = 0;
            if (v > 255) v = 255;
            return (byte)v;
        }

        private static decimal MsToSeconds(int milliseconds)
        {
            return milliseconds / 1000m;
        }

        private static int SecondsToMs(decimal seconds)
        {
            decimal clampedSeconds = seconds < 0m ? 0m : seconds;
            decimal ms = decimal.Round(clampedSeconds * 1000m, 0, MidpointRounding.AwayFromZero);
            if (ms < int.MinValue) return int.MinValue;
            if (ms > int.MaxValue) return int.MaxValue;
            return (int)ms;
        }

        private static int ClampInt(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private static int RoundToStep100(int value)
        {
            return (int)(Math.Round(value / 100.0) * 100.0);
        }

        private static bool TryParseSettingLine(string line, out string key, out string value)
        {
            key = null;
            value = null;
            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            string trimmed = line.Trim();
            if (!trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                return false;
            }

            string withoutHash = trimmed.Substring(1);
            string[] parts = withoutHash.Split(new[] { '=' }, 2);
            if (parts.Length != 2)
            {
                return false;
            }

            key = parts[0].Trim();
            value = parts[1].Trim();
            return key.Length > 0;
        }

        private static void ApplySettingValue(string key, string value, ref int spinUpWindowMs, ref int spinDownWindowMs)
        {
            if (string.Equals(key, "spinUpWindowSeconds", StringComparison.OrdinalIgnoreCase))
            {
                if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsedSeconds))
                {
                    spinUpWindowMs = SecondsToMs(parsedSeconds);
                }
                return;
            }

            if (string.Equals(key, "spinDownWindowSeconds", StringComparison.OrdinalIgnoreCase))
            {
                if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsedSeconds))
                {
                    spinDownWindowMs = SecondsToMs(parsedSeconds);
                }
                return;
            }

            if (string.Equals(key, "spinUpWindowMs", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedUpMs))
            {
                spinUpWindowMs = parsedUpMs;
            }
            else if (string.Equals(key, "spinDownWindowMs", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedDownMs))
            {
                spinDownWindowMs = parsedDownMs;
            }
        }

        private static List<(int Temp, int Rpm)> NormalizePoints(IEnumerable<(int Temp, int Rpm)> points)
        {
            var list = new List<(int Temp, int Rpm)>();
            foreach (var p in points)
            {
                int temp = ClampInt(p.Temp, 0, 100);
                int rpm = RoundToStep100(p.Rpm);
                if (rpm < MinCurveRpm) rpm = MinCurveRpm;
                if (rpm > MaxCurveRpm) rpm = MaxCurveRpm;
                list.Add((temp, rpm));
            }

            list.Sort((a, b) => a.Temp.CompareTo(b.Temp));
            for (int i = 1; i < list.Count; i++)
            {
                if (list[i].Temp == list[i - 1].Temp)
                {
                    // reject duplicate temps by dropping later duplicate
                    list.RemoveAt(i);
                    i--;
                }
            }

            return list;
        }

        private void Log(string message)
        {
            try { LogMessage?.Invoke("[FanCurve] " + message); } catch { }
        }

        public void Dispose()
        {
            Stop();
            _temperatureReader.Dispose();
        }
    }
}
