using System;
using System.Collections.Generic;
using System.Linq;

namespace OmenHelper.Domain.Fan;

internal sealed class FanCurveProfile
{
    public static readonly int[] CpuGpuTemperaturePoints = { 50, 55, 60, 65, 70, 75, 80, 85, 90 };
    public static readonly int[] ChassisTemperaturePoints = { 30, 35, 40, 45, 50, 55, 60, 65, 70 };

    private readonly int[] _temperaturePoints;
    private readonly int[] _rpmPoints;

    public FanCurveProfile(IEnumerable<int> temperaturePoints, IEnumerable<int> rpmPoints)
    {
        if (temperaturePoints == null)
        {
            throw new ArgumentNullException(nameof(temperaturePoints));
        }

        if (rpmPoints == null)
        {
            throw new ArgumentNullException(nameof(rpmPoints));
        }

        _temperaturePoints = temperaturePoints.ToArray();
        _rpmPoints = rpmPoints.ToArray();
        if (_temperaturePoints.Length != 9 || _rpmPoints.Length != _temperaturePoints.Length)
        {
            throw new ArgumentException("Fan curve must define exactly 9 temperature/RPM points.", nameof(rpmPoints));
        }

        for (int i = 0; i < _rpmPoints.Length; i++)
        {
            _rpmPoints[i] = NormalizeRpm(_rpmPoints[i]);
        }
    }

    public IReadOnlyList<int> TemperaturePoints => _temperaturePoints;

    public IReadOnlyList<int> RpmPoints => _rpmPoints;

    public int this[int index] => _rpmPoints[index];

    public FanCurveProfile WithPoint(int index, int rpm)
    {
        if (index < 0 || index >= _rpmPoints.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        int[] clone = (int[])_rpmPoints.Clone();
        clone[index] = NormalizeRpm(rpm);
        return new FanCurveProfile(_temperaturePoints, clone);
    }

    public int EvaluateCpuOrGpu(double temperatureC)
    {
        if (double.IsNaN(temperatureC) || double.IsInfinity(temperatureC))
        {
            temperatureC = _temperaturePoints[0];
        }

        if (temperatureC <= _temperaturePoints[0])
        {
            return _rpmPoints[0];
        }

        if (temperatureC >= _temperaturePoints[_temperaturePoints.Length - 1])
        {
            return _rpmPoints[_rpmPoints.Length - 1];
        }

        for (int i = 0; i < _temperaturePoints.Length - 1; i++)
        {
            int leftTemp = _temperaturePoints[i];
            int rightTemp = _temperaturePoints[i + 1];
            if (temperatureC < rightTemp || i == _temperaturePoints.Length - 2)
            {
                int leftRpm = _rpmPoints[i];
                int rightRpm = _rpmPoints[i + 1];
                double ratio = (temperatureC - leftTemp) / (rightTemp - leftTemp);
                return NormalizeRpm((int)Math.Round(leftRpm + ((rightRpm - leftRpm) * ratio)));
            }
        }

        return _rpmPoints[_rpmPoints.Length - 1];
    }

    public int EvaluateChassis(double temperatureC)
    {
        if (temperatureC < _temperaturePoints[0])
        {
            return 0;
        }

        return EvaluateCpuOrGpu(temperatureC);
    }

    public static int NormalizeRpm(int rpm)
    {
        int clamped = Math.Max(0, Math.Min(6500, rpm));
        int quantized = (int)(Math.Round(clamped / 100.0) * 100.0);
        if (quantized > 0 && quantized < 1300)
        {
            return 0;
        }

        return quantized;
    }
}
