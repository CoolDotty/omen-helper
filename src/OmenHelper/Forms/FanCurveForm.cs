using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using OmenHelper.Services;

namespace OmenHelper
{
    internal sealed class FanCurveForm : Form
    {
        private readonly OmenPerformanceController _controller;
        private readonly FanCurveGraphControl _graph;
        private readonly DataGridView _grid;
        private readonly CheckBox _enableCheck;
        private readonly NumericUpDown _spinUpWindowUpDown;
        private readonly NumericUpDown _spinDownWindowUpDown;
        private readonly NumericUpDown _breakpointPaddingUpDown;
        private readonly Button _addButton;
        private readonly Button _removeButton;
        private readonly Button _saveButton;
        private readonly Button _resetButton;
        private readonly Label _hintLabel;
        private readonly Timer _telemetryTimer;
        private bool _suppressUiUpdates;

        public FanCurveForm(OmenPerformanceController controller)
        {
            _controller = controller ?? throw new ArgumentNullException(nameof(controller));

            Text = "Fan Curve Manager";
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(900, 650);
            MinimumSize = new Size(760, 540);

            Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point);

            _hintLabel = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                Padding = new Padding(0, 0, 0, 8),
                Text = "Drag points on the graph to shape the fan curve. Double-click empty space to add a point. Press Delete to remove the selected point."
            };

            _graph = new FanCurveGraphControl
            {
                Dock = DockStyle.Top,
                Height = 300,
                Margin = new Padding(0, 0, 0, 10)
            };
            _graph.PointsChanged += GraphOnPointsChanged;

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                RowHeadersVisible = false
            };
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Temp",
                HeaderText = "Temperature (°C)",
                ValueType = typeof(int)
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Rpm",
                HeaderText = "Target RPM",
                ValueType = typeof(int)
            });
            _grid.CellEndEdit += GridOnCellEndEdit;
            _grid.UserDeletingRow += GridOnUserDeletingRow;

            _enableCheck = new CheckBox
            {
                Text = "Enable automatic fan curve",
                AutoSize = true,
                Margin = new Padding(0, 0, 12, 0)
            };
            _enableCheck.CheckedChanged += (_, __) =>
            {
                if (_suppressUiUpdates) return;
                _controller.SetFanCurveEnabled(_enableCheck.Checked);
            };

            _spinUpWindowUpDown = CreateTimingUpDown(1000, 60000, 5000);
            _spinUpWindowUpDown.ValueChanged += (_, __) =>
            {
                if (_suppressUiUpdates) return;
                _controller.SetFanCurveSpinUpWindowMs((int)_spinUpWindowUpDown.Value);
                SyncTimingControls();
            };

            _spinDownWindowUpDown = CreateTimingUpDown(1000, 60000, 15000);
            _spinDownWindowUpDown.ValueChanged += (_, __) =>
            {
                if (_suppressUiUpdates) return;
                _controller.SetFanCurveSpinDownWindowMs((int)_spinDownWindowUpDown.Value);
                SyncTimingControls();
            };

            _breakpointPaddingUpDown = CreatePaddingUpDown(0, 15, 5);
            _breakpointPaddingUpDown.ValueChanged += (_, __) =>
            {
                if (_suppressUiUpdates) return;
                _controller.SetFanCurveBreakpointPaddingCelsius((int)_breakpointPaddingUpDown.Value);
            };

            _addButton = new Button { Text = "Add Point", AutoSize = true };
            _removeButton = new Button { Text = "Remove Selected", AutoSize = true };
            _saveButton = new Button { Text = "Save", AutoSize = true };
            _resetButton = new Button { Text = "Reset to Default", AutoSize = true };

            _addButton.Click += (_, __) => AddPoint();
            _removeButton.Click += (_, __) => RemoveSelectedPoint();
            _saveButton.Click += (_, __) => SavePoints();
            _resetButton.Click += (_, __) => ResetToDefault();

            FlowLayoutPanel controls = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Padding = new Padding(0, 0, 0, 8)
            };
            controls.Controls.Add(_addButton);
            controls.Controls.Add(_removeButton);
            controls.Controls.Add(_saveButton);
            controls.Controls.Add(_resetButton);
            controls.Controls.Add(_enableCheck);
            controls.Controls.Add(new Label { Text = " Spin up ms:", AutoSize = true, Padding = new Padding(12, 6, 4, 0) });
            controls.Controls.Add(_spinUpWindowUpDown);
            controls.Controls.Add(new Label { Text = " Spin down ms:", AutoSize = true, Padding = new Padding(12, 6, 4, 0) });
            controls.Controls.Add(_spinDownWindowUpDown);
            controls.Controls.Add(new Label { Text = " Padding °C:", AutoSize = true, Padding = new Padding(12, 6, 4, 0) });
            controls.Controls.Add(_breakpointPaddingUpDown);

            GroupBox gridGroup = new GroupBox
            {
                Text = "Curve Points",
                Dock = DockStyle.Fill,
                Padding = new Padding(12)
            };
            gridGroup.Controls.Add(_grid);

            TableLayoutPanel root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(12)
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 300F));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            root.Controls.Add(_hintLabel, 0, 0);
            root.Controls.Add(_graph, 0, 1);
            root.Controls.Add(controls, 0, 2);
            root.Controls.Add(gridGroup, 0, 3);

            Controls.Add(root);

            _telemetryTimer = new Timer { Interval = 500 };
            _telemetryTimer.Tick += (_, __) => UpdateTelemetryOverlay();

            Load += (_, __) =>
            {
                LoadCurrentCurve();
                UpdateTelemetryOverlay();
                _telemetryTimer.Start();
            };
            FormClosed += (_, __) =>
            {
                _telemetryTimer.Stop();
                _graph.PointsChanged -= GraphOnPointsChanged;
            };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _telemetryTimer?.Dispose();
            }

            base.Dispose(disposing);
        }

        private void LoadCurrentCurve()
        {
            try
            {
                _suppressUiUpdates = true;
                IReadOnlyList<(int Temp, int Rpm)> points = _controller.GetFanCurvePoints();
                if (points == null || points.Count == 0)
                {
                    points = new List<(int Temp, int Rpm)>
                    {
                        (40, 2000),
                        (55, 2800),
                        (70, 4200),
                        (85, 5200)
                    };
                }

                ApplyPointsToUi(points);
                _enableCheck.Checked = _controller.IsFanCurveEnabled();
                _spinUpWindowUpDown.Value = ClampDecimal(_controller.GetFanCurveSpinUpWindowMs(), _spinUpWindowUpDown.Minimum, _spinUpWindowUpDown.Maximum);
                _spinDownWindowUpDown.Value = ClampDecimal(_controller.GetFanCurveSpinDownWindowMs(), _spinDownWindowUpDown.Minimum, _spinDownWindowUpDown.Maximum);
                _breakpointPaddingUpDown.Value = ClampDecimal(_controller.GetFanCurveBreakpointPaddingCelsius(), _breakpointPaddingUpDown.Minimum, _breakpointPaddingUpDown.Maximum);
                SyncTimingControls();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Failed to load curve: " + ex.Message, "Fan Curve", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _suppressUiUpdates = false;
            }
        }

        private void SyncTimingControls()
        {
            if (_spinDownWindowUpDown.Value < _spinUpWindowUpDown.Value)
            {
                _spinDownWindowUpDown.Value = _spinUpWindowUpDown.Value;
            }
        }

        private static decimal ClampDecimal(int value, decimal min, decimal max)
        {
            if (value < (int)min) return min;
            if (value > (int)max) return max;
            return value;
        }

        private static NumericUpDown CreateTimingUpDown(int min, int max, int value)
        {
            return new NumericUpDown
            {
                Minimum = min,
                Maximum = max,
                Increment = 100,
                ThousandsSeparator = true,
                Width = 92,
                Value = value
            };
        }

        private static NumericUpDown CreatePaddingUpDown(int min, int max, int value)
        {
            return new NumericUpDown
            {
                Minimum = min,
                Maximum = max,
                Increment = 1,
                Width = 60,
                Value = value
            };
        }

        private void ApplyPointsToUi(IReadOnlyList<(int Temp, int Rpm)> points)
        {
            _graph.SetPoints(points);

            _grid.Rows.Clear();
            foreach (var p in points)
            {
                _grid.Rows.Add(p.Temp, p.Rpm);
            }

            if (_grid.Rows.Count > 0)
            {
                _grid.Rows[0].Selected = true;
            }
        }

        private List<(int Temp, int Rpm)> ReadPointsFromGrid()
        {
            var points = new List<(int Temp, int Rpm)>();
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.IsNewRow) continue;

                string tempText = Convert.ToString(row.Cells[0].Value);
                string rpmText = Convert.ToString(row.Cells[1].Value);
                if (!int.TryParse(tempText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int temp) &&
                    !int.TryParse(tempText, NumberStyles.Integer, CultureInfo.CurrentCulture, out temp))
                {
                    throw new InvalidOperationException("Invalid temperature value: " + tempText);
                }

                if (!int.TryParse(rpmText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rpm) &&
                    !int.TryParse(rpmText, NumberStyles.Integer, CultureInfo.CurrentCulture, out rpm))
                {
                    throw new InvalidOperationException("Invalid RPM value: " + rpmText);
                }

                if (temp < 0) temp = 0;
                if (temp > 100) temp = 100;
                if (rpm < 0) rpm = 0;
                if (rpm > 6500) rpm = 6500;
                rpm = (int)(Math.Round(rpm / 100.0) * 100.0);

                points.Add((temp, rpm));
            }

            if (points.Count == 0)
            {
                throw new InvalidOperationException("Curve must contain at least one point.");
            }

            return points;
        }

        private void AddPoint()
        {
            int temp = 50;
            int rpm = 2200;

            if (_grid.Rows.Count > 0)
            {
                DataGridViewRow lastRow = _grid.Rows[_grid.Rows.Count - 1];
                int.TryParse(Convert.ToString(lastRow.Cells[0].Value), NumberStyles.Integer, CultureInfo.InvariantCulture, out temp);
                int.TryParse(Convert.ToString(lastRow.Cells[1].Value), NumberStyles.Integer, CultureInfo.InvariantCulture, out rpm);
                temp += 5;
            }

            _grid.Rows.Add(temp, rpm);
            _grid.ClearSelection();
            _grid.Rows[_grid.Rows.Count - 1].Selected = true;
            _grid.CurrentCell = _grid.Rows[_grid.Rows.Count - 1].Cells[0];
            PushGridToGraph();
        }

        private void RemoveSelectedPoint()
        {
            if (_grid.SelectedRows.Count == 0)
            {
                return;
            }

            int index = _grid.SelectedRows[0].Index;
            if (index < 0 || index >= _grid.Rows.Count)
            {
                return;
            }

            _grid.Rows.RemoveAt(index);
            PushGridToGraph();
        }

        private void SavePoints()
        {
            try
            {
                List<(int Temp, int Rpm)> points = ReadPointsFromGrid();
                points = points.OrderBy(p => p.Temp).ToList();

                for (int i = 1; i < points.Count; i++)
                {
                    if (Math.Abs(points[i].Temp - points[i - 1].Temp) < 0.000001)
                    {
                        MessageBox.Show(this, "Duplicate temperature values found. Each point needs a unique temperature.", "Fan Curve", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                }

                _controller.SetFanCurvePoints(points);
                ApplyPointsToUi(points);
                MessageBox.Show(this, "Saved fan curve.", "Fan Curve", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Failed to save curve: " + ex.Message, "Fan Curve", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ResetToDefault()
        {
            if (MessageBox.Show(this, "Reset the fan curve to the conservative default?", "Fan Curve", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }

            _controller.ResetFanCurveToDefault();
            LoadCurrentCurve();
        }

        private void UpdateTelemetryOverlay()
        {
            if (_suppressUiUpdates)
            {
                return;
            }

            if (_controller.TryGetFanTelemetry(out double currentTemp, out double spinUpAverageTemp, out double spinDownAverageTemp))
            {
                _graph.SetTelemetry(
                    currentTemp,
                    spinUpAverageTemp,
                    spinDownAverageTemp,
                    _controller.GetFanCurveBreakpointPaddingCelsius());
            }
            else
            {
                _graph.SetTelemetry(null, null, null, _controller.GetFanCurveBreakpointPaddingCelsius());
            }
        }

        private void PushGridToGraph()
        {
            try
            {
                var points = ReadPointsFromGrid().OrderBy(p => p.Temp).ToList();
                _graph.SetPoints(points);
            }
            catch
            {
                // ignore until user finishes editing
            }
        }

        private void GraphOnPointsChanged(object sender, EventArgs e)
        {
            if (_suppressUiUpdates)
            {
                return;
            }

            try
            {
                _suppressUiUpdates = true;
                var points = _graph.GetPoints();
                _grid.Rows.Clear();
                foreach (var p in points)
                {
                    _grid.Rows.Add(p.Temp, p.Rpm);
                }
            }
            finally
            {
                _suppressUiUpdates = false;
            }
        }

        private void GridOnCellEndEdit(object sender, DataGridViewCellEventArgs e)
        {
            if (_suppressUiUpdates)
            {
                return;
            }

            PushGridToGraph();
        }

        private void GridOnUserDeletingRow(object sender, DataGridViewRowCancelEventArgs e)
        {
            if (_suppressUiUpdates)
            {
                return;
            }

            BeginInvoke(new Action(PushGridToGraph));
        }

        private sealed class FanCurveGraphControl : Control
        {
            private sealed class GraphPoint
            {
                public int Temp;
                public int Rpm;
            }

            private readonly List<GraphPoint> _points = new List<GraphPoint>();
            private readonly Timer _animationTimer = new Timer();
            private bool _dragging;
            private int _selectedIndex = -1;
            private const int MinTemp = 30;
            private const int MaxTemp = 100;
            private const int MinRpm = 0;
            private const int MaxRpm = 6500;
            private const int PointRadius = 7;
            private double? _targetCurrentTemp;
            private double? _targetSpinUpTemp;
            private double? _targetSpinDownTemp;
            private double? _displayCurrentTemp;
            private double? _displaySpinUpTemp;
            private double? _displaySpinDownTemp;
            private int _breakpointPaddingCelsius;

            public event EventHandler PointsChanged;

            public FanCurveGraphControl()
            {
                DoubleBuffered = true;
                TabStop = true;
                BackColor = Color.White;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
                _animationTimer.Interval = 33;
                _animationTimer.Tick += AnimationTimerOnTick;
            }

            public void SetPoints(IEnumerable<(int Temp, int Rpm)> points)
            {
                _points.Clear();
                if (points != null)
                {
                    foreach (var p in points)
                    {
                        _points.Add(new GraphPoint
                        {
                            Temp = ClampTemp(p.Temp),
                            Rpm = ClampRpm(p.Rpm)
                        });
                    }
                }

                SortAndNormalize();
                Invalidate();
            }

            public IReadOnlyList<(int Temp, int Rpm)> GetPoints()
            {
                var list = new List<(int Temp, int Rpm)>();
                foreach (var p in _points)
                {
                    list.Add((p.Temp, p.Rpm));
                }
                return list;
            }

            public void SetTelemetry(double? currentTemp, double? spinUpAverageTemp, double? spinDownAverageTemp, int breakpointPaddingCelsius)
            {
                _targetCurrentTemp = currentTemp;
                _targetSpinUpTemp = spinUpAverageTemp;
                _targetSpinDownTemp = spinDownAverageTemp;
                _breakpointPaddingCelsius = Math.Max(0, breakpointPaddingCelsius);

                if (!_targetCurrentTemp.HasValue && !_targetSpinUpTemp.HasValue && !_targetSpinDownTemp.HasValue)
                {
                    _displayCurrentTemp = null;
                    _displaySpinUpTemp = null;
                    _displaySpinDownTemp = null;
                    _animationTimer.Stop();
                    Invalidate();
                    return;
                }

                if (!_animationTimer.Enabled)
                {
                    _animationTimer.Start();
                }

                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);

                Rectangle plot = GetPlotArea();
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                e.Graphics.Clear(BackColor);

                using (var axisPen = new Pen(Color.Silver, 1))
                using (var linePen = new Pen(Color.FromArgb(90, 140, 220), 2f))
                using (var pointBrush = new SolidBrush(Color.FromArgb(70, 160, 70)))
                using (var selectedBrush = new SolidBrush(Color.OrangeRed))
                using (var textBrush = new SolidBrush(ForeColor))
                using (var gridPen = new Pen(Color.Gainsboro, 1))
                using (var paddingPen = new Pen(Color.FromArgb(150, 150, 150), 2f))
                using (var paddingBrush = new SolidBrush(Color.FromArgb(150, 150, 150)))
                using (var currentPen = new Pen(Color.LimeGreen, 2f))
                using (var spinUpPen = new Pen(Color.Red, 2f))
                using (var spinDownPen = new Pen(Color.Blue, 2f))
                using (var currentBrush = new SolidBrush(Color.LimeGreen))
                using (var spinUpBrush = new SolidBrush(Color.Red))
                using (var spinDownBrush = new SolidBrush(Color.Blue))
                {
                    for (int t = MinTemp; t <= MaxTemp; t += 10)
                    {
                        int x = TempToX(plot, t);
                        e.Graphics.DrawLine(gridPen, x, plot.Top, x, plot.Bottom);
                        e.Graphics.DrawString(t.ToString(CultureInfo.InvariantCulture) + "°C", Font, textBrush, x - 16, plot.Bottom + 4);
                    }

                    for (int r = MinRpm; r <= MaxRpm; r += 1000)
                    {
                        int y = RpmToY(plot, r);
                        e.Graphics.DrawLine(gridPen, plot.Left, y, plot.Right, y);
                        e.Graphics.DrawString(r.ToString(CultureInfo.InvariantCulture), Font, textBrush, 4, y - 8);
                    }

                    e.Graphics.DrawRectangle(axisPen, plot);

                    DrawPaddingBars(e.Graphics, plot, paddingPen, paddingBrush, textBrush);
                    DrawTelemetryMarker(e.Graphics, plot, _displayCurrentTemp, currentPen, currentBrush, textBrush, "Current", 0);
                    DrawTelemetryMarker(e.Graphics, plot, _displaySpinUpTemp, spinUpPen, spinUpBrush, textBrush, "Spin up", 1);
                    DrawTelemetryMarker(e.Graphics, plot, _displaySpinDownTemp, spinDownPen, spinDownBrush, textBrush, "Spin down", 2);

                    if (_points.Count >= 2)
                    {
                        for (int i = 0; i < _points.Count - 1; i++)
                        {
                            Point a = PointFromPoint(plot, _points[i]);
                            Point b = PointFromPoint(plot, _points[i + 1]);
                            e.Graphics.DrawLine(linePen, a, b);
                        }
                    }

                    for (int i = 0; i < _points.Count; i++)
                    {
                        Point p = PointFromPoint(plot, _points[i]);
                        Rectangle ellipse = new Rectangle(p.X - PointRadius, p.Y - PointRadius, PointRadius * 2, PointRadius * 2);
                        if (i == _selectedIndex)
                        {
                            e.Graphics.FillEllipse(selectedBrush, ellipse);
                            e.Graphics.DrawEllipse(Pens.DarkRed, ellipse);
                        }
                        else
                        {
                            e.Graphics.FillEllipse(pointBrush, ellipse);
                            e.Graphics.DrawEllipse(Pens.DarkGreen, ellipse);
                        }

                        string label = string.Format(CultureInfo.InvariantCulture, "{0}°C / {1} RPM", _points[i].Temp, _points[i].Rpm);
                        e.Graphics.DrawString(label, Font, textBrush, p.X + 10, p.Y - 10);
                    }

                    DrawLegend(e.Graphics, textBrush, paddingBrush, currentBrush, spinUpBrush, spinDownBrush);
                }
            }

            private void AnimationTimerOnTick(object sender, EventArgs e)
            {
                bool changed = false;
                changed |= StepTowards(ref _displayCurrentTemp, _targetCurrentTemp, 0.25);
                changed |= StepTowards(ref _displaySpinUpTemp, _targetSpinUpTemp, 0.25);
                changed |= StepTowards(ref _displaySpinDownTemp, _targetSpinDownTemp, 0.25);

                if (!changed)
                {
                    _animationTimer.Stop();
                    return;
                }

                Invalidate();
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                Focus();

                Rectangle plot = GetPlotArea();
                if (!plot.Contains(e.Location))
                {
                    return;
                }

                int index = HitTest(e.Location);
                if (index >= 0)
                {
                    _selectedIndex = index;
                    _dragging = true;
                    Capture = true;
                    Invalidate();
                    return;
                }

                if (e.Button == MouseButtons.Left && e.Clicks >= 2)
                {
                    AddPointAt(e.Location);
                    return;
                }

                _selectedIndex = -1;
                Invalidate();
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (!_dragging || _selectedIndex < 0 || _selectedIndex >= _points.Count)
                {
                    return;
                }

                Rectangle plot = GetPlotArea();
                if (!plot.Contains(e.Location))
                {
                    return;
                }

                GraphPoint point = _points[_selectedIndex];
                point.Temp = ClampTemp(XToTemp(plot, e.X));
                point.Rpm = ClampRpm(YToRpm(plot, e.Y));
                SortAndNormalize();
                PointsChanged?.Invoke(this, EventArgs.Empty);
                Invalidate();
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                _dragging = false;
                Capture = false;
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                base.OnKeyDown(e);
                if (e.KeyCode == Keys.Delete && _selectedIndex >= 0 && _selectedIndex < _points.Count)
                {
                    _points.RemoveAt(_selectedIndex);
                    _selectedIndex = Math.Min(_selectedIndex, _points.Count - 1);
                    SortAndNormalize();
                    PointsChanged?.Invoke(this, EventArgs.Empty);
                    Invalidate();
                }
            }

            private void AddPointAt(Point location)
            {
                Rectangle plot = GetPlotArea();
                if (!plot.Contains(location))
                {
                    return;
                }

                _points.Add(new GraphPoint
                {
                    Temp = ClampTemp(XToTemp(plot, location.X)),
                    Rpm = ClampRpm(YToRpm(plot, location.Y))
                });
                SortAndNormalize();
                _selectedIndex = HitTest(location);
                PointsChanged?.Invoke(this, EventArgs.Empty);
                Invalidate();
            }

            private int HitTest(Point location)
            {
                Rectangle plot = GetPlotArea();
                for (int i = 0; i < _points.Count; i++)
                {
                    Point p = PointFromPoint(plot, _points[i]);
                    int dx = p.X - location.X;
                    int dy = p.Y - location.Y;
                    if ((dx * dx) + (dy * dy) <= (PointRadius + 4) * (PointRadius + 4))
                    {
                        return i;
                    }
                }

                return -1;
            }

            private void SortAndNormalize()
            {
                _points.Sort((a, b) => a.Temp.CompareTo(b.Temp));

                for (int i = 1; i < _points.Count; i++)
                {
                    if (_points[i].Temp <= _points[i - 1].Temp)
                    {
                        _points[i].Temp = Math.Min(MaxTemp, _points[i - 1].Temp + 1);
                    }
                }

                for (int i = 0; i < _points.Count; i++)
                {
                    _points[i].Temp = ClampTemp(_points[i].Temp);
                    _points[i].Rpm = ClampRpm(_points[i].Rpm);
                }
            }

            private void DrawPaddingBars(Graphics graphics, Rectangle plot, Pen barPen, Brush barBrush, Brush textBrush)
            {
                int padding = Math.Max(0, _breakpointPaddingCelsius);
                if (padding <= 0)
                {
                    return;
                }

                for (int i = 0; i < _points.Count; i++)
                {
                    GraphPoint point = _points[i];
                    int leftTemp = ClampTemp(point.Temp - padding);
                    int rightTemp = ClampTemp(point.Temp + padding);
                    Point left = new Point(TempToX(plot, leftTemp), RpmToY(plot, point.Rpm));
                    Point right = new Point(TempToX(plot, rightTemp), RpmToY(plot, point.Rpm));
                    Point center = PointFromPoint(plot, point);

                    graphics.DrawLine(barPen, left, right);
                    DrawVerticalCap(graphics, barPen, left, 6);
                    DrawVerticalCap(graphics, barPen, right, 6);
                    graphics.FillEllipse(barBrush, center.X - 3, center.Y - 3, 6, 6);
                    graphics.DrawString("|--o---|", Font, textBrush, center.X + 8, center.Y - 18);
                }
            }

            private void DrawTelemetryMarker(Graphics graphics, Rectangle plot, double? temp, Pen linePen, Brush dotBrush, Brush textBrush, string label, int stackIndex)
            {
                if (!temp.HasValue)
                {
                    return;
                }

                int clampedTemp = ClampTemp(temp.Value);
                int x = TempToX(plot, clampedTemp);
                int labelY = plot.Top + 4 + (stackIndex * 16);
                int dotY = plot.Top + 14 + (stackIndex * 10);

                graphics.DrawLine(linePen, x, plot.Top, x, plot.Bottom);
                graphics.FillEllipse(dotBrush, x - 5, dotY - 5, 10, 10);
                graphics.DrawEllipse(Pens.Black, x - 5, dotY - 5, 10, 10);
                graphics.DrawString(label + ": " + clampedTemp.ToString(CultureInfo.InvariantCulture) + "°C", Font, textBrush, x + 8, labelY);
            }

            private void DrawLegend(Graphics graphics, Brush textBrush, Brush paddingBrush, Brush currentBrush, Brush spinUpBrush, Brush spinDownBrush)
            {
                int x = 54;
                int y = 6;
                DrawLegendItem(graphics, textBrush, paddingBrush, "|--o---| padding", ref x, y);
                DrawLegendItem(graphics, textBrush, currentBrush, "Current temp", ref x, y);
                DrawLegendItem(graphics, textBrush, spinUpBrush, "Spin up avg", ref x, y);
                DrawLegendItem(graphics, textBrush, spinDownBrush, "Spin down avg", ref x, y);
            }

            private void DrawLegendItem(Graphics graphics, Brush textBrush, Brush brush, string text, ref int x, int y)
            {
                graphics.FillEllipse(brush, x, y + 3, 8, 8);
                graphics.DrawString(text, Font, textBrush, x + 12, y);
                x += (int)Math.Ceiling(graphics.MeasureString(text, Font).Width) + 32;
            }

            private static void DrawVerticalCap(Graphics graphics, Pen pen, Point point, int halfHeight)
            {
                graphics.DrawLine(pen, point.X, point.Y - halfHeight, point.X, point.Y + halfHeight);
            }

            private static bool StepTowards(ref double? current, double? target, double rate)
            {
                if (!target.HasValue)
                {
                    if (current.HasValue)
                    {
                        current = null;
                        return true;
                    }

                    return false;
                }

                if (!current.HasValue)
                {
                    current = target;
                    return true;
                }

                double delta = target.Value - current.Value;
                if (Math.Abs(delta) < 0.05)
                {
                    if (Math.Abs(current.Value - target.Value) > 0)
                    {
                        current = target;
                        return true;
                    }

                    return false;
                }

                current = current.Value + (delta * rate);
                return true;
            }

            private Rectangle GetPlotArea()
            {
                return new Rectangle(48, 28, Math.Max(1, ClientSize.Width - 70), Math.Max(1, ClientSize.Height - 64));
            }

            private static int ClampTemp(double temp)
            {
                return ClampTemp((int)Math.Round(temp));
            }

            private static int ClampTemp(int temp)
            {
                if (temp < MinTemp) return MinTemp;
                if (temp > MaxTemp) return MaxTemp;
                return temp;
            }

            private static int ClampRpm(int rpm)
            {
                rpm = (int)(Math.Round(rpm / 100.0) * 100.0);
                if (rpm < MinRpm) return MinRpm;
                if (rpm > MaxRpm) return MaxRpm;
                return rpm;
            }

            private static int TempToX(Rectangle plot, int temp)
            {
                double normalized = (temp - MinTemp) / (double)(MaxTemp - MinTemp);
                return plot.Left + (int)Math.Round(normalized * plot.Width);
            }

            private static int RpmToY(Rectangle plot, int rpm)
            {
                double normalized = (rpm - MinRpm) / (double)(MaxRpm - MinRpm);
                return plot.Bottom - (int)Math.Round(normalized * plot.Height);
            }

            private static int XToTemp(Rectangle plot, int x)
            {
                double normalized = (x - plot.Left) / (double)plot.Width;
                return ClampTemp((int)Math.Round(MinTemp + normalized * (MaxTemp - MinTemp)));
            }

            private static int YToRpm(Rectangle plot, int y)
            {
                double normalized = (plot.Bottom - y) / (double)plot.Height;
                return (int)Math.Round(MinRpm + normalized * (MaxRpm - MinRpm));
            }

            private static Point PointFromPoint(Rectangle plot, GraphPoint point)
            {
                return new Point(TempToX(plot, point.Temp), RpmToY(plot, point.Rpm));
            }
        }
    }
}
