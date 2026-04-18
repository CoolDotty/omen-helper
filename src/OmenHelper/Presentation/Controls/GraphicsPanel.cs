using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using HP.Omen.Core.Model.DataStructure.Modules.GraphicsSwitcher.Enums;
using OmenHelper.Application.State;
using OmenHelper.Domain.Graphics;

namespace OmenHelper.Presentation.Controls;

internal sealed class GraphicsPanel : UserControl
{
    private readonly Label _graphicsSummaryLabel = new Label();
    private readonly Label _graphicsMetricsLabel = new Label();
    private Button _umaButton;
    private Button _hybridButton;

    public GraphicsPanel()
    {
        Dock = DockStyle.Top;
        AutoSize = true;

        Controls.Add(BuildGraphicsGroup());
    }

    public Func<GraphicsSwitcherMode, string, Task<bool>> RequestGraphicsModeAsync { get; set; } = (_, __) => Task.FromResult(false);

    public void ApplyState(PerformanceControlState state)
    {
        PerformanceControlState safeState = state ?? new PerformanceControlState();
        UpdateSummaryLabels(safeState);
        UpdateGraphicsButtonState(_umaButton, "[ Integrated ]", safeState.GraphicsModeSwitchSupported, safeState.CurrentGraphicsMode, GraphicsSwitcherMode.UMAMode);
        UpdateGraphicsButtonState(_hybridButton, "[ Hybrid ]", safeState.GraphicsModeSwitchSupported, safeState.CurrentGraphicsMode, GraphicsSwitcherMode.Hybrid);
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

        _umaButton = CreateGraphicsButton("[ Integrated ]", GraphicsSwitcherMode.UMAMode, "Integrated");
        _hybridButton = CreateGraphicsButton("[ Hybrid ]", GraphicsSwitcherMode.Hybrid, "Hybrid");
        graphicsPanel.Controls.Add(_umaButton);
        graphicsPanel.Controls.Add(_hybridButton);

        layout.Controls.Add(summaryRow, 0, 0);
        layout.Controls.Add(graphicsPanel, 0, 1);
        graphicsGroup.Controls.Add(layout);
        return graphicsGroup;
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
        return button;
    }

    private async Task ApplyGraphicsModeAsync(GraphicsSwitcherMode mode, string label)
    {
        IWin32Window owner = FindForm() ?? (IWin32Window)this;
        DialogResult result = MessageBox.Show(
            owner,
            "Apply " + label + " mode? A restart is required for the change to take effect.",
            "Graphics Mode",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (result != DialogResult.Yes)
        {
            return;
        }

        bool success;
        try
        {
            success = await RequestGraphicsModeAsync(mode, label).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, "Failed to request graphics mode change: " + ex.Message, "Graphics Mode", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (!success)
        {
            MessageBox.Show(owner, "Failed to request graphics mode change. Check diagnostics or the log for support flags and the BIOS return code.", "Graphics Mode", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        DialogResult restartResult = MessageBox.Show(
            owner,
            "The BIOS accepted the graphics mode change. A reboot is required before it takes effect. Restart now?",
            "Graphics Mode",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (restartResult == DialogResult.Yes)
        {
            Process.Start("shutdown", "/r /t 0");
        }
    }

    private void UpdateSummaryLabels(PerformanceControlState state)
    {
        string graphicsText = FormatGraphicsMode(state.CurrentGraphicsMode);
        _graphicsSummaryLabel.Text = "Graphics Mode: " + graphicsText;
        _graphicsMetricsLabel.Text = "Support: UMA=" + state.GraphicsSupportsUma + " Hybrid=" + state.GraphicsSupportsHybrid + " | Reboot=" + state.GraphicsNeedsReboot;
    }

    private static string FormatGraphicsMode(string currentGraphicsMode)
    {
        return GraphicsSupportPolicy.FormatDisplayName(currentGraphicsMode);
    }

    private static void UpdateGraphicsButtonState(Button button, string title, bool supported, string currentGraphicsMode, GraphicsSwitcherMode representedMode)
    {
        bool isCurrent = string.Equals(currentGraphicsMode, representedMode.ToString(), StringComparison.OrdinalIgnoreCase);
        button.Enabled = supported && !isCurrent;
        button.BackColor = isCurrent ? Color.LightGreen : SystemColors.Control;
        button.Text = title;
    }
}
