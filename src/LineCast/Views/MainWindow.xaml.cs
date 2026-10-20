using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LineCast.Configuration;
using LineCast.Services;
using LineCast.ViewModels;
using Microsoft.Win32;

namespace LineCast.Views;

public partial class MainWindow : Window
{
    private const string AudioFilter =
        "Audio files|*.wav;*.mp3;*.m4a;*.aac;*.flac;*.ogg;*.opus;*.wma;*.aiff;*.aif|All files|*.*";

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".wav", ".mp3", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".wma", ".aiff", ".aif",
    };

    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _seekCommit;
    private HotkeyService? _hotkeys;
    private DateTime _lastDeviceRefresh = DateTime.MinValue;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        _seekCommit = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(160),
        };
        _seekCommit.Tick += (_, _) =>
        {
            _seekCommit.Stop();
            _viewModel.CommitSeek(SeekSlider.Value);
        };

        SeekSlider.ValueChanged += OnSeekValueChanged;

        _viewModel.HotkeysChanged += RefreshHotkeys;
        Loaded += OnLoaded;
        Closed += OnClosed;
        Activated += OnActivated;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        ThemeManager.ApplyTitleBar(this);
        _hotkeys = new HotkeyService(this);
        RefreshHotkeys();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateThemeChips();
        UpdateAccentSwatches();

        // The view model initialises audio right after the first frame; do not
        // also enumerate on the activation that follows.
        _lastDeviceRefresh = DateTime.UtcNow;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _seekCommit.Stop();
        _viewModel.HotkeysChanged -= RefreshHotkeys;

        _hotkeys?.Dispose();
        _hotkeys = null;

        _viewModel.Dispose();
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        // Endpoints appear and disappear while the app sits in the tray. Polling
        // on activation is enough and avoids a COM notification client.
        if ((DateTime.UtcNow - _lastDeviceRefresh).TotalSeconds < 5)
        {
            return;
        }

        _lastDeviceRefresh = DateTime.UtcNow;
        _viewModel.RefreshDevices();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var typing = Keyboard.FocusedElement is TextBoxBase or ComboBox;

        if (e.Key == Key.Space && !typing)
        {
            _viewModel.TogglePlayPause();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Delete && !typing && SoundGrid.SelectedItem is SoundItemViewModel selected)
        {
            ConfirmRemove(selected);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (_viewModel.IsSettingsOpen)
            {
                _viewModel.IsSettingsOpen = false;
            }
            else if (_viewModel.IsPlaying)
            {
                _viewModel.Stop();
            }

            e.Handled = true;
        }
    }

    // ── Library ────────────────────────────────────────────────────────────

    private void OnAddSounds(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Add sounds to LineCast",
            Filter = AudioFilter,
            Multiselect = true,
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.AddPaths(dialog.FileNames);
        }
    }

    private void OnSoundDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IsInsideButton(e.OriginalSource) ||
            SoundGrid.SelectedItem is not SoundItemViewModel item)
        {
            return;
        }

        if (item.IsMissing)
        {
            Relink(item);
            return;
        }

        _viewModel.PlayItem(item);
        e.Handled = true;
    }

    private void OnRenameSound(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SoundItemViewModel item)
        {
            return;
        }

        var dialog = new TextPromptWindow(
            "Rename sound",
            "This is the label shown in the library and in the hotkey list.",
            item.Name)
        {
            Owner = this,
        };

        if (dialog.ShowDialog() == true)
        {
            item.Name = dialog.Value;
        }
    }

    private void OnRemoveSound(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SoundItemViewModel item)
        {
            ConfirmRemove(item);
        }
    }

    private void ConfirmRemove(SoundItemViewModel item)
    {
        var result = MessageBox.Show(
            this,
            $"Remove \"{item.Name}\" from the library?\n\nThe audio file itself is not deleted.",
            "LineCast",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (result == MessageBoxResult.Yes)
        {
            _viewModel.Remove(item);
        }
    }

    private void Relink(SoundItemViewModel item)
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Locate \"{item.Name}\"",
            Filter = AudioFilter,
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.Relink(item, dialog.FileName);
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void OnFilesDropped(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files)
        {
            return;
        }

        var audio = files.Where(f => AudioExtensions.Contains(Path.GetExtension(f))).ToArray();
        if (audio.Length > 0)
        {
            _viewModel.AddPaths(audio);
        }
    }

    // ── Transport ──────────────────────────────────────────────────────────

    private void OnPlayPause(object sender, RoutedEventArgs e) => _viewModel.TogglePlayPause();

    private void OnStopClick(object sender, RoutedEventArgs e) => _viewModel.Stop();

    private void OnSeekValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_viewModel.IsTickUpdate)
        {
            return;
        }

        _viewModel.BeginSeek();
        _seekCommit.Stop();
        _seekCommit.Start();
    }

    // ── Settings overlay ───────────────────────────────────────────────────

    private void OnScrimClick(object sender, MouseButtonEventArgs e) => _viewModel.IsSettingsOpen = false;

    private void OnCloseSettings(object sender, RoutedEventArgs e) => _viewModel.IsSettingsOpen = false;

    private void OnThemeDark(object sender, RoutedEventArgs e)
    {
        _viewModel.SetTheme("dark");
        UpdateThemeChips();
    }

    private void OnThemeLight(object sender, RoutedEventArgs e)
    {
        _viewModel.SetTheme("light");
        UpdateThemeChips();
    }

    private void OnAccentClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string accent })
        {
            _viewModel.SetAccent(accent);
            UpdateAccentSwatches();
        }
    }

    private void UpdateThemeChips()
    {
        DarkChip.IsChecked = _viewModel.ThemeIsDark;
        LightChip.IsChecked = !_viewModel.ThemeIsDark;
    }

    private void UpdateAccentSwatches()
    {
        foreach (var swatch in AccentPanel.Children.OfType<ToggleButton>())
        {
            swatch.IsChecked = string.Equals(
                swatch.Tag as string,
                _viewModel.AccentName,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    private void OnHotkeyCapture(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box)
        {
            return;
        }

        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key is Key.Back or Key.Delete)
        {
            AssignHotkey(box, string.Empty);
            return;
        }

        if (key is Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin
            or Key.Tab)
        {
            return;
        }

        var modifiers = Keyboard.Modifiers;
        var isFunctionKey = key is >= Key.F1 and <= Key.F24;

        // A bare letter would be swallowed system-wide, so require a modifier
        // unless it is a function key.
        if (modifiers == ModifierKeys.None && !isFunctionKey)
        {
            return;
        }

        AssignHotkey(box, HotkeyService.FormatGesture(key, modifiers));
    }

    private void AssignHotkey(TextBox box, string gesture)
    {
        switch (box.Tag as string)
        {
            case "play":
                _viewModel.HotkeyPlay = gesture;
                break;
            case "stop":
                _viewModel.HotkeyStop = gesture;
                break;
            case "mic":
                _viewModel.HotkeyToggleMic = gesture;
                break;
        }

        box.Text = gesture;
    }

    private void RefreshHotkeys()
    {
        if (_hotkeys is null)
        {
            return;
        }

        _hotkeys.Clear();
        _viewModel.ClearHotkeyErrors();

        foreach (var binding in _viewModel.GetHotkeyBindings())
        {
            if (string.IsNullOrWhiteSpace(binding.Gesture))
            {
                continue;
            }

            if (!_hotkeys.TryRegister(binding.Gesture, binding.Action))
            {
                _viewModel.ReportHotkeyConflict(binding.Label, binding.Gesture, binding.Owner);
            }
        }
    }

    private void OnOpenSettingsFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.Directory);
            Process.Start(new ProcessStartInfo(SettingsStore.Directory)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "LineCast",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static bool IsInsideButton(object? source)
    {
        var node = source as DependencyObject;

        while (node is not null)
        {
            if (node is ButtonBase)
            {
                return true;
            }

            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return false;
    }
}
