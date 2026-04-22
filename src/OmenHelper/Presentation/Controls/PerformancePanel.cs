using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using Hp.Bridge.Client.SDKs.PerformanceControl.Enums;
using OmenHelper.Application.State;
using OmenHelper.Domain.Fan;
using OmenHelper.Domain.Firmware;

namespace OmenHelper.Presentation.Controls;

internal sealed class PerformancePanel : UserControl
{
    private readonly Label _performanceSummaryLabel = new Label();
    private readonly Label _performanceMetricsLabel = new Label();
    private readonly Label _temperatureLabel = new Label();
    private readonly Label _fanCurveStatusLabel = new Label();
    private readonly ComboBox _batteryModeCombo = new ComboBox();
    private readonly ComboBox _pluggedModeCombo = new ComboBox();
    private readonly Label _powerSourceLabel = new Label();
    private readonly CheckBox _maxFanCheckBox = new CheckBox();
    private readonly ComboBox _fanSpeedCombo = new ComboBox();
    private readonly CheckBox _fanCurvesEnabledCheckBox = new CheckBox();
    private readonly CheckBox _gpuLinkedCheckBox = new CheckBox();
    private readonly NumericUpDown _hysteresisRiseNumeric = new NumericUpDown();
    private readonly NumericUpDown _hysteresisDropNumeric = new NumericUpDown();
    private readonly FanCurveChartControl _cpuCurveChart = new FanCurveChartControl();
    private readonly FanCurveChartControl _gpuCurveChart = new FanCurveChartControl();
    private readonly FanCurveChartControl _chassisCurveChart = new FanCurveChartControl();
    private TableLayoutPanel _chartRow;
    private readonly Dictionary<PerformanceMode, Button> _modeButtons = new Dictionary<PerformanceMode, Button>();
    private bool _suppressPowerModeUiUpdates;
    private bool _suppressMaxFanUiUpdates;
    private bool _suppressFanMinimumUiUpdates;
    private bool _suppressCurveUiUpdates;
    private bool _suppressHysteresisUiUpdates;

    public PerformancePanel()
    {
        Dock = DockStyle.Top;
        AutoSize = true;
        Margin = new Padding(0, 0, 0, 12);

        Controls.Add(BuildPerformanceGroup());
    }

    public event EventHandler<PerformanceMode> PerformanceModeRequested;
    public event EventHandler<bool> MaxFanRequested;
    public event EventHandler<int?> FanMinimumRequested;
    public event EventHandler<PerformanceMode?> BatteryPreferenceChanged;
    public event EventHandler<PerformanceMode?> PluggedPreferenceChanged;
    public event EventHandler PowerModeSelectionsChanged;
    public event EventHandler<bool> FanCurveRuntimeEnabledChanged;
    public event EventHandler<bool> GpuCurveLinkedChanged;
    public event EventHandler<FanCurveHysteresisChangedEventArgs> FanCurveHysteresisChanged;
    public event EventHandler<FanCurveProfile> CpuCurveCommitted;
    public event EventHandler<FanCurveProfile> GpuCurveCommitted;
    public event EventHandler<FanCurveProfile> ChassisCurveCommitted;

    public void ApplyState(PerformanceControlState state)
    {
        PerformanceControlState safeState = state ?? new PerformanceControlState();

        foreach (KeyValuePair<PerformanceMode, Button> pair in _modeButtons)
        {
            pair.Value.BackColor = Color.Gainsboro;
        }

        bool biosStateReady = safeState.Initialized && safeState.Available && (safeState.CurrentModeKnown || safeState.CurrentModeIsInferred);
        PerformanceMode? selectedMode;
        if (biosStateReady && TryParseDisplayedPerformanceMode(safeState.CurrentMode, out selectedMode) && selectedMode.HasValue && _modeButtons.TryGetValue(selectedMode.Value, out Button activeButton))
        {
            activeButton.BackColor = Color.LightGreen;
        }

        UpdateMaxFanCheckBoxState(safeState);
        UpdateFanSpeedComboState(safeState);
        UpdateCurveUiState(safeState);
        UpdateSummaryLabels(safeState);
        RefreshPowerSourceLabel();
    }

    public void LoadPowerModePreferences(PerformanceMode? batteryMode, PerformanceMode? pluggedInMode)
    {
        try
        {
            _suppressPowerModeUiUpdates = true;
            SetComboSelection(_batteryModeCombo, FormatPowerModeChoice(batteryMode));
            SetComboSelection(_pluggedModeCombo, FormatPowerModeChoice(pluggedInMode));
        }
        finally
        {
            _suppressPowerModeUiUpdates = false;
        }

        RefreshPowerSourceLabel();
    }

    public void RefreshPowerSourceLabel()
    {
        PowerLineStatus powerLineStatus = SystemInformation.PowerStatus.PowerLineStatus;
        string currentSource = powerLineStatus == PowerLineStatus.Online
            ? "Plugged in"
            : powerLineStatus == PowerLineStatus.Offline
                ? "On battery"
                : "Unknown";

        _powerSourceLabel.Text =
            "Current power source: " + currentSource +
            " | Battery target: " + GetComboSelectionText(_batteryModeCombo) +
            " | Plugged-in target: " + GetComboSelectionText(_pluggedModeCombo);
    }

    private GroupBox BuildPerformanceGroup()
    {
        GroupBox performanceGroup = new GroupBox
        {
            Text = "Performance Mode",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(12),
            Margin = new Padding(0, 0, 0, 12)
        };

        TableLayoutPanel layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 10
        };

        TableLayoutPanel summaryRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 8)
        };
        summaryRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55F));
        summaryRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45F));
        _performanceSummaryLabel.AutoSize = true;
        _performanceMetricsLabel.AutoSize = true;
        _performanceMetricsLabel.Anchor = AnchorStyles.Right;
        summaryRow.Controls.Add(_performanceSummaryLabel, 0, 0);
        summaryRow.Controls.Add(_performanceMetricsLabel, 1, 0);

        _temperatureLabel.AutoSize = true;
        _temperatureLabel.Margin = new Padding(0, 0, 0, 8);
        _fanCurveStatusLabel.AutoSize = true;
        _fanCurveStatusLabel.Margin = new Padding(0, 0, 0, 8);

        FlowLayoutPanel modePanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = true,
            Margin = new Padding(0, 0, 0, 8)
        };

        AddModeButton(modePanel, PerformanceMode.Eco, "[ Eco ]");
        AddModeButton(modePanel, PerformanceMode.Default, "[ Balanced ]");
        AddModeButton(modePanel, PerformanceMode.Performance, "[ Performance ]");
        AddModeButton(modePanel, PerformanceMode.Extreme, "[ Unleashed ]");

        FlowLayoutPanel thermalPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = true,
            Margin = new Padding(0, 0, 0, 8)
        };

        _maxFanCheckBox.Text = "Max fan thermal mode";
        _maxFanCheckBox.AutoSize = true;
        _maxFanCheckBox.Margin = new Padding(0, 6, 16, 6);
        _maxFanCheckBox.CheckedChanged += (_, __) =>
        {
            if (_suppressMaxFanUiUpdates)
            {
                return;
            }

            MaxFanRequested?.Invoke(this, _maxFanCheckBox.Checked);
        };
        thermalPanel.Controls.Add(_maxFanCheckBox);

        _fanCurvesEnabledCheckBox.Text = "Enable custom fan curves";
        _fanCurvesEnabledCheckBox.AutoSize = true;
        _fanCurvesEnabledCheckBox.Margin = new Padding(0, 6, 16, 6);
        _fanCurvesEnabledCheckBox.CheckedChanged += (_, __) =>
        {
            if (_suppressCurveUiUpdates)
            {
                return;
            }

            FanCurveRuntimeEnabledChanged?.Invoke(this, _fanCurvesEnabledCheckBox.Checked);
        };
        thermalPanel.Controls.Add(_fanCurvesEnabledCheckBox);

        _gpuLinkedCheckBox.Text = "Link GPU to CPU (-200 RPM)";
        _gpuLinkedCheckBox.AutoSize = true;
        _gpuLinkedCheckBox.Margin = new Padding(0, 6, 0, 6);
        _gpuLinkedCheckBox.CheckedChanged += (_, __) =>
        {
            if (_suppressCurveUiUpdates)
            {
                return;
            }

            GpuCurveLinkedChanged?.Invoke(this, _gpuLinkedCheckBox.Checked);
        };
        thermalPanel.Controls.Add(_gpuLinkedCheckBox);

        _chartRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 8)
        };
        _chartRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));
        _chartRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));
        _chartRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34F));

        _cpuCurveChart.Dock = DockStyle.Fill;
        _cpuCurveChart.MinimumSize = new Size(220, 180);
        _cpuCurveChart.Title = "CPU Curve";
        _cpuCurveChart.CurveEditCommitted += (_, profile) => CpuCurveCommitted?.Invoke(this, profile);

        _gpuCurveChart.Dock = DockStyle.Fill;
        _gpuCurveChart.MinimumSize = new Size(220, 180);
        _gpuCurveChart.Title = "GPU Curve";
        _gpuCurveChart.CurveEditCommitted += (_, profile) => GpuCurveCommitted?.Invoke(this, profile);

        _chassisCurveChart.Dock = DockStyle.Fill;
        _chassisCurveChart.MinimumSize = new Size(220, 180);
        _chassisCurveChart.Title = "Chassis Floor";
        _chassisCurveChart.CurveEditCommitted += (_, profile) => ChassisCurveCommitted?.Invoke(this, profile);

        _chartRow.Controls.Add(_cpuCurveChart, 0, 0);
        _chartRow.Controls.Add(_gpuCurveChart, 1, 0);
        _chartRow.Controls.Add(_chassisCurveChart, 2, 0);

        TableLayoutPanel hysteresisRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 4,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 8)
        };
        hysteresisRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        hysteresisRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        hysteresisRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        hysteresisRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        Label hysteresisRiseLabel = new Label
        {
            Text = "Raise +°C:",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 6, 8, 6)
        };

        Label hysteresisDropLabel = new Label
        {
            Text = "Drop -°C:",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(12, 6, 8, 6)
        };

        ConfigureHysteresisNumeric(_hysteresisRiseNumeric);
        ConfigureHysteresisNumeric(_hysteresisDropNumeric);
        _hysteresisRiseNumeric.Margin = new Padding(0, 4, 0, 4);
        _hysteresisDropNumeric.Margin = new Padding(0, 4, 0, 4);
        _hysteresisRiseNumeric.ValueChanged += (_, __) => OnHysteresisChanged();
        _hysteresisDropNumeric.ValueChanged += (_, __) => OnHysteresisChanged();

        hysteresisRow.Controls.Add(hysteresisRiseLabel, 0, 0);
        hysteresisRow.Controls.Add(_hysteresisRiseNumeric, 1, 0);
        hysteresisRow.Controls.Add(hysteresisDropLabel, 2, 0);
        hysteresisRow.Controls.Add(_hysteresisDropNumeric, 3, 0);

        TableLayoutPanel fanRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 8)
        };
        fanRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        fanRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        Label fanLabel = new Label
        {
            Text = "Debug fan speed:",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 6, 8, 6)
        };

        _fanSpeedCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _fanSpeedCombo.Width = 180;
        _fanSpeedCombo.Items.AddRange(new object[]
        {
            FormatFanSpeedChoice(null),
            FormatFanSpeedChoice(0),
            FormatFanSpeedChoice(1500),
            FormatFanSpeedChoice(2500),
            FormatFanSpeedChoice(3500),
            FormatFanSpeedChoice(4500),
            FormatFanSpeedChoice(5500),
            FormatFanSpeedChoice(6500)
        });
        _fanSpeedCombo.SelectedIndexChanged += (_, __) =>
        {
            if (_suppressFanMinimumUiUpdates)
            {
                return;
            }

            FanMinimumRequested?.Invoke(this, ParseSelectedFanMinimumRpm(_fanSpeedCombo));
        };

        fanRow.Controls.Add(fanLabel, 0, 0);
        fanRow.Controls.Add(_fanSpeedCombo, 1, 0);

        TableLayoutPanel powerRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 4,
            RowCount = 1
        };
        powerRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        powerRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        powerRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        powerRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));

        Label batteryLabel = new Label { Text = "On battery:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 6) };
        Label pluggedLabel = new Label { Text = "Plugged in:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 6) };

        _batteryModeCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _batteryModeCombo.Width = 180;
        _batteryModeCombo.Items.AddRange(new object[] { "None", "Eco", "Balanced", "Performance", "Unleashed" });
        _batteryModeCombo.SelectedIndexChanged += (_, __) => OnPowerModeSelectionChanged();

        _pluggedModeCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _pluggedModeCombo.Width = 180;
        _pluggedModeCombo.Items.AddRange(new object[] { "None", "Eco", "Balanced", "Performance", "Unleashed" });
        _pluggedModeCombo.SelectedIndexChanged += (_, __) => OnPowerModeSelectionChanged();

        powerRow.Controls.Add(batteryLabel, 0, 0);
        powerRow.Controls.Add(_batteryModeCombo, 1, 0);
        powerRow.Controls.Add(pluggedLabel, 2, 0);
        powerRow.Controls.Add(_pluggedModeCombo, 3, 0);

        _powerSourceLabel.AutoSize = true;
        _powerSourceLabel.Margin = new Padding(0, 2, 0, 0);

        layout.Controls.Add(summaryRow, 0, 0);
        layout.Controls.Add(_temperatureLabel, 0, 1);
        layout.Controls.Add(_fanCurveStatusLabel, 0, 2);
        layout.Controls.Add(modePanel, 0, 3);
        layout.Controls.Add(thermalPanel, 0, 4);
        layout.Controls.Add(_chartRow, 0, 5);
        layout.Controls.Add(hysteresisRow, 0, 6);
        layout.Controls.Add(fanRow, 0, 7);
        layout.Controls.Add(powerRow, 0, 8);
        layout.Controls.Add(_powerSourceLabel, 0, 9);
        performanceGroup.Controls.Add(layout);
        return performanceGroup;
    }

    private void AddModeButton(FlowLayoutPanel panel, PerformanceMode mode, string title)
    {
        Button button = new Button
        {
            Text = title,
            AutoSize = true,
            Margin = new Padding(0, 0, 12, 12),
            Padding = new Padding(10, 4, 10, 4),
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false
        };
        button.Click += (_, __) => PerformanceModeRequested?.Invoke(this, mode);
        panel.Controls.Add(button);
        _modeButtons[mode] = button;
    }

    private void OnPowerModeSelectionChanged()
    {
        if (_suppressPowerModeUiUpdates)
        {
            return;
        }

        if (TryParseSelectedPowerMode(_batteryModeCombo, out PerformanceMode? batteryMode))
        {
            BatteryPreferenceChanged?.Invoke(this, batteryMode);
        }

        if (TryParseSelectedPowerMode(_pluggedModeCombo, out PerformanceMode? pluggedMode))
        {
            PluggedPreferenceChanged?.Invoke(this, pluggedMode);
        }

        RefreshPowerSourceLabel();
        PowerModeSelectionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateSummaryLabels(PerformanceControlState state)
    {
        string modeText = FormatDisplayedPerformanceMode(state);
        string fanMinimumText = state.Initialized && state.Available ? state.CurrentFanMinimumRpm.ToString("N0") + "RPM" : "<unavailable>";
        string fanMinimumSuffix = state.FanMinimumOverrideRpm.HasValue ? " (debug override)" : " (mode default)";
        string fanRpmText = !string.IsNullOrWhiteSpace(state.CurrentFanRpmSummary) ? state.CurrentFanRpmSummary : "<unavailable>";
        string cpuTempText = FormatTemperature(state.CpuTemperatureC);
        string gpuTempText = FormatTemperature(state.GpuTemperatureC);
        string chassisTempText = FormatTemperature(state.ChassisTemperatureC);
        string avgCpuText = FormatTemperature(state.AveragedCpuTemperatureC);
        string avgGpuText = FormatTemperature(state.AveragedGpuTemperatureC);
        string avgChassisText = FormatTemperature(state.AveragedChassisTemperatureC);

        _performanceSummaryLabel.Text = "Mode: " + modeText;
        _performanceMetricsLabel.Text = "Thermal: " + state.CurrentThermalMode + " | Legacy fan: " + state.CurrentLegacyFanMode + " | Fan RPM: " + fanRpmText + " | Fan minimum: " + fanMinimumText + fanMinimumSuffix;
        _temperatureLabel.Text = "Temps: CPU " + cpuTempText + " | GPU " + gpuTempText + " | Chassis " + chassisTempText + " | Avg: CPU " + avgCpuText + " / GPU " + avgGpuText + " / Chassis " + avgChassisText;
        _fanCurveStatusLabel.Text = "Fan curves: " + (state.FanCurveRuntimeEnabled ? "Enabled" : "Disabled") +
            " | Hysteresis: +" + state.FanCurveHysteresisRiseDeltaC + "°C / -" + state.FanCurveHysteresisDropDeltaC + "°C" +
            " | Desired CPU/GPU: " + state.CurveDesiredCpuRpm + "/" + state.CurveDesiredGpuRpm + " RPM" +
            " | Applied CPU/GPU: " + state.CurveAppliedCpuRpm + "/" + state.CurveAppliedGpuRpm + " RPM" +
            " | Chassis floor used: " + (state.CurveChassisOverrideUsed ? "Yes" : "No") +
            " | Last write: " + (string.IsNullOrWhiteSpace(state.LastCurveWriteReason) ? "<none>" : state.LastCurveWriteReason);
    }

    private void UpdateMaxFanCheckBoxState(PerformanceControlState state)
    {
        try
        {
            _suppressMaxFanUiUpdates = true;
            _maxFanCheckBox.Enabled = state.Initialized && state.Available;
            _maxFanCheckBox.Checked = state.MaxFanEnabled;
        }
        finally
        {
            _suppressMaxFanUiUpdates = false;
        }
    }

    private void UpdateFanSpeedComboState(PerformanceControlState state)
    {
        try
        {
            _suppressFanMinimumUiUpdates = true;
            _fanSpeedCombo.Enabled = state.Initialized && state.Available;
            SetFanSpeedSelection(state.FanMinimumOverrideRpm);
        }
        finally
        {
            _suppressFanMinimumUiUpdates = false;
        }
    }

    private void UpdateCurveUiState(PerformanceControlState state)
    {
        try
        {
            _suppressCurveUiUpdates = true;
            bool enabled = state.Initialized && state.Available;
            _fanCurvesEnabledCheckBox.Enabled = enabled;
            _fanCurvesEnabledCheckBox.Checked = state.FanCurveRuntimeEnabled;
            _gpuLinkedCheckBox.Enabled = enabled;
            _gpuLinkedCheckBox.Checked = state.GpuCurveLinked;
            UpdateChartLayout(state.GpuCurveLinked);
            _hysteresisRiseNumeric.Enabled = enabled;
            _hysteresisDropNumeric.Enabled = enabled;
            SetHysteresisNumericValue(_hysteresisRiseNumeric, state.FanCurveHysteresisRiseDeltaC);
            SetHysteresisNumericValue(_hysteresisDropNumeric, state.FanCurveHysteresisDropDeltaC);
            if (!_cpuCurveChart.IsDraggingPoint)
            {
                _cpuCurveChart.Profile = state.ActiveCpuCurve ?? _cpuCurveChart.Profile;
            }

            if (!_gpuCurveChart.IsDraggingPoint)
            {
                _gpuCurveChart.Profile = state.ActiveGpuCurve ?? _gpuCurveChart.Profile;
            }

            if (!_chassisCurveChart.IsDraggingPoint)
            {
                _chassisCurveChart.Profile = state.ActiveChassisCurve ?? _chassisCurveChart.Profile;
            }
            _cpuCurveChart.HysteresisRiseDeltaC = state.FanCurveHysteresisRiseDeltaC;
            _cpuCurveChart.HysteresisDropDeltaC = state.FanCurveHysteresisDropDeltaC;
            _gpuCurveChart.HysteresisRiseDeltaC = state.FanCurveHysteresisRiseDeltaC;
            _gpuCurveChart.HysteresisDropDeltaC = state.FanCurveHysteresisDropDeltaC;
            _chassisCurveChart.HysteresisRiseDeltaC = state.FanCurveHysteresisRiseDeltaC;
            _chassisCurveChart.HysteresisDropDeltaC = state.FanCurveHysteresisDropDeltaC;
            _cpuCurveChart.HysteresisAnchorTemperatureC = state.CpuHysteresisAnchorTemperatureC;
            _gpuCurveChart.HysteresisAnchorTemperatureC = state.GpuHysteresisAnchorTemperatureC;
            _chassisCurveChart.HysteresisAnchorTemperatureC = state.ChassisHysteresisAnchorTemperatureC;
            _cpuCurveChart.CurrentTemperatureC = state.AveragedCpuTemperatureC ?? state.CpuTemperatureC;
            _cpuCurveChart.CurrentFanRpm = state.CpuFanRpm;
            _gpuCurveChart.CurrentTemperatureC = state.AveragedGpuTemperatureC ?? state.GpuTemperatureC;
            _gpuCurveChart.CurrentFanRpm = state.GpuFanRpm;
            _chassisCurveChart.CurrentTemperatureC = state.AveragedChassisTemperatureC ?? state.ChassisTemperatureC;
            _chassisCurveChart.CurrentFanRpm = GetChassisReferenceFanRpm(state);
            _cpuCurveChart.ReadOnlyCurve = !enabled || !state.FanCurveRuntimeEnabled;
            _gpuCurveChart.ReadOnlyCurve = !enabled || !state.FanCurveRuntimeEnabled || state.GpuCurveLinked;
            _chassisCurveChart.ReadOnlyCurve = !enabled || !state.FanCurveRuntimeEnabled;
        }
        finally
        {
            _suppressCurveUiUpdates = false;
        }
    }

    private void UpdateChartLayout(bool gpuLinked)
    {
        if (_chartRow == null)
        {
            return;
        }

        _gpuCurveChart.Visible = !gpuLinked;
        _chartRow.ColumnStyles[0].SizeType = SizeType.Percent;
        _chartRow.ColumnStyles[1].SizeType = gpuLinked ? SizeType.Absolute : SizeType.Percent;
        _chartRow.ColumnStyles[2].SizeType = SizeType.Percent;

        _chartRow.ColumnStyles[0].Width = gpuLinked ? 50F : 33.33F;
        _chartRow.ColumnStyles[1].Width = gpuLinked ? 0F : 33.33F;
        _chartRow.ColumnStyles[2].Width = gpuLinked ? 50F : 33.34F;
    }

    private void SetFanSpeedSelection(int? rpm)
    {
        string target = FormatFanSpeedChoice(rpm);
        for (int i = 0; i < _fanSpeedCombo.Items.Count; i++)
        {
            if (string.Equals(Convert.ToString(_fanSpeedCombo.Items[i]), target, StringComparison.OrdinalIgnoreCase))
            {
                _fanSpeedCombo.SelectedIndex = i;
                return;
            }
        }

        _fanSpeedCombo.SelectedIndex = 0;
    }

    private static int? ParseSelectedFanMinimumRpm(ComboBox comboBox)
    {
        string value = Convert.ToString(comboBox.SelectedItem);
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "Mode default", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (value.EndsWith(" RPM", StringComparison.OrdinalIgnoreCase))
        {
            value = value.Substring(0, value.Length - 4).Trim();
        }

        int parsed;
        if (int.TryParse(value, NumberStyles.Integer | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string FormatFanSpeedChoice(int? rpm)
    {
        return rpm.HasValue ? rpm.Value.ToString("N0") + " RPM" : "Mode default";
    }

    private static bool TryParseSelectedPowerMode(ComboBox comboBox, out PerformanceMode? mode)
    {
        string value = Convert.ToString(comboBox.SelectedItem);
        return TryParseDisplayedPerformanceMode(value, out mode);
    }

    private static string FormatPowerModeChoice(PerformanceMode? mode)
    {
        return mode.HasValue ? PerformanceModeFirmwareMap.FormatDisplayName(mode.Value) : "None";
    }

    private static string FormatDisplayedPerformanceMode(PerformanceControlState state)
    {
        if (state.CurrentModeKnown || state.CurrentModeIsInferred)
        {
            return state.CurrentModeIsInferred && !state.CurrentModeKnown
                ? state.CurrentMode + " (last requested)"
                : state.CurrentMode;
        }

        return "Unknown (readback unavailable)";
    }

    private static bool TryParseDisplayedPerformanceMode(string currentMode, out PerformanceMode? mode)
    {
        if (string.Equals(currentMode, "None", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(currentMode))
        {
            mode = null;
            return true;
        }

        PerformanceMode parsed;
        if (PerformanceModeFirmwareMap.TryParseDisplayName(currentMode, out parsed))
        {
            mode = parsed;
            return true;
        }

        mode = null;
        return false;
    }

    private static string GetComboSelectionText(ComboBox comboBox)
    {
        return comboBox.SelectedItem != null ? Convert.ToString(comboBox.SelectedItem) : "<none>";
    }

    private void OnHysteresisChanged()
    {
        if (_suppressHysteresisUiUpdates)
        {
            return;
        }

        FanCurveHysteresisChanged?.Invoke(this, new FanCurveHysteresisChangedEventArgs((int)_hysteresisRiseNumeric.Value, (int)_hysteresisDropNumeric.Value));
    }

    private static string FormatTemperature(double? temperatureC)
    {
        return temperatureC.HasValue ? temperatureC.Value.ToString("0.0") + "°C" : "<unavailable>";
    }

    private static void ConfigureHysteresisNumeric(NumericUpDown numericUpDown)
    {
        numericUpDown.Minimum = 0;
        numericUpDown.Maximum = 30;
        numericUpDown.DecimalPlaces = 0;
        numericUpDown.Increment = 1;
        numericUpDown.Width = 64;
        numericUpDown.TextAlign = HorizontalAlignment.Right;
        numericUpDown.Value = 5;
    }

    private void SetHysteresisNumericValue(NumericUpDown numericUpDown, int value)
    {
        try
        {
            _suppressHysteresisUiUpdates = true;
            decimal clamped = Math.Max(numericUpDown.Minimum, Math.Min(numericUpDown.Maximum, value));
            if (numericUpDown.Value != clamped)
            {
                numericUpDown.Value = clamped;
            }
        }
        finally
        {
            _suppressHysteresisUiUpdates = false;
        }
    }

    private static void SetComboSelection(ComboBox comboBox, string value)
    {
        for (int i = 0; i < comboBox.Items.Count; i++)
        {
            if (string.Equals(Convert.ToString(comboBox.Items[i]), value, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedIndex = i;
                return;
            }
        }

        comboBox.SelectedIndex = comboBox.Items.Count > 0 ? 0 : -1;
    }

    private static int? GetChassisReferenceFanRpm(PerformanceControlState state)
    {
        if (state.CpuFanRpm.HasValue && state.GpuFanRpm.HasValue)
        {
            return Math.Max(state.CpuFanRpm.Value, state.GpuFanRpm.Value);
        }

        if (state.CpuFanRpm.HasValue)
        {
            return state.CpuFanRpm.Value;
        }

        if (state.GpuFanRpm.HasValue)
        {
            return state.GpuFanRpm.Value;
        }

        return null;
    }
}
