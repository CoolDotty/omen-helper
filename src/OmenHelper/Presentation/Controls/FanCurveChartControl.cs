using System;
using System.Drawing;
using System.Windows.Forms;
using OmenHelper.Domain.Fan;

namespace OmenHelper.Presentation.Controls;

internal sealed class FanCurveChartControl : Control
{
    private FanCurveProfile _profile = new FanCurveProfile(FanCurveProfile.CpuGpuTemperaturePoints, new[] { 0, 0, 0, 0, 0, 0, 0, 0, 0 });
    private string _title = string.Empty;
    private bool _readOnly;
    private int _dragIndex = -1;
    private int _hoverIndex = -1;
    private double? _currentTemperatureC;
    private int? _currentFanRpm;
    private double? _hysteresisAnchorTemperatureC;
    private int _hysteresisRiseDeltaC = 5;
    private int _hysteresisDropDeltaC = 10;

    public FanCurveChartControl()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
        Size = new Size(240, 180);
        BackColor = Color.White;
    }

    public event EventHandler<FanCurveProfile> CurveEdited;
    public event EventHandler<FanCurveProfile> CurveEditCommitted;

    public string Title
    {
        get => _title;
        set { _title = value ?? string.Empty; Invalidate(); }
    }

    public bool ReadOnlyCurve
    {
        get => _readOnly;
        set { _readOnly = value; Invalidate(); }
    }

    public FanCurveProfile Profile
    {
        get => _profile;
        set
        {
            if (IsDraggingPoint)
            {
                return;
            }

            _profile = value ?? _profile;
            Invalidate();
        }
    }

    public bool IsDraggingPoint => _dragIndex >= 0;

    public double? CurrentTemperatureC
    {
        get => _currentTemperatureC;
        set { _currentTemperatureC = value; Invalidate(); }
    }

    public int? CurrentFanRpm
    {
        get => _currentFanRpm;
        set { _currentFanRpm = value; Invalidate(); }
    }

    public double? HysteresisAnchorTemperatureC
    {
        get => _hysteresisAnchorTemperatureC;
        set { _hysteresisAnchorTemperatureC = value; Invalidate(); }
    }

    public int HysteresisRiseDeltaC
    {
        get => _hysteresisRiseDeltaC;
        set { _hysteresisRiseDeltaC = Math.Max(0, value); Invalidate(); }
    }

    public int HysteresisDropDeltaC
    {
        get => _hysteresisDropDeltaC;
        set { _hysteresisDropDeltaC = Math.Max(0, value); Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.Clear(ReadOnlyCurve ? Color.FromArgb(245, 245, 245) : Color.White);

        Rectangle chart = GetChartBounds();
        using (Pen axisPen = new Pen(Color.DarkGray))
        using (Pen linePen = new Pen(ReadOnlyCurve ? Color.Gray : Color.DodgerBlue, 2f))
        using (Brush pointBrush = new SolidBrush(ReadOnlyCurve ? Color.Gray : Color.DodgerBlue))
        using (Brush hoverBrush = new SolidBrush(Color.OrangeRed))
        using (Brush liveBrush = new SolidBrush(Color.Red))
        using (Pen livePen = new Pen(Color.Red, 2f))
        using (Pen hysteresisPen = new Pen(Color.FromArgb(150, Color.Gray), 2f))
        using (Pen minimumNonZeroPen = new Pen(Color.FromArgb(170, Color.DarkOrange), 1f))
        using (Brush textBrush = new SolidBrush(Color.Black))
        using (Font smallFont = new Font(Font.FontFamily, 8f))
        {
            minimumNonZeroPen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;

            g.DrawString(Title, Font, textBrush, new PointF(4, 4));
            g.DrawRectangle(axisPen, chart);
            DrawDeadZoneGuide(g, chart, minimumNonZeroPen, smallFont, textBrush);

            DrawEffectiveCurve(g, chart, linePen);

            for (int i = 0; i < _profile.TemperaturePoints.Count; i++)
            {
                Point point = GetPoint(chart, i, _profile[i]);
                Rectangle marker = new Rectangle(point.X - 4, point.Y - 4, 8, 8);
                g.FillEllipse(i == _hoverIndex ? hoverBrush : pointBrush, marker);
                g.DrawString(_profile.TemperaturePoints[i].ToString(), smallFont, textBrush, point.X - 9, chart.Bottom + 4);
            }

            g.DrawString(_profile.TemperaturePoints[0] + "C", smallFont, textBrush, 2, chart.Bottom + 4);
            g.DrawString("6500", smallFont, textBrush, 2, chart.Top - 2);
            g.DrawString("1300", smallFont, Brushes.DarkOrange, 2, GetYForRpm(chart, 1300) - 8);
            g.DrawString("0", smallFont, textBrush, 8, chart.Bottom - 12);

            if (_currentTemperatureC.HasValue && _currentFanRpm.HasValue)
            {
                Point livePoint = GetLivePoint(chart, _currentTemperatureC.Value, _currentFanRpm.Value);
                DrawHysteresisRange(g, chart, livePoint, _hysteresisAnchorTemperatureC, hysteresisPen, smallFont);
                g.DrawLine(livePen, livePoint.X - 5, livePoint.Y, livePoint.X + 5, livePoint.Y);
                g.DrawLine(livePen, livePoint.X, livePoint.Y - 5, livePoint.X, livePoint.Y + 5);
                g.FillEllipse(liveBrush, new Rectangle(livePoint.X - 4, livePoint.Y - 4, 8, 8));
            }

            if (_hoverIndex >= 0 && _hoverIndex < _profile.TemperaturePoints.Count)
            {
                string hover = _profile.TemperaturePoints[_hoverIndex] + "°C / " + _profile[_hoverIndex] + " RPM";
                SizeF size = g.MeasureString(hover, smallFont);
                RectangleF box = new RectangleF(chart.Right - size.Width - 8, 20, size.Width + 6, size.Height + 4);
                g.FillRectangle(Brushes.Beige, box);
                g.DrawRectangle(Pens.SaddleBrown, box.X, box.Y, box.Width, box.Height);
                g.DrawString(hover, smallFont, Brushes.Black, box.X + 3, box.Y + 2);
            }
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (ReadOnlyCurve)
        {
            return;
        }

        _dragIndex = HitTestIndex(e.Location);
        if (_dragIndex >= 0)
        {
            UpdateDrag(e.Location, committed: false);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _hoverIndex = HitTestIndex(e.Location);
        if (_dragIndex >= 0)
        {
            UpdateDrag(e.Location, committed: false);
        }

        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragIndex >= 0)
        {
            UpdateDrag(e.Location, committed: true);
            _dragIndex = -1;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverIndex = -1;
        Invalidate();
    }

    private void UpdateDrag(Point location, bool committed)
    {
        Rectangle chart = GetChartBounds();
        int rpm = GetRpmForY(chart, location.Y);
        _profile = _profile.WithPoint(_dragIndex, rpm);
        CurveEdited?.Invoke(this, _profile);
        if (committed)
        {
            CurveEditCommitted?.Invoke(this, _profile);
        }

        Invalidate();
    }

    private Rectangle GetChartBounds()
    {
        return new Rectangle(28, 24, Math.Max(100, Width - 40), Math.Max(80, Height - 54));
    }

    private Point GetPoint(Rectangle chart, int index, int rpm)
    {
        float x = chart.Left + (chart.Width * index / (float)(_profile.TemperaturePoints.Count - 1));
        int y = GetYForRpm(chart, rpm);
        return new Point((int)Math.Round(x), y);
    }

    private int HitTestIndex(Point location)
    {
        Rectangle chart = GetChartBounds();
        for (int i = 0; i < _profile.TemperaturePoints.Count; i++)
        {
            Point point = GetPoint(chart, i, _profile[i]);
            Rectangle hit = new Rectangle(point.X - 7, point.Y - 7, 14, 14);
            if (hit.Contains(location))
            {
                return i;
            }
        }

        return -1;
    }

    private Point GetLivePoint(Rectangle chart, double temperatureC, int fanRpm)
    {
        double minTemp = _profile.TemperaturePoints[0];
        double maxTemp = _profile.TemperaturePoints[_profile.TemperaturePoints.Count - 1];
        double clampedTemp = Math.Max(minTemp, Math.Min(maxTemp, temperatureC));
        int clampedRpm = Math.Max(0, Math.Min(6500, fanRpm));

        double tempRatio = _profile.TemperaturePoints.Count == 1
            ? 0
            : (clampedTemp - minTemp) / (maxTemp - minTemp);

        int x = chart.Left + (int)Math.Round(chart.Width * tempRatio);
        int y = GetYForRpm(chart, clampedRpm);
        return new Point(x, y);
    }

    private void DrawHysteresisRange(Graphics g, Rectangle chart, Point livePoint, double? anchorTemperatureC, Pen hysteresisPen, Font smallFont)
    {
        if (!anchorTemperatureC.HasValue)
        {
            return;
        }

        int leftIndex = 0;
        int rightIndex = _profile.TemperaturePoints.Count - 1;
        double minTemp = _profile.TemperaturePoints[leftIndex];
        double maxTemp = _profile.TemperaturePoints[rightIndex];

        double anchor = anchorTemperatureC.Value;
        double dropTemp = Math.Max(minTemp, anchor - _hysteresisDropDeltaC);
        double riseTemp = Math.Min(maxTemp, anchor + _hysteresisRiseDeltaC);
        if (riseTemp <= dropTemp)
        {
            riseTemp = Math.Min(maxTemp, dropTemp + 1);
        }

        Point left = GetPointForTemperature(chart, dropTemp);
        Point right = GetPointForTemperature(chart, riseTemp);
        int bandY = livePoint.Y;

        g.DrawLine(hysteresisPen, left.X, bandY, right.X, bandY);
        g.DrawLine(hysteresisPen, left.X, bandY - 6, left.X, bandY + 6);
        g.DrawLine(hysteresisPen, right.X, bandY - 6, right.X, bandY + 6);

        string label = "Hys @" + anchor.ToString("0.0") + "°C";
        SizeF size = g.MeasureString(label, smallFont);
        float labelX = Math.Max(chart.Left + 2, Math.Min(chart.Right - size.Width - 2, left.X + ((right.X - left.X) - size.Width) / 2f));
        float labelY = Math.Max(4, bandY - size.Height - 10);
        g.DrawString(label, smallFont, Brushes.Gray, labelX, labelY);
    }

    private void DrawEffectiveCurve(Graphics g, Rectangle chart, Pen linePen)
    {
        for (int i = 1; i < _profile.TemperaturePoints.Count; i++)
        {
            Point leftPoint = GetPoint(chart, i - 1, _profile[i - 1]);
            Point rightPoint = GetPoint(chart, i, _profile[i]);
            int leftRpm = _profile[i - 1];
            int rightRpm = _profile[i];

            bool leftZero = leftRpm == 0;
            bool rightZero = rightRpm == 0;
            bool leftAboveThreshold = leftRpm >= 1300;
            bool rightAboveThreshold = rightRpm >= 1300;

            if ((leftZero && rightZero) || (leftAboveThreshold && rightAboveThreshold))
            {
                g.DrawLine(linePen, leftPoint, rightPoint);
                continue;
            }

            if ((leftZero && rightAboveThreshold) || (leftAboveThreshold && rightZero))
            {
                double thresholdRatio = GetThresholdCrossingRatio(leftRpm, rightRpm);
                int thresholdX = leftPoint.X + (int)Math.Round((rightPoint.X - leftPoint.X) * thresholdRatio);
                int zeroY = GetYForRpm(chart, 0);
                int thresholdY = GetYForRpm(chart, 1300);

                if (leftZero)
                {
                    g.DrawLine(linePen, leftPoint.X, zeroY, thresholdX, zeroY);
                    g.DrawLine(linePen, thresholdX, zeroY, thresholdX, thresholdY);
                    g.DrawLine(linePen, thresholdX, thresholdY, rightPoint.X, rightPoint.Y);
                }
                else
                {
                    g.DrawLine(linePen, leftPoint.X, leftPoint.Y, thresholdX, thresholdY);
                    g.DrawLine(linePen, thresholdX, thresholdY, thresholdX, zeroY);
                    g.DrawLine(linePen, thresholdX, zeroY, rightPoint.X, zeroY);
                }

                continue;
            }

            g.DrawLine(linePen, leftPoint, rightPoint);
        }
    }

    private static double GetThresholdCrossingRatio(int leftRpm, int rightRpm)
    {
        int delta = rightRpm - leftRpm;
        if (delta == 0)
        {
            return 0;
        }

        double ratio = (1300.0 - leftRpm) / delta;
        return Math.Max(0.0, Math.Min(1.0, ratio));
    }

    private Point GetPointForTemperature(Rectangle chart, double temperatureC)
    {
        double minTemp = _profile.TemperaturePoints[0];
        double maxTemp = _profile.TemperaturePoints[_profile.TemperaturePoints.Count - 1];
        double clampedTemp = Math.Max(minTemp, Math.Min(maxTemp, temperatureC));
        double tempRatio = _profile.TemperaturePoints.Count == 1
            ? 0
            : (clampedTemp - minTemp) / (maxTemp - minTemp);
        int x = chart.Left + (int)Math.Round(chart.Width * tempRatio);
        return new Point(x, chart.Bottom);
    }

    private static void DrawDeadZoneGuide(Graphics g, Rectangle chart, Pen minimumNonZeroPen, Font smallFont, Brush textBrush)
    {
        int boundaryY = GetYForRpm(chart, 1300);
        g.DrawLine(minimumNonZeroPen, chart.Left, boundaryY, chart.Right, boundaryY);

        const string label = "<1300 snaps to 0";
        SizeF size = g.MeasureString(label, smallFont);
        float x = Math.Max(chart.Left + 4, chart.Right - size.Width - 4);
        float y = Math.Min(chart.Bottom - size.Height - 2, boundaryY + 2);
        g.DrawString(label, smallFont, textBrush, x, y);
    }

    private static int GetYForRpm(Rectangle chart, int rpm)
    {
        int clampedRpm = Math.Max(0, Math.Min(6500, rpm));
        double rpmRatio = clampedRpm / 6500.0;
        return chart.Bottom - (int)Math.Round(chart.Height * rpmRatio);
    }

    private static int GetRpmForY(Rectangle chart, int y)
    {
        int clampedY = Math.Max(chart.Top, Math.Min(chart.Bottom, y));
        double ratio = 1.0 - ((clampedY - chart.Top) / (double)chart.Height);
        return FanCurveProfile.NormalizeRpm((int)Math.Round(ratio * 6500.0));
    }
}
