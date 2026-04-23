using System;
using System.Drawing;
using System.Windows.Forms;

namespace OmenHelper.Presentation.Forms;

internal sealed class DiagnosticsForm : Form
{
    private readonly Func<System.Threading.Tasks.Task<string>> _reportProvider;
    private readonly Func<int, byte[], string, int, System.Threading.Tasks.Task<string>> _probeProvider;
    private readonly Timer _refreshTimer = new Timer();
    private readonly TextBox _textBox = new TextBox();
    private bool _refreshing;

    public DiagnosticsForm(Func<System.Threading.Tasks.Task<string>> reportProvider, Func<int, byte[], string, int, System.Threading.Tasks.Task<string>> probeProvider)
    {
        _reportProvider = reportProvider;
        _probeProvider = probeProvider;

        Text = _probeProvider == null ? "OMEN Helper Advanced Diagnostics" : "OMEN Helper Diagnostics";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(900, 700);
        MinimumSize = new Size(700, 500);

        _textBox.Dock = DockStyle.Fill;
        _textBox.Multiline = true;
        _textBox.ReadOnly = true;
        _textBox.WordWrap = false;
        _textBox.ScrollBars = ScrollBars.Both;
        _textBox.Font = new Font("Consolas", 9F, FontStyle.Regular, GraphicsUnit.Point);

        FlowLayoutPanel topBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(8)
        };

        Button refreshButton = new Button
        {
            Text = "Refresh",
            AutoSize = true
        };
        refreshButton.Click += async (_, __) => await RefreshReportAsync();
        topBar.Controls.Add(refreshButton);

        Button copyButton = new Button
        {
            Text = "Copy Report",
            AutoSize = true
        };
        copyButton.Click += (_, __) =>
        {
            if (!string.IsNullOrWhiteSpace(_textBox.Text))
            {
                Clipboard.SetText(_textBox.Text);
            }
        };
        topBar.Controls.Add(copyButton);

        if (_probeProvider != null)
        {
            AddProbeButton(topBar, "[1,0,0,0]", 35, new byte[] { 1, 0, 0, 0 });
            AddProbeButton(topBar, "[0,1,0,0]", 35, new byte[] { 0, 1, 0, 0 });
            AddProbeButton(topBar, "[0,0,1,0]", 35, new byte[] { 0, 0, 1, 0 });
            AddProbeButton(topBar, "[1,1,1,0]", 35, new byte[] { 1, 1, 1, 0 });
            AddProbeButton(topBar, "idx 0", 35, new byte[] { 0, 0, 0, 0 });
            AddProbeButton(topBar, "idx 1", 35, new byte[] { 1, 0, 0, 0 });
            AddProbeButton(topBar, "idx 2", 35, new byte[] { 2, 0, 0, 0 });
            AddProbeButton(topBar, "idx 3", 35, new byte[] { 3, 0, 0, 0 });
            AddProbeButton(topBar, "idx 4", 35, new byte[] { 4, 0, 0, 0 });
            AddProbeButton(topBar, "idx 5", 35, new byte[] { 5, 0, 0, 0 });
            AddProbeButton(topBar, "dump128", 35, new byte[] { 1, 0, 0, 0 }, 128);
            AddProbeButton(topBar, "cmd45", 45, new byte[] { 1, 0, 0, 0 }, 128);
            AddProbeButton(topBar, "cmd46", 46, new byte[] { 1, 0, 0, 0 }, 128);
        }
        else
        {
            Label noteLabel = new Label
            {
                AutoSize = true,
                Margin = new Padding(12, 8, 0, 0),
                Text = "Developer probe buttons are hidden in normal builds. Set OMENHELPER_ENABLE_DEVTOOLS=1 to re-enable them."
            };
            topBar.Controls.Add(noteLabel);
        }

        Controls.Add(_textBox);
        Controls.Add(topBar);

        Load += async (_, __) =>
        {
            _refreshTimer.Interval = 5000;
            _refreshTimer.Tick += async (_, ____) => await RefreshReportAsync();
            _refreshTimer.Start();
            await RefreshReportAsync();
        };

        FormClosing += (_, __) =>
        {
            _refreshTimer.Stop();
            _refreshTimer.Dispose();
        };
    }

    private void AddProbeButton(FlowLayoutPanel topBar, string label, int commandType, byte[] input, int returnDataSize = 4)
    {
        Button button = new Button
        {
            Text = label,
            AutoSize = true
        };
        button.Click += async (_, __) => await RunProbeAsync(label, commandType, input, returnDataSize);
        topBar.Controls.Add(button);
    }

    private async System.Threading.Tasks.Task RunProbeAsync(string label, int commandType, byte[] input, int returnDataSize)
    {
        if (_probeProvider == null)
        {
            return;
        }

        string result = await _probeProvider(commandType, input, label, returnDataSize);
        if (!IsDisposed)
        {
            _textBox.AppendText(Environment.NewLine + result + Environment.NewLine);
        }
    }

    private async System.Threading.Tasks.Task RefreshReportAsync()
    {
        if (_refreshing || IsDisposed)
        {
            return;
        }

        _refreshing = true;
        try
        {
            string report = await _reportProvider();
            if (!IsDisposed)
            {
                _textBox.Text = report;
            }
        }
        finally
        {
            _refreshing = false;
        }
    }
}
