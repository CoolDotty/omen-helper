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
    private readonly TextBox _logTextBox = new TextBox();
    private readonly Dictionary<PerformanceMode, Button> _modeButtons = new Dictionary<PerformanceMode, Button>();
    private Button _integratedButton;
    private Button _hybridButton;
    private Button _discreteButton;
    private DiagnosticsForm _diagnosticsForm;

    public MainForm()
    {
        Text = "OMEN Helper BIOS Control";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 620);
        Size = new Size(980, 680);

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

        AddModeButton(modePanel, PerformanceMode.Default, "Default");
        AddModeButton(modePanel, PerformanceMode.Performance, "Performance");
        AddModeButton(modePanel, PerformanceMode.Eco, "Eco");
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

        GroupBox spacer = new GroupBox
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(12),
            Margin = new Padding(0, 0, 0, 12)
        };
        controlsRow.Controls.Add(spacer, 1, 1);

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(_statusLabel, 0, 1);
        root.Controls.Add(_graphicsLabel, 0, 2);
        root.Controls.Add(modeGroup, 0, 3);
        root.Controls.Add(controlsRow, 0, 4);

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

        actionPanel.Controls.Add(refreshButton);
        actionPanel.Controls.Add(notesButton);
        actionPanel.Controls.Add(diagnosticsButton);
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
        _graphicsModeNoteLabel.Text = "Loading supported graphics modes...";

        _integratedButton = new Button
        {
            Text = "Integrated (UMA)",
            AutoSize = true
        };
        _integratedButton.Click += async (_, __) => await ApplyGraphicsModeAsync(GraphicsSwitcherMode.UMAMode, "Integrated Graphics Only");

        _hybridButton = new Button
        {
            Text = "Hybrid",
            AutoSize = true
        };
        _hybridButton.Click += async (_, __) => await ApplyGraphicsModeAsync(GraphicsSwitcherMode.Hybrid, "Hybrid Graphics");

        _discreteButton = new Button
        {
            Text = "Discrete",
            AutoSize = true
        };
        _discreteButton.Click += async (_, __) => await ApplyGraphicsModeAsync(GraphicsSwitcherMode.Discrete, "Discrete Graphics");

        graphicsPanel.Controls.Add(_graphicsModeNoteLabel);
        graphicsPanel.Controls.Add(_integratedButton);
        graphicsPanel.Controls.Add(_hybridButton);
        graphicsPanel.Controls.Add(_discreteButton);
        graphicsGroup.Controls.Add(graphicsPanel);
        return graphicsGroup;
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
    }

    private void OnFormClosing(object sender, FormClosingEventArgs e)
    {
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
            " | Mode: " + state.CurrentMode +
            " | Graphics: " + state.CurrentGraphicsMode +
            " | Thermal: " + state.CurrentThermalMode +
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

        _graphicsLabel.Text = "Graphics: " + state.CurrentGraphicsMode;
        _integratedButton.Visible = state.GraphicsSupportsUma;
        _hybridButton.Visible = state.GraphicsSupportsHybrid;
        _discreteButton.Visible = state.GraphicsSupportsDiscrete;
        UpdateGraphicsButtonState(_integratedButton, "Integrated (UMA)", state.GraphicsSupportsUma, state.CurrentGraphicsMode, GraphicsSwitcherMode.UMAMode);
        UpdateGraphicsButtonState(_hybridButton, "Hybrid", state.GraphicsSupportsHybrid, state.CurrentGraphicsMode, GraphicsSwitcherMode.Hybrid);
        UpdateGraphicsButtonState(_discreteButton, "Discrete", state.GraphicsSupportsDiscrete, state.CurrentGraphicsMode, GraphicsSwitcherMode.Discrete);
        _graphicsModeNoteLabel.Text =
            "Supported: " + string.Join(", ", BuildSupportedGraphicsModeList(state)) +
            " | Restart required: " + state.GraphicsNeedsReboot;
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

    private static IEnumerable<string> BuildSupportedGraphicsModeList(PerformanceControlState state)
    {
        if (state.GraphicsSupportsUma)
        {
            yield return "Integrated";
        }

        if (state.GraphicsSupportsHybrid)
        {
            yield return "Hybrid";
        }

        if (state.GraphicsSupportsDiscrete)
        {
            yield return "Discrete";
        }
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
            "The graphics mode change was requested. Restart now?",
            "Graphics Mode",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (restartResult == DialogResult.Yes)
        {
            Process.Start("shutdown", "/r /t 0");
        }
    }
}
