using System;
using System.Drawing;
using System.Windows.Forms;

namespace OmenHelper;

internal sealed class DiagnosticsForm : Form
{
    private readonly Func<System.Threading.Tasks.Task<string>> _reportProvider;
    private readonly Timer _refreshTimer = new Timer();
    private readonly TextBox _textBox = new TextBox();
    private bool _refreshing;

    public DiagnosticsForm(Func<System.Threading.Tasks.Task<string>> reportProvider)
    {
        _reportProvider = reportProvider;

        Text = "OMEN Helper Diagnostics";
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
