using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using OmenHelper.Application.Controllers;
using OmenHelper.Application.State;
using OmenHelper.Domain.Fan;
using OmenHelper.Presentation.Controls;

namespace OmenHelper.Presentation.Forms;

internal sealed class MainForm : Form
{
    private readonly OmenSessionController _controller = new OmenSessionController();
    private readonly PerformancePanel _performancePanel = new PerformancePanel();
    private readonly GraphicsPanel _graphicsPanel = new GraphicsPanel();
    private readonly LogPanel _logPanel = new LogPanel();
    private readonly Button _diagnosticsButton = new Button();
    private readonly NotifyIcon _trayIcon = new NotifyIcon();
    private readonly ContextMenuStrip _trayMenu = new ContextMenuStrip();
    private DiagnosticsForm _diagnosticsForm;
    private readonly Timer _powerModeTimer = new Timer();
    private bool _allowExit;
    private bool _trayHintShown;

    public MainForm()
    {
        Text = "OMEN Helper BIOS Control";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1040, 760);
        Size = new Size(1180, 860);

        InitializeUi();
        InitializeTrayIcon();

        Load += OnLoad;
        Resize += OnResize;
        FormClosing += OnFormClosing;
        _powerModeTimer.Interval = 3000;
        _powerModeTimer.Tick += async (_, __) => await SyncPowerSourceModeAsync();
    }

    private void InitializeUi()
    {
        Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point);

        _performancePanel.PerformanceModeRequested += async (_, mode) => await _controller.SetPerformanceModeAsync(mode);
        _performancePanel.MaxFanRequested += async (_, enabled) => await _controller.SetMaxFanAsync(enabled);
        _performancePanel.FanMinimumRequested += async (_, rpm) => await _controller.SetFanMinimumOverrideRpmAsync(rpm);
        _performancePanel.BatteryPreferenceChanged += (_, mode) => _controller.SetBatteryPowerModePreference(mode);
        _performancePanel.PluggedPreferenceChanged += (_, mode) => _controller.SetPluggedInPowerModePreference(mode);
        _performancePanel.PowerModeSelectionsChanged += async (_, __) => await SyncPowerSourceModeAsync();
        _performancePanel.FanCurveRuntimeEnabledChanged += (_, enabled) => _controller.SetFanCurveRuntimeEnabled(enabled);
        _performancePanel.GpuCurveLinkedChanged += (_, linked) => _controller.SetFanCurveGpuLinked(linked);
        _performancePanel.FanCurveHysteresisChanged += async (_, args) => await _controller.SetFanCurveHysteresisAsync(args.RiseDeltaC, args.DropDeltaC);
        _performancePanel.CpuCurveCommitted += (_, profile) => _controller.SetFanCurveProfile(FanCurveKind.Cpu, profile);
        _performancePanel.GpuCurveCommitted += (_, profile) => _controller.SetFanCurveProfile(FanCurveKind.Gpu, profile);
        _performancePanel.ChassisCurveCommitted += (_, profile) => _controller.SetFanCurveProfile(FanCurveKind.Chassis, profile);

        _graphicsPanel.RequestGraphicsModeAsync = async (mode, label) => await _controller.SetGraphicsModeAsync(mode);

        FlowLayoutPanel topBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = false,
            FlowDirection = FlowDirection.RightToLeft,
            Margin = new Padding(0, 0, 0, 12)
        };

        _diagnosticsButton.Text = "Advanced Diagnostics";
        _diagnosticsButton.AutoSize = true;
        _diagnosticsButton.Click += (_, __) => ShowDiagnosticsWindow();
        _diagnosticsButton.Visible = true;
        topBar.Controls.Add(_diagnosticsButton);

        TableLayoutPanel root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(16)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        root.Controls.Add(topBar, 0, 0);
        root.Controls.Add(_performancePanel, 0, 1);
        root.Controls.Add(_graphicsPanel, 0, 2);
        root.Controls.Add(_logPanel, 0, 3);

        Controls.Add(root);
    }

    private void OnLoad(object sender, EventArgs e)
    {
        _controller.StateChanged += ControllerOnStateChanged;
        _controller.LogMessage += ControllerOnLogMessage;
        _controller.Start();
        LoadPowerModePreferencesIntoUi();
        _powerModeTimer.Start();
        _trayIcon.Visible = true;
        _ = SyncPowerSourceModeAsync();
    }

    private void OnResize(object sender, EventArgs e)
    {
        if (WindowState == FormWindowState.Minimized)
        {
            HideToTray(showBalloonTip: !_trayHintShown);
        }
    }

    private void OnFormClosing(object sender, FormClosingEventArgs e)
    {
        if (!_allowExit && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideToTray(showBalloonTip: !_trayHintShown);
            return;
        }

        _powerModeTimer.Stop();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _trayMenu.Dispose();
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
        _performancePanel.ApplyState(safeState);
        _graphicsPanel.ApplyState(safeState);
    }

    private void ControllerOnLogMessage(object sender, string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<object, string>(ControllerOnLogMessage), sender, message);
            return;
        }

        _logPanel.AppendMessage(message);
    }

    private void LoadPowerModePreferencesIntoUi()
    {
        _performancePanel.LoadPowerModePreferences(_controller.GetBatteryPowerModePreference(), _controller.GetPluggedInPowerModePreference());
        _performancePanel.RefreshPowerSourceLabel();
    }

    private async Task SyncPowerSourceModeAsync()
    {
        PowerLineStatus powerLineStatus = SystemInformation.PowerStatus.PowerLineStatus;
        _performancePanel.RefreshPowerSourceLabel();

        if (powerLineStatus == PowerLineStatus.Unknown)
        {
            return;
        }

        bool pluggedIn = powerLineStatus == PowerLineStatus.Online;
        await _controller.SyncPowerSourcePerformanceModeAsync(pluggedIn).ConfigureAwait(true);
    }


    private void ShowDiagnosticsWindow()
    {
        ShowFromTray();

        if (_diagnosticsForm != null && !_diagnosticsForm.IsDisposed)
        {
            _diagnosticsForm.Focus();
            return;
        }

        _diagnosticsForm = new DiagnosticsForm(
            () => _controller.BuildDiagnosticsReportAsync(),
            BuildFeatures.DeveloperToolsEnabled
                ? new Func<int, byte[], string, int, Task<string>>((commandType, input, label, outSize) => _controller.ProbeTemperatureCommandAsync(commandType, input, label, outSize))
                : null);
        _diagnosticsForm.Show(this);
    }

    private void InitializeTrayIcon()
    {
        ToolStripMenuItem openItem = new ToolStripMenuItem("Open OMEN Helper");
        openItem.Click += (_, __) => ShowFromTray();

        ToolStripMenuItem diagnosticsItem = new ToolStripMenuItem("Diagnostics");
        diagnosticsItem.Click += (_, __) => ShowDiagnosticsWindow();

        ToolStripMenuItem exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, __) => ExitApplication();

        _trayMenu.Items.Add(openItem);
        _trayMenu.Items.Add(diagnosticsItem);
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add(exitItem);

        _trayIcon.Text = "OMEN Helper";
        _trayIcon.Icon = Icon.ExtractAssociatedIcon(System.Windows.Forms.Application.ExecutablePath) ?? SystemIcons.Application;
        _trayIcon.ContextMenuStrip = _trayMenu;
        _trayIcon.DoubleClick += (_, __) => ShowFromTray();
    }

    private void HideToTray(bool showBalloonTip)
    {
        if (!Visible && !ShowInTaskbar)
        {
            return;
        }

        Hide();
        ShowInTaskbar = false;

        if (showBalloonTip)
        {
            _trayIcon.ShowBalloonTip(2000, "OMEN Helper", "Still running in the system tray.", ToolTipIcon.Info);
            _trayHintShown = true;
        }
    }

    private void ShowFromTray()
    {
        Show();
        ShowInTaskbar = true;
        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }

        Activate();
        BringToFront();
    }

    private void ExitApplication()
    {
        _allowExit = true;
        Close();
    }
}
