using System.Windows;
using System.Windows.Controls;

namespace LineCast;

/// <summary>
/// Simple text-input dialog — equivalent to PyQt5's QInputDialog.getText().
/// </summary>
public partial class InputDialog : Window
{
    public string Result { get; private set; } = string.Empty;

    public InputDialog(string title, string prompt, string defaultValue = "")
    {
        Title  = title;
        Width  = 380;
        Height = 160;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = System.Windows.Media.Brushes.Black;

        var stack = new StackPanel { Margin = new Thickness(16) };

        var lbl = new Label
        {
            Content    = prompt,
            Foreground = System.Windows.Media.Brushes.White
        };

        var box = new TextBox
        {
            Text       = defaultValue,
            Background = System.Windows.Media.Brushes.Black,
            Foreground = System.Windows.Media.Brushes.White,
            BorderBrush = System.Windows.Media.Brushes.Gray,
            Margin     = new Thickness(0, 4, 0, 8)
        };

        var btns = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var ok = new Button { Content = "OK", Width = 72, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Width = 72 };

        ok.Click     += (_, _) => { Result = box.Text; DialogResult = true; };
        cancel.Click += (_, _) => { DialogResult = false; };

        btns.Children.Add(ok);
        btns.Children.Add(cancel);
        stack.Children.Add(lbl);
        stack.Children.Add(box);
        stack.Children.Add(btns);
        Content = stack;

        Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
    }
}
