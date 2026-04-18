using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using Hp.Bridge.Client.SDKs.PerformanceControl.Enums;
using HP.Omen.Core.Common.PowerControl.Enum;
using HP.Omen.Core.Model.DataStructure.Modules.GraphicsSwitcher.Enums;
using OmenHelper.Application.State;
using OmenHelper.Domain.Firmware;
using OmenHelper.Domain.Graphics;

namespace OmenHelper.Presentation.Controls;

internal sealed class PerformancePanel : UserControl
{
    private readonly Label _performanceSummaryLabel = new Label();
    private readonly Label _performanceMetricsLabel = new Label();
    private readonly ComboBox _batteryModeCombo = new ComboBox();
    private readonly ComboBox _pluggedModeCombo = new ComboBox();
    private readonly Label _powerSourceLabel = new Label();
    private readonly CheckBox _maxFanCheckBox = new CheckBox();
    private readonly ComboBox _fanSpeedCombo = new ComboBox();
    private readonly Dictionary<PerformanceMode, Button> _modeButtons = new Dictionary<PerformanceMode, Button>();
    private bool _suppressPowerModeUiUpdates;
    private bool _suppressMaxFanUiUpdates;
    private bool _suppressFanMinimumUiUpdates;

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
            RowCount = 6
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

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
        _maxFanCheckBox.Margin = new Padding(0, 6, 0, 6);
        _maxFanCheckBox.CheckedChanged += (_, __) =>
        {
            if (_suppressMaxFanUiUpdates)
            {
                return;
            }

            MaxFanRequested?.Invoke(this, _maxFanCheckBox.Checked);
        };
        thermalPanel.Controls.Add(_maxFanCheckBox);

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
            Text = "Fan speed:",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 6, 8, 6)
        };

        _fanSpeedCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _fanSpeedCombo.Width = 180;
        _fanSpeedCombo.Items.Clear();
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

        Label batteryLabel = new Label
        {
            Text = "On battery:",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 6, 8, 6)
        };
        Label pluggedLabel = new Label
        {
            Text = "Plugged in:",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 6, 8, 6)
        };

        _batteryModeCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _batteryModeCombo.Width = 180;
        _batteryModeCombo.Items.Clear();
        _batteryModeCombo.Items.AddRange(new object[] { "None", "Eco", "Balanced", "Performance", "Unleashed" });
        _batteryModeCombo.SelectedIndexChanged += (_, __) => OnPowerModeSelectionChanged();

        _pluggedModeCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _pluggedModeCombo.Width = 180;
        _pluggedModeCombo.Items.Clear();
        _pluggedModeCombo.Items.AddRange(new object[] { "None", "Eco", "Balanced", "Performance", "Unleashed" });
        _pluggedModeCombo.SelectedIndexChanged += (_, __) => OnPowerModeSelectionChanged();

        powerRow.Controls.Add(batteryLabel, 0, 0);
        powerRow.Controls.Add(_batteryModeCombo, 1, 0);
        powerRow.Controls.Add(pluggedLabel, 2, 0);
        powerRow.Controls.Add(_pluggedModeCombo, 3, 0);

        _powerSourceLabel.AutoSize = true;
        _powerSourceLabel.Margin = new Padding(0, 2, 0, 0);

        layout.Controls.Add(summaryRow, 0, 0);
        layout.Controls.Add(modePanel, 0, 1);
        layout.Controls.Add(thermalPanel, 0, 2);
        layout.Controls.Add(fanRow, 0, 3);
        layout.Controls.Add(powerRow, 0, 4);
        layout.Controls.Add(_powerSourceLabel, 0, 5);
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
        string fanMinimumSuffix = state.FanMinimumOverrideRpm.HasValue ? " (custom)" : " (mode default)";

        _performanceSummaryLabel.Text = "Mode: " + modeText;
        _performanceMetricsLabel.Text = "Thermal: " + state.CurrentThermalMode + " | Legacy fan: " + state.CurrentLegacyFanMode + " | Fan minimum: " + fanMinimumText + fanMinimumSuffix;
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
}
