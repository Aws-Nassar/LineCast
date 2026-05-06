using System.Windows;

namespace LineCast;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Suppress unhandled exceptions from crashing silently
        DispatcherUnhandledException += (_, ex) =>
        {
            MessageBox.Show(ex.Exception.ToString(), "LineCast Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };

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
        [System.Runtime.InteropServices.MarshalAs(
            System.Runtime.InteropServices.UnmanagedType.LPWStr)]
        string appId);
}
