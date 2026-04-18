using System;
using WinFormsApplication = System.Windows.Forms.Application;
using OmenHelper.Presentation.Forms;

namespace OmenHelper;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        HpAssemblyResolver.Register();

        WinFormsApplication.EnableVisualStyles();
        WinFormsApplication.SetCompatibleTextRenderingDefault(false);
        WinFormsApplication.Run(new MainForm());
    }
}
