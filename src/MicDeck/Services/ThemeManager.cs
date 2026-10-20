using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace MicDeck.Services;

public static class ThemeManager
{
    private static readonly Dictionary<string, (string Base, string Hover, string Pressed)> Accents = new()
    {
        ["cyan"]    = ("#38BDF8", "#62CDFA", "#0EA5E9"),
        ["violet"]  = ("#A78BFA", "#BFA9FC", "#8B5CF6"),
        ["emerald"] = ("#34D399", "#5FE3B1", "#10B981"),
        ["amber"]   = ("#FBBF24", "#FCD34D", "#F59E0B"),
        ["rose"]    = ("#FB7185", "#FDA4AF", "#F43F5E"),
        ["blue"]    = ("#60A5FA", "#93C5FD", "#3B82F6"),
    };

    public static IReadOnlyCollection<string> AccentNames => Accents.Keys;

    public static bool IsDark(string theme) =>
        !string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase);

    public static void Apply(string theme, string accent)
    {
        var isDark = IsDark(theme);
        var source = new Uri(
            isDark ? "pack://application:,,,/Themes/Palette.Dark.xaml"
                   : "pack://application:,,,/Themes/Palette.Light.xaml",
            UriKind.Absolute);

        var dictionaries = Application.Current.Resources.MergedDictionaries;

        // Remove any palette loaded from App.xaml so switching themes replaces it
        // instead of stacking a second palette underneath.
        for (var i = dictionaries.Count - 1; i >= 0; i--)
        {
            var existing = dictionaries[i].Source?.OriginalString;
            if (existing is not null &&
                existing.Contains("Palette.", StringComparison.OrdinalIgnoreCase))
            {
                dictionaries.RemoveAt(i);
            }
        }

        dictionaries.Insert(0, new ResourceDictionary { Source = source });

        ApplyAccent(accent, isDark);
    }

    private static void ApplyAccent(string name, bool isDark)
    {
        if (!Accents.TryGetValue(name ?? "", out var value))
        {
            value = Accents["cyan"];
        }

        var baseColor = (Color)ColorConverter.ConvertFromString(value.Base);
        var hoverColor = (Color)ColorConverter.ConvertFromString(value.Hover);
        var pressedColor = (Color)ColorConverter.ConvertFromString(value.Pressed);

        // On light themes the bright accent is unreadable as small text, so text
        // usage falls back to the darker pressed variant.
        var textColor = isDark ? baseColor : pressedColor;

        var resources = Application.Current.Resources;
        resources["Accent"] = Freeze(new SolidColorBrush(baseColor));
        resources["Accent.Hover"] = Freeze(new SolidColorBrush(hoverColor));
        resources["Accent.Pressed"] = Freeze(new SolidColorBrush(pressedColor));
        resources["Accent.Text"] = Freeze(new SolidColorBrush(textColor));
        resources["Accent.On"] = Freeze(new SolidColorBrush(
            baseColor.R * 0.299 + baseColor.G * 0.587 + baseColor.B * 0.114 > 160
                ? Color.FromRgb(0x08, 0x11, 0x1C)
                : Colors.White));
        resources["Accent.Soft"] = Freeze(new SolidColorBrush(Color.FromArgb(
            isDark ? (byte)0x2E : (byte)0x1E, baseColor.R, baseColor.G, baseColor.B)));
    }

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    public static void ApplyTitleBar(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var enabled = IsDark(App.Settings.Theme) ? 1 : 0;
            // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Win10 2004+), 19 on older builds.
            if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
            }
        }
        catch
        {
            // Cosmetic only.
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attribute, ref int value, int size);
}
