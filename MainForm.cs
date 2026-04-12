using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Hp.Bridge.Client.SDKs.PerformanceControl.Enums;
using HP.Omen.Core.Model.DataStructure.Modules.FanControl.Enums;
using OmenHelper.Models;
using OmenHelper.Services;

namespace OmenHelper;

internal sealed class MainForm : Form
{
    private readonly OmenPerformanceController _controller = new OmenPerformanceController();
    private readonly Label _statusLabel = new Label();
    private readonly Label _cpuLabel = new Label();
    private readonly Label _gpuLabel = new Label();
    private readonly ComboBox _thermalModeCombo = new ComboBox();
    private readonly ComboBox _fanModeCombo = new ComboBox();
    private readonly TextBox _logTextBox = new TextBox();
    private readonly Dictionary<PerformanceMode, Button> _modeButtons = new Dictionary<PerformanceMode, Button>();

    public MainForm()
    {
        Text = "OMEN Helper PoC";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 650);
        Size = new Size(980, 720);

        InitializeUi();

        Load += OnLoad;
        FormClosing += OnFormClosing;
    }

    private void InitializeUi()
    {
        Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point);

        TableLayoutPanel root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(16)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        Label header = new Label
        {
            AutoSize = true,
            Text = "Proof-of-concept for HP OMEN Transcend 14 control",
            Font = new Font("Segoe UI Semibold", 16F, FontStyle.Bold, GraphicsUnit.Point),
            Margin = new Padding(0, 0, 0, 10)
        };

        _statusLabel.AutoSize = true;
        _statusLabel.Text = "Waiting for HP background initialization.";
        _statusLabel.Margin = new Padding(0, 0, 0, 12);

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

        AddModeButton(modePanel, PerformanceMode.Default, "Default");
        AddModeButton(modePanel, PerformanceMode.Performance, "Performance");
        AddModeButton(modePanel, PerformanceMode.Quiet, "Quiet");
        AddModeButton(modePanel, PerformanceMode.Cool, "Cool");
        AddModeButton(modePanel, PerformanceMode.Eco, "Eco");
        AddModeButton(modePanel, PerformanceMode.Extreme, "Extreme / Unleash");

        modeGroup.Controls.Add(modePanel);

        TableLayoutPanel controlsRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 3,
            AutoSize = true
        };
        controlsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35F));
        controlsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35F));
        controlsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30F));

        controlsRow.Controls.Add(BuildThermalGroup(), 0, 0);
        controlsRow.Controls.Add(BuildFanGroup(), 1, 0);
        controlsRow.Controls.Add(BuildActionGroup(), 2, 0);

        GroupBox telemetryGroup = new GroupBox
        {
            Text = "Telemetry",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(12),
            Margin = new Padding(0, 0, 0, 12)
        };
        TableLayoutPanel telemetryPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            AutoSize = true
        };
        _cpuLabel.AutoSize = true;
        _gpuLabel.AutoSize = true;
        _cpuLabel.Text = "CPU: waiting for monitor registration";
        _gpuLabel.Text = "GPU: waiting for monitor registration";
        telemetryPanel.Controls.Add(_cpuLabel, 0, 0);
        telemetryPanel.Controls.Add(_gpuLabel, 0, 1);
        telemetryGroup.Controls.Add(telemetryPanel);

        GroupBox logGroup = new GroupBox
        {
            Text = "Log",
            Dock = DockStyle.Fill,
            Padding = new Padding(12)
        };
        _logTextBox.Dock = DockStyle.Fill;
        _logTextBox.Multiline = true;
        _logTextBox.ScrollBars = ScrollBars.Vertical;
        _logTextBox.ReadOnly = true;
        _logTextBox.Font = new Font("Consolas", 10F, FontStyle.Regular, GraphicsUnit.Point);
        logGroup.Controls.Add(_logTextBox);

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(_statusLabel, 0, 1);
        root.Controls.Add(modeGroup, 0, 2);
        root.Controls.Add(controlsRow, 0, 3);
        root.Controls.Add(telemetryGroup, 0, 4);

        Controls.Add(logGroup);
        Controls.Add(root);

        logGroup.Dock = DockStyle.Bottom;
        logGroup.Height = 240;
    }

    private GroupBox BuildThermalGroup()
    {
        GroupBox thermalGroup = new GroupBox
        {
            Text = "Thermal Mode",
            Dock = DockStyle.Fill,
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
        _thermalModeCombo.Items.AddRange(new object[] { ThermalControl.Auto, ThermalControl.Max, ThermalControl.Manual });
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

    private GroupBox BuildFanGroup()
    {
        GroupBox fanGroup = new GroupBox
        {
            Text = "Legacy Fan Mode",
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            Margin = new Padding(0, 0, 12, 12)
        };
        FlowLayoutPanel fanPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true
        };
        _fanModeCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _fanModeCombo.Width = 180;
        foreach (FanMode fanMode in Enum.GetValues(typeof(FanMode)).Cast<FanMode>())
        {
            _fanModeCombo.Items.Add(fanMode);
        }
        if (_fanModeCombo.Items.Count > 0)
        {
            _fanModeCombo.SelectedIndex = 0;
        }
        Button applyFanButton = new Button
        {
            Text = "Apply Fan",
            AutoSize = true
        };
        applyFanButton.Click += async (_, __) =>
        {
            if (_fanModeCombo.SelectedItem is FanMode fanMode)
            {
                await _controller.SetLegacyFanModeAsync(fanMode);
            }
        };
        fanPanel.Controls.Add(_fanModeCombo);
        fanPanel.Controls.Add(applyFanButton);
        fanGroup.Controls.Add(fanPanel);
        return fanGroup;
    }

    private GroupBox BuildActionGroup()
    {
        GroupBox actionGroup = new GroupBox
        {
            Text = "Actions",
            Dock = DockStyle.Fill,
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
            Text = "Request State",
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
        actionPanel.Controls.Add(refreshButton);
        actionPanel.Controls.Add(notesButton);
        actionGroup.Controls.Add(actionPanel);
        return actionGroup;
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
        _controller.TelemetryChanged += ControllerOnTelemetryChanged;
        _controller.LogMessage += ControllerOnLogMessage;
        _controller.Start();
    }

    private void OnFormClosing(object sender, FormClosingEventArgs e)
    {
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
            " | Mode: " + state.CurrentMode +
            " | Thermal: " + state.CurrentThermalMode +
            " | Legacy Fan: " + state.CurrentLegacyFanMode +
            " | Thermal UI: " + state.ThermalUiType +
            " | Support: " + string.Join(", ", state.SupportModes ?? Array.Empty<string>());

        foreach (KeyValuePair<PerformanceMode, Button> pair in _modeButtons)
        {
            pair.Value.BackColor = Color.Gainsboro;
        }

        if (Enum.TryParse(state.CurrentMode, out PerformanceMode selectedMode) && _modeButtons.TryGetValue(selectedMode, out Button activeButton))
        {
            activeButton.BackColor = Color.LightGreen;
        }

        if (Enum.TryParse(state.CurrentThermalMode, out ThermalControl thermalControl))
        {
            _thermalModeCombo.SelectedItem = thermalControl;
        }

        if (Enum.TryParse(state.CurrentLegacyFanMode, out FanMode fanMode))
        {
            _fanModeCombo.SelectedItem = fanMode;
        }
    }

    private void ControllerOnTelemetryChanged(object sender, TelemetrySnapshot snapshot)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<object, TelemetrySnapshot>(ControllerOnTelemetryChanged), sender, snapshot);
            return;
        }

        _cpuLabel.Text = "CPU: " + FormatSample(snapshot.Cpu);
        _gpuLabel.Text = "GPU: " + FormatSample(snapshot.Gpu);
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

    private static string FormatSample(PerformanceMonitorSample sample)
    {
        if (sample == null)
        {
            return "no data";
        }

        string temperature = string.IsNullOrWhiteSpace(sample.TemperatureString) ? "--" : sample.TemperatureString;
        string usage = string.IsNullOrWhiteSpace(sample.UsageString) ? "--" : sample.UsageString;
        return temperature + " | " + usage + " | state " + sample.TemperatureState;
    }
}
