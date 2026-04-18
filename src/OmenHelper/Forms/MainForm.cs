using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using Hp.Bridge.Client.SDKs.PerformanceControl.Enums;
using HP.Omen.Core.Common.Enums;
using HP.Omen.Core.Model.DataStructure.Modules.GraphicsSwitcher.Enums;
using OmenHelper.Models;
using OmenHelper.Services;

namespace OmenHelper;

internal sealed class MainForm : Form
{
    private readonly OmenPerformanceController _controller = new OmenPerformanceController();
    private readonly Label _performanceSummaryLabel = new Label();
    private readonly Label _performanceMetricsLabel = new Label();
    private readonly Label _graphicsSummaryLabel = new Label();
    private readonly Label _graphicsMetricsLabel = new Label();
    private readonly ComboBox _batteryModeCombo = new ComboBox();
    private readonly ComboBox _pluggedModeCombo = new ComboBox();
    private readonly Label _powerSourceLabel = new Label();
    private readonly CheckBox _maxFanCheckBox = new CheckBox();
    private readonly TextBox _logTextBox = new TextBox();
    private readonly Dictionary<PerformanceMode, Button> _modeButtons = new Dictionary<PerformanceMode, Button>();
    private readonly Dictionary<GraphicsSwitcherMode, Button> _graphicsButtons = new Dictionary<GraphicsSwitcherMode, Button>();
    private Button _umaButton;
    private Button _hybridButton;
    private DiagnosticsForm _diagnosticsForm;
    private readonly Timer _powerModeTimer = new Timer();
    private bool _suppressPowerModeUiUpdates;
    private bool _suppressMaxFanUiUpdates;
    private PerformanceControlState _latestState = new PerformanceControlState();

    public MainForm()
    {
        Text = "OMEN Helper BIOS Control";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1040, 760);
        Size = new Size(1180, 860);

        InitializeUi();

        Load += OnLoad;
        FormClosing += OnFormClosing;
        _powerModeTimer.Interval = 3000;
        _powerModeTimer.Tick += async (_, __) => await SyncPowerSourceModeAsync();
    }

    private void InitializeUi()
    {
        Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point);

        TableLayoutPanel root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(16)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        root.Controls.Add(BuildPerformanceGroup(), 0, 0);
        root.Controls.Add(BuildGraphicsGroup(), 0, 1);
        root.Controls.Add(BuildLogsGroup(), 0, 2);

        Controls.Add(root);

        UpdateSummaryLabels();
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
            RowCount = 4
        };
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
        _maxFanCheckBox.CheckedChanged += async (_, __) => await OnMaxFanCheckedChangedAsync();
        thermalPanel.Controls.Add(_maxFanCheckBox);

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
        _batteryModeCombo.SelectedIndexChanged += async (_, __) => await OnPowerModeSelectionChangedAsync();

        _pluggedModeCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _pluggedModeCombo.Width = 180;
        _pluggedModeCombo.Items.Clear();
        _pluggedModeCombo.Items.AddRange(new object[] { "None", "Eco", "Balanced", "Performance", "Unleashed" });
        _pluggedModeCombo.SelectedIndexChanged += async (_, __) => await OnPowerModeSelectionChangedAsync();

        powerRow.Controls.Add(batteryLabel, 0, 0);
        powerRow.Controls.Add(_batteryModeCombo, 1, 0);
        powerRow.Controls.Add(pluggedLabel, 2, 0);
        powerRow.Controls.Add(_pluggedModeCombo, 3, 0);

        layout.Controls.Add(summaryRow, 0, 0);
        layout.Controls.Add(modePanel, 0, 1);
        layout.Controls.Add(thermalPanel, 0, 2);
        layout.Controls.Add(powerRow, 0, 3);
        performanceGroup.Controls.Add(layout);
        return performanceGroup;
    }

    private GroupBox BuildGraphicsGroup()
    {
        GroupBox graphicsGroup = new GroupBox
        {
            Text = "Graphics Mode",
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(12),
            Margin = new Padding(0, 0, 0, 12)
        };

        TableLayoutPanel layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2
        };
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
        _graphicsSummaryLabel.AutoSize = true;
        _graphicsMetricsLabel.AutoSize = true;
        _graphicsMetricsLabel.Anchor = AnchorStyles.Right;
        summaryRow.Controls.Add(_graphicsSummaryLabel, 0, 0);
        summaryRow.Controls.Add(_graphicsMetricsLabel, 1, 0);

        FlowLayoutPanel graphicsPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = true
        };

        _umaButton = CreateGraphicsButton("[ Integrated Only ]", GraphicsSwitcherMode.UMAMode, "Integrated Only");
        _hybridButton = CreateGraphicsButton("[ Hybrid ]", GraphicsSwitcherMode.Hybrid, "Hybrid");
        graphicsPanel.Controls.Add(_umaButton);
        graphicsPanel.Controls.Add(_hybridButton);

        layout.Controls.Add(summaryRow, 0, 0);
        layout.Controls.Add(graphicsPanel, 0, 1);
        graphicsGroup.Controls.Add(layout);
        return graphicsGroup;
    }

    private GroupBox BuildLogsGroup()
    {
        GroupBox logGroup = new GroupBox
        {
            Text = "Logs",
            Dock = DockStyle.Fill,
            Padding = new Padding(12)
        };

        _logTextBox.Dock = DockStyle.Fill;
        _logTextBox.Multiline = true;
        _logTextBox.ScrollBars = ScrollBars.Vertical;
        _logTextBox.ReadOnly = true;
        _logTextBox.Font = new Font("Consolas", 10F, FontStyle.Regular, GraphicsUnit.Point);
        logGroup.Controls.Add(_logTextBox);
        return logGroup;
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
        button.Click += async (_, __) => await _controller.SetPerformanceModeAsync(mode);
        panel.Controls.Add(button);
        _modeButtons[mode] = button;
    }

    private Button CreateGraphicsButton(string title, GraphicsSwitcherMode mode, string label)
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
        button.Click += async (_, __) => await ApplyGraphicsModeAsync(mode, label);
        _graphicsButtons[mode] = button;
        return button;
    }

    private void UpdateSummaryLabels()
    {
        PerformanceControlState state = _latestState ?? new PerformanceControlState();
        string modeText = FormatDisplayedPerformanceMode(state);
        string graphicsText = FormatGraphicsMode(state.CurrentGraphicsMode);
        string fanMinimumText = state.CurrentFanMinimumRpm > 0 ? state.CurrentFanMinimumRpm.ToString("N0") + "RPM" : "<unavailable>";

        _performanceSummaryLabel.Text = "Mode: " + modeText;
        _performanceMetricsLabel.Text = "Thermal: " + state.CurrentThermalMode + " | Legacy fan: " + state.CurrentLegacyFanMode + " | Fan minimum: " + fanMinimumText;
        _graphicsSummaryLabel.Text = "Graphics Mode: " + graphicsText;
        _graphicsMetricsLabel.Text = "Support: UMA=" + state.GraphicsSupportsUma + " Hybrid=" + state.GraphicsSupportsHybrid + " | Reboot=" + state.GraphicsNeedsReboot;
    }

    private void OnLoad(object sender, EventArgs e)
    {
        _controller.StateChanged += ControllerOnStateChanged;
        _controller.LogMessage += ControllerOnLogMessage;
        _controller.Start();
        LoadPowerModePreferencesIntoUi();
        _powerModeTimer.Start();
        _ = SyncPowerSourceModeAsync();
    }

    private void OnFormClosing(object sender, FormClosingEventArgs e)
    {
        _powerModeTimer.Stop();
        _diagnosticsForm?.Close();
        _controller.Dispose();
    }

    private void ControllerOnStateChanged(object sender, PerformanceControlState state)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<object, PerformanceControlState>(ControllerOnStateChanged), sender, state);
            return;
        }

        PerformanceControlState safeState = state ?? new PerformanceControlState();
        _latestState = safeState;

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

        UpdateGraphicsButtonState(_umaButton, "[ Integrated Only ]", safeState.GraphicsModeSwitchSupported, safeState.CurrentGraphicsMode, GraphicsSwitcherMode.UMAMode);
        UpdateGraphicsButtonState(_hybridButton, "[ Hybrid ]", safeState.GraphicsModeSwitchSupported, safeState.CurrentGraphicsMode, GraphicsSwitcherMode.Hybrid);
        UpdateMaxFanCheckBoxState(safeState);

        UpdateSummaryLabels();
        UpdatePowerSourceLabel();
    }

    private void ControllerOnLogMessage(object sender, string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<object, string>(ControllerOnLogMessage), sender, message);
            return;
        }

        _logTextBox.AppendText(message + Environment.NewLine);
    }

    private void LoadPowerModePreferencesIntoUi()
    {
        try
        {
            _suppressPowerModeUiUpdates = true;
            SetComboSelection(_batteryModeCombo, FormatPowerModeChoice(_controller.GetBatteryPowerModePreference()));
            SetComboSelection(_pluggedModeCombo, FormatPowerModeChoice(_controller.GetPluggedInPowerModePreference()));
        }
        finally
        {
            _suppressPowerModeUiUpdates = false;
        }

        UpdatePowerSourceLabel();
    }

    private async Task OnPowerModeSelectionChangedAsync()
    {
        if (_suppressPowerModeUiUpdates)
        {
            return;
        }

        if (TryParseSelectedPowerMode(_batteryModeCombo, out PerformanceMode? batteryMode))
        {
            _controller.SetBatteryPowerModePreference(batteryMode);
        }

        if (TryParseSelectedPowerMode(_pluggedModeCombo, out PerformanceMode? pluggedMode))
        {
            _controller.SetPluggedInPowerModePreference(pluggedMode);
        }

        await SyncPowerSourceModeAsync();
    }

    private async Task OnMaxFanCheckedChangedAsync()
    {
        if (_suppressMaxFanUiUpdates)
        {
            return;
        }

        await _controller.SetMaxFanThermalModeAsync(_maxFanCheckBox.Checked);
    }

    private async Task SyncPowerSourceModeAsync()
    {
        PowerLineStatus powerLineStatus = SystemInformation.PowerStatus.PowerLineStatus;
        UpdatePowerSourceLabel();

        if (powerLineStatus == PowerLineStatus.Unknown)
        {
            return;
        }

        bool pluggedIn = powerLineStatus == PowerLineStatus.Online;
        await _controller.SyncPowerSourcePerformanceModeAsync(pluggedIn);
    }

    private void UpdatePowerSourceLabel()
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

    private static string GetComboSelectionText(ComboBox comboBox)
    {
        return comboBox.SelectedItem != null ? Convert.ToString(comboBox.SelectedItem) : "<none>";
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

    private static string FormatGraphicsMode(string currentGraphicsMode)
    {
        return GraphicsSupportHelper.FormatDisplayName(currentGraphicsMode);
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

    private static void UpdateGraphicsButtonState(Button button, string title, bool supported, string currentGraphicsMode, GraphicsSwitcherMode representedMode)
    {
        bool isCurrent = string.Equals(currentGraphicsMode, representedMode.ToString(), StringComparison.OrdinalIgnoreCase);
        button.Enabled = supported && !isCurrent;
        button.BackColor = isCurrent ? Color.LightGreen : SystemColors.Control;
        button.Text = title;
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

    private void ShowDiagnosticsWindow()
    {
        if (_diagnosticsForm != null && !_diagnosticsForm.IsDisposed)
        {
            _diagnosticsForm.Focus();
            return;
        }

        _diagnosticsForm = new DiagnosticsForm(() => _controller.BuildDiagnosticsReportAsync());
        _diagnosticsForm.Show(this);
    }

    private async Task ApplyGraphicsModeAsync(GraphicsSwitcherMode mode, string label)
    {
        DialogResult result = MessageBox.Show(
            this,
            "Apply " + label + " mode? A restart is required for the change to take effect.",
            "Graphics Mode",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (result != DialogResult.Yes)
        {
            return;
        }

        bool success = await _controller.SetGraphicsModeAsync(mode);
        if (!success)
        {
            MessageBox.Show(this, "Failed to request graphics mode change. Check diagnostics or the log for support flags and the BIOS return code.", "Graphics Mode", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        DialogResult restartResult = MessageBox.Show(
            this,
            "The BIOS accepted the graphics mode change. A reboot is required before it takes effect. Restart now?",
            "Graphics Mode",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (restartResult == DialogResult.Yes)
        {
            Process.Start("shutdown", "/r /t 0");
        }
    }
}
