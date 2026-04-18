using System.Drawing;
using System.Windows.Forms;

namespace OmenHelper.Presentation.Controls;

internal sealed class LogPanel : UserControl
{
    private readonly TextBox _logTextBox = new TextBox();

    public LogPanel()
    {
        Dock = DockStyle.Fill;
        AutoSize = true;

        Controls.Add(BuildLogsGroup());
    }

    public void AppendMessage(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        _logTextBox.AppendText(message + System.Environment.NewLine);
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
}
