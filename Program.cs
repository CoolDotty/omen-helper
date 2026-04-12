using System;
using System.Windows.Forms;

namespace OmenHelper;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        HpAssemblyResolver.Register();

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}
