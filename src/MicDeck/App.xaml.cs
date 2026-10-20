using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using MicDeck.Configuration;
using MicDeck.Services;
using MicDeck.ViewModels;
using MicDeck.Views;

namespace MicDeck;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private TrayService? _tray;

    public static AppSettings Settings { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, @"Local\MicDeck.Soundboard", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("MicDeck is already running.", "MicDeck",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            CrashLog.Write(args.Exception);
            MessageBox.Show(
                "MicDeck hit an unexpected error.\n\n" +
                args.Exception.Message +
                "\n\nDetails were written to:\n" + CrashLog.Path,
                "MicDeck", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        try
        {
            SetCurrentProcessExplicitAppUserModelID("MicDeck.Soundboard");
        }
        catch
        {
            // Cosmetic only: groups the taskbar entry under the app icon.
        }

        Settings = SettingsStore.Load();
        ThemeManager.Apply(Settings.Theme, Settings.Accent);

        var viewModel = new MainViewModel(Settings);
        var window = new MainWindow(viewModel);
        MainWindow = window;

        _tray = new TrayService(window, viewModel);
        _tray.Attach();

        // Show once even when starting in the tray: the window handle must exist
        // before global hotkeys can be registered, and TrayService hides it again
        // through the normal minimize path.
        if (Settings.StartMinimized && Settings.MinimizeToTray)
        {
            window.Opacity = 0;
            window.Show();
            window.WindowState = WindowState.Minimized;
            window.Opacity = 1;
        }
        else
        {
            window.Show();
        }

        // Enumerate devices and open the WASAPI streams only after the first
        // frame, so the window appears immediately.
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(viewModel.Initialize));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();

        // Persist synchronously: the debounced save timer is gone by now.
        SettingsStore.SaveNow();

        try
        {
            _singleInstance?.ReleaseMutex();
        }
        catch
        {
            // Already released.
        }

        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", SetLastError = true)]
    private static extern void SetCurrentProcessExplicitAppUserModelID(
        [System.Runtime.InteropServices.MarshalAs(
            System.Runtime.InteropServices.UnmanagedType.LPWStr)]
        string appId);
}

internal static class CrashLog
{
    public static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MicDeck", "error.log");

    public static void Write(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.AppendAllText(Path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {exception}\n\n");
        }
        catch
        {
            // Never let logging take down the process.
        }
    }
}
