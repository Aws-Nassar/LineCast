using System.Windows;
using MicDeck.ViewModels;

namespace MicDeck.Services;

/// <summary>
/// Tray icon plus the "minimize to tray" behaviour. WinForms is referenced only
/// for NotifyIcon; all UI still runs in WPF.
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly Window _window;
    private readonly MainViewModel _viewModel;

    private System.Windows.Forms.NotifyIcon? _icon;
    private bool _balloonShown;

    public TrayService(Window window, MainViewModel viewModel)
    {
        _window = window;
        _viewModel = viewModel;
    }

    public void Attach()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();

        menu.Items.Add("Open MicDeck", null, (_, _) => Restore());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        var play = new System.Windows.Forms.ToolStripMenuItem("Play selected", null,
            (_, _) => _viewModel.PlaySelected());
        menu.Items.Add(play);

        menu.Items.Add("Stop playback", null, (_, _) => _viewModel.Stop());

        var mute = new System.Windows.Forms.ToolStripMenuItem("Mute microphone", null,
            (_, _) => _viewModel.MicEnabled = !_viewModel.MicEnabled);
        menu.Items.Add(mute);

        menu.Opening += (_, _) =>
            mute.Text = _viewModel.MicEnabled ? "Mute microphone" : "Unmute microphone";

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => System.Windows.Application.Current.Shutdown());

        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "MicDeck — soundboard",
            Visible = true,
            ContextMenuStrip = menu,
        };

        _icon.DoubleClick += (_, _) => Restore();
        _window.StateChanged += OnStateChanged;
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (!_viewModel.MinimizeToTray)
        {
            return;
        }

        if (_window.WindowState != WindowState.Minimized)
        {
            return;
        }

        _window.Hide();

        if (!_balloonShown && _icon is not null)
        {
            _balloonShown = true;
            try
            {
                _icon.ShowBalloonTip(2500, "MicDeck is still running",
                    "Hotkeys keep working. Double-click the tray icon to reopen.",
                    System.Windows.Forms.ToolTipIcon.Info);
            }
            catch
            {
                // Balloons are best effort.
            }
        }
    }

    private void Restore()
    {
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private static System.Drawing.Icon LoadIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/micdeck.ico"));

            if (resource is not null)
            {
                return new System.Drawing.Icon(resource.Stream);
            }
        }
        catch
        {
            // Fall through to the stock icon.
        }

        return System.Drawing.SystemIcons.Application;
    }

    public void Dispose()
    {
        _window.StateChanged -= OnStateChanged;

        if (_icon is not null)
        {
            _icon.Visible = false;
            _icon.Dispose();
            _icon = null;
        }
    }
}
