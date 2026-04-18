using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using Hp.Bridge.Client.SDKs.PerformanceControl.Enums;
using HP.Omen.Core.Model.DataStructure.Modules.GraphicsSwitcher.Enums;
using OmenHelper.Models;
using OmenHelper.Services;

namespace OmenHelper;

internal sealed class MainForm : Form
{
    private readonly OmenPerformanceController _controller = new OmenPerformanceController();
    private readonly Label _statusLabel = new Label();
    private readonly Label _graphicsLabel = new Label();
    private readonly Label _graphicsModeNoteLabel = new Label();
    private readonly ComboBox _thermalModeCombo = new ComboBox();
    private readonly ComboBox _batteryModeCombo = new ComboBox();
    private readonly ComboBox _pluggedModeCombo = new ComboBox();
    private readonly Label _powerSourceLabel = new Label();
    private readonly Label _fanTempStatsLabel = new Label();
    private readonly TextBox _logTextBox = new TextBox();
    private readonly Dictionary<PerformanceMode, Button> _modeButtons = new Dictionary<PerformanceMode, Button>();
    private Button _umaButton;
    private Button _hybridButton;
    private DiagnosticsForm _diagnosticsForm;
    private readonly Timer _powerModeTimer = new Timer();
    private readonly Timer _fanTempTimer = new Timer();
    private bool _suppressPowerModeUiUpdates;

    public MainForm()
    {
        Text = "OMEN Helper BIOS Control";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 620);
        Size = new Size(980, 680);

        InitializeUi();

        Load += OnLoad;
        FormClosing += OnFormClosing;
        _powerModeTimer.Interval = 3000;
        _powerModeTimer.Tick += async (_, __) => await SyncPowerSourceModeAsync();
        _fanTempTimer.Interval = 1000;
        _fanTempTimer.Tick += (_, __) => UpdateFanTempStats();
    }

    private void InitializeUi()
    {
        Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point);

        TableLayoutPanel root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(16)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        Label header = new Label
        {
            AutoSize = true,
            Text = "BIOS-only OMEN control",
            Font = new Font("Segoe UI Semibold", 16F, FontStyle.Bold, GraphicsUnit.Point),
            Margin = new Padding(0, 0, 0, 10)
        };

        _statusLabel.AutoSize = true;
        _statusLabel.Text = "Initializing firmware state.";
        _statusLabel.Margin = new Padding(0, 0, 0, 12);

        _graphicsLabel.AutoSize = true;
        _graphicsLabel.Text = "Graphics: reading current mode";
        _graphicsLabel.Margin = new Padding(0, 0, 0, 12);

        GroupBox modeGroup = new GroupBox
        {
            Text = "Performance Mode",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(12)
        };
        FlowLayoutPanel modePanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = true
        };

        AddModeButton(modePanel, PerformanceMode.Eco, "Eco");
        AddModeButton(modePanel, PerformanceMode.Default, "Balanced");
        AddModeButton(modePanel, PerformanceMode.Performance, "Performance");
        AddModeButton(modePanel, PerformanceMode.Extreme, "Unleashed");

        modeGroup.Controls.Add(modePanel);

        TableLayoutPanel controlsRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 2,
            AutoSize = true
        };
        controlsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        controlsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        controlsRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        controlsRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        controlsRow.Controls.Add(BuildThermalGroup(), 0, 0);
        controlsRow.Controls.Add(BuildActionsGroup(), 1, 0);
        controlsRow.Controls.Add(BuildGraphicsGroup(), 0, 1);
        controlsRow.Controls.Add(BuildPowerModeGroup(), 1, 1);

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(_statusLabel, 0, 1);
        root.Controls.Add(_graphicsLabel, 0, 2);
        root.Controls.Add(modeGroup, 0, 3);
        root.Controls.Add(controlsRow, 0, 4);
        root.Controls.Add(BuildFanTempGroup(), 0, 5);

        GroupBox logGroup = new GroupBox
        {
            Text = "Log",
            Dock = DockStyle.Bottom,
            Padding = new Padding(12),
            Height = 240
        };
        _logTextBox.Dock = DockStyle.Fill;
        _logTextBox.Multiline = true;
        _logTextBox.ScrollBars = ScrollBars.Vertical;
        _logTextBox.ReadOnly = true;
        _logTextBox.Font = new Font("Consolas", 10F, FontStyle.Regular, GraphicsUnit.Point);
        logGroup.Controls.Add(_logTextBox);

        Controls.Add(logGroup);
        Controls.Add(root);
    }

    private GroupBox BuildThermalGroup()
    {
        GroupBox thermalGroup = new GroupBox
        {
            Text = "Thermal Mode",
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(12),
            Margin = new Padding(0, 0, 12, 12)
        };

        FlowLayoutPanel thermalPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true
        };

        _thermalModeCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _thermalModeCombo.Width = 180;
        _thermalModeCombo.Items.AddRange(new object[] { ThermalControl.Auto, ThermalControl.Max });
        _thermalModeCombo.SelectedIndex = 0;

        Button applyThermalButton = new Button
        {
            Text = "Apply Thermal",
            AutoSize = true
        };
        applyThermalButton.Click += async (_, __) =>
        {
            if (_thermalModeCombo.SelectedItem is ThermalControl thermalControl)
            {
                await _controller.SetThermalModeAsync(thermalControl);
            }
        };

        thermalPanel.Controls.Add(_thermalModeCombo);
        thermalPanel.Controls.Add(applyThermalButton);
        thermalGroup.Controls.Add(thermalPanel);
        return thermalGroup;
    }

    private GroupBox BuildActionsGroup()
    {
        GroupBox actionGroup = new GroupBox
        {
            Text = "Actions",
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(12),
            Margin = new Padding(0, 0, 0, 12)
        };

        FlowLayoutPanel actionPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true
        };

        Button refreshButton = new Button
        {
            Text = "Refresh State",
            AutoSize = true
        };
        refreshButton.Click += async (_, __) => await _controller.RequestInitializationAsync();

        Button notesButton = new Button
        {
            Text = "Open Notes",
            AutoSize = true
        };
        notesButton.Click += (_, __) =>
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RESEARCH_NOTES.md"),
                UseShellExecute = true
            });
        };

        Button diagnosticsButton = new Button
        {
            Text = "Open Diagnostics",
            AutoSize = true
        };
        diagnosticsButton.Click += (_, __) => ShowDiagnosticsWindow();

        Button fanCurveButton = new Button
        {
            Text = "Fan Curve Manager",
            AutoSize = true
        };
        fanCurveButton.Click += (_, __) => ShowFanCurveManager();

        actionPanel.Controls.Add(refreshButton);
        actionPanel.Controls.Add(notesButton);
        actionPanel.Controls.Add(diagnosticsButton);
        actionPanel.Controls.Add(fanCurveButton);
        actionGroup.Controls.Add(actionPanel);
        return actionGroup;
    }

    private GroupBox BuildGraphicsGroup()
    {
        GroupBox graphicsGroup = new GroupBox
        {
            Text = "Graphics Mode",
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(12),
            Margin = new Padding(0, 0, 12, 12)
        };

        FlowLayoutPanel graphicsPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false
        };

        _graphicsModeNoteLabel.AutoSize = true;
        _graphicsModeNoteLabel.Text = "Changing graphics mode requires a reboot.";

        _umaButton = new Button
        {
            Text = "Set UMA",
            AutoSize = true
        };
        _umaButton.Click += async (_, __) => await ApplyGraphicsModeAsync(GraphicsSwitcherMode.UMAMode, "UMA");

        _hybridButton = new Button
        {
            Text = "Set Hybrid",
            AutoSize = true
        };
        _hybridButton.Click += async (_, __) => await ApplyGraphicsModeAsync(GraphicsSwitcherMode.Hybrid, "Hybrid");

        graphicsPanel.Controls.Add(_graphicsModeNoteLabel);
        graphicsPanel.Controls.Add(_hybridButton);
        graphicsPanel.Controls.Add(_umaButton);
        graphicsGroup.Controls.Add(graphicsPanel);
        return graphicsGroup;
    }

    private GroupBox BuildPowerModeGroup()
    {
        GroupBox powerGroup = new GroupBox
        {
            Text = "Power Source Modes",
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(12),
            Margin = new Padding(0, 0, 0, 12)
        };

        TableLayoutPanel panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 3
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

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
        _batteryModeCombo.Items.AddRange(new object[] { "Eco", "Balanced", "Performance", "Unleashed" });
        _batteryModeCombo.SelectedIndexChanged += async (_, __) => await OnPowerModeSelectionChangedAsync();

        _pluggedModeCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _pluggedModeCombo.Width = 180;
        _pluggedModeCombo.Items.AddRange(new object[] { "Eco", "Balanced", "Performance", "Unleashed" });
        _pluggedModeCombo.SelectedIndexChanged += async (_, __) => await OnPowerModeSelectionChangedAsync();

        _powerSourceLabel.AutoSize = true;
        _powerSourceLabel.Text = "Power source: checking...";
        _powerSourceLabel.Margin = new Padding(0, 8, 0, 0);

        panel.Controls.Add(batteryLabel, 0, 0);
        panel.Controls.Add(_batteryModeCombo, 1, 0);
        panel.Controls.Add(pluggedLabel, 0, 1);
        panel.Controls.Add(_pluggedModeCombo, 1, 1);
        panel.Controls.Add(_powerSourceLabel, 0, 2);
        panel.SetColumnSpan(_powerSourceLabel, 2);

        powerGroup.Controls.Add(panel);
        return powerGroup;
    }

    private GroupBox BuildFanTempGroup()
    {
        GroupBox tempGroup = new GroupBox
        {
            Text = "Temperature Stats",
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(12),
            Margin = new Padding(0, 0, 0, 12)
        };

        _fanTempStatsLabel.AutoSize = true;
        _fanTempStatsLabel.Text =
            "CPU core avg: <unavailable> | GPU temp: <unavailable> | Chassis temp: <unavailable>" + Environment.NewLine +
            "Spin-up avg: <unavailable> | Spin-down avg: <unavailable>";
        _fanTempStatsLabel.MaximumSize = new Size(0, 0);

        tempGroup.Controls.Add(_fanTempStatsLabel);
        return tempGroup;
    }

    private void AddModeButton(FlowLayoutPanel panel, PerformanceMode mode, string title)
    {
        Button button = new Button
        {
            Text = title,
            AutoSize = true,
            Margin = new Padding(0, 0, 12, 12)
        };
        button.Click += async (_, __) => await _controller.SetPerformanceModeAsync(mode);
        panel.Controls.Add(button);
        _modeButtons[mode] = button;
    }

    private void OnLoad(object sender, EventArgs e)
    {
        _controller.StateChanged += ControllerOnStateChanged;
        _controller.LogMessage += ControllerOnLogMessage;
        _controller.Start();
        LoadPowerModePreferencesIntoUi();
        _powerModeTimer.Start();
        _fanTempTimer.Start();
        UpdateFanTempStats();
        _ = SyncPowerSourceModeAsync();
    }

    private void OnFormClosing(object sender, FormClosingEventArgs e)
    {
        _powerModeTimer.Stop();
        _fanTempTimer.Stop();
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

        _statusLabel.Text =
            "Initialized: " + state.Initialized +
            " | Available: " + state.Available +
            " | Mode: " + FormatDisplayedPerformanceMode(state) +
            " | Fan Min: " + state.CurrentFanMinimumRpm + " RPM" +
            " | Graphics: " + FormatGraphicsMode(state.CurrentGraphicsMode) +
            " | Thermal: " + state.CurrentThermalMode +
            " | FanCurve: up " + state.FanCurveSpinUpWindowMs + "ms / down " + state.FanCurveSpinDownWindowMs + "ms / pad " + state.FanCurveBreakpointPaddingCelsius + "°C" +
            " | Support: " + string.Join(", ", state.SupportModes ?? Array.Empty<string>());

        foreach (KeyValuePair<PerformanceMode, Button> pair in _modeButtons)
        {
            pair.Value.BackColor = Color.Gainsboro;
        }

        bool biosStateReady = state.Initialized && state.Available && (state.CurrentModeKnown || state.CurrentModeIsInferred);
        if (biosStateReady && TryParseDisplayedPerformanceMode(state.CurrentMode, out PerformanceMode selectedMode) && _modeButtons.TryGetValue(selectedMode, out Button activeButton))
        {
            activeButton.BackColor = Color.LightGreen;
        }

        if (Enum.TryParse(state.CurrentThermalMode, out ThermalControl thermalControl))
        {
            _thermalModeCombo.SelectedItem = thermalControl;
        }

        _graphicsLabel.Text = "Graphics: " + FormatGraphicsMode(state.CurrentGraphicsMode);
        UpdateGraphicsButtonState(_umaButton, "UMA", state.GraphicsModeSwitchSupported, state.CurrentGraphicsMode, GraphicsSwitcherMode.UMAMode);
        UpdateGraphicsButtonState(_hybridButton, "Hybrid", state.GraphicsModeSwitchSupported, state.CurrentGraphicsMode, GraphicsSwitcherMode.Hybrid);
        UpdateFanTempStats();
        if (!state.GraphicsModeSwitchSupported)
        {
            _graphicsModeNoteLabel.Text =
                "No BIOS-confirmed graphics switching support. BIOS bits: 0x" +
                state.GraphicsModeSwitchBits.ToString("X2");
        }
        else
        {
            _graphicsModeNoteLabel.Text =
                "BIOS-supported: " + string.Join(", ", BuildSupportedGraphicsModeList(state)) +
                " | BIOS bits: 0x" + state.GraphicsModeSwitchBits.ToString("X2") +
                " | Restart required: " + state.GraphicsNeedsReboot;
        }

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

        if (TryParseSelectedPowerMode(_batteryModeCombo, out PerformanceMode batteryMode))
        {
            _controller.SetBatteryPowerModePreference(batteryMode);
        }

        if (TryParseSelectedPowerMode(_pluggedModeCombo, out PerformanceMode pluggedMode))
        {
            _controller.SetPluggedInPowerModePreference(pluggedMode);
        }

        await SyncPowerSourceModeAsync();
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

    private void UpdateFanTempStats()
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(UpdateFanTempStats));
            return;
        }

        bool hasTemps = _controller.TryGetTemperatureSnapshot(out double cpuCoreAvg, out double gpuTemp, out double chassisTemp);
        bool hasFanTelemetry = _controller.TryGetFanTelemetry(out double currentTemp, out double spinUpAverageTemp, out double spinDownAverageTemp);

        string topLine =
            "CPU core avg: " + FormatTempValue(hasTemps ? cpuCoreAvg : double.NaN) +
            " | GPU temp: " + FormatTempValue(hasTemps ? gpuTemp : double.NaN) +
            " | Chassis temp: " + FormatTempValue(hasTemps ? chassisTemp : double.NaN);

        string bottomLine =
            "Spin-up avg: " + FormatTempValue(hasFanTelemetry ? spinUpAverageTemp : double.NaN) +
            " | Spin-down avg: " + FormatTempValue(hasFanTelemetry ? spinDownAverageTemp : double.NaN);

        _fanTempStatsLabel.Text = topLine + Environment.NewLine + bottomLine;
    }

    private static string FormatTempValue(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return "<unavailable>";
        }

        return value.ToString("F1") + "°C";
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

    private static bool TryParseSelectedPowerMode(ComboBox comboBox, out PerformanceMode mode)
    {
        string value = Convert.ToString(comboBox.SelectedItem);
        return TryParseDisplayedPerformanceMode(value, out mode);
    }

    private static string FormatPowerModeChoice(PerformanceMode mode)
    {
        return PerformanceModeFirmwareMap.FormatDisplayName(mode);
    }

    private static IEnumerable<string> BuildSupportedGraphicsModeList(PerformanceControlState state)
    {
        return GraphicsSupportHelper.BuildSupportedGraphicsModeList(state);
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

    private static bool TryParseDisplayedPerformanceMode(string currentMode, out PerformanceMode mode)
    {
        return PerformanceModeFirmwareMap.TryParseDisplayName(currentMode, out mode);
    }

    private static void UpdateGraphicsButtonState(Button button, string title, bool supported, string currentGraphicsMode, GraphicsSwitcherMode representedMode)
    {
        bool isCurrent = string.Equals(currentGraphicsMode, representedMode.ToString(), StringComparison.OrdinalIgnoreCase);
        button.Enabled = supported && !isCurrent;
        button.BackColor = isCurrent ? Color.LightGreen : SystemColors.Control;
        button.Text = isCurrent ? title + " (Current)" : title;
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

    private void ShowFanCurveManager()
    {
        try
        {
            var form = new FanCurveForm(_controller);
            form.ShowDialog(this);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Failed to open Fan Curve Manager: " + ex.Message, "Fan Curve", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
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
