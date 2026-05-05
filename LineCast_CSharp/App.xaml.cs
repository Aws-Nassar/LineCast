using System.Windows;

namespace LineCast;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Set Windows App User Model ID for taskbar grouping
        try
        {
            SetCurrentProcessExplicitAppUserModelID("LineCast.Soundboard");
        }
        catch { /* non-critical */ }

        var window = new MainWindow();
        window.Show();
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", SetLastError = true)]
    private static extern void SetCurrentProcessExplicitAppUserModelID(
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)]
        string appId);
}
