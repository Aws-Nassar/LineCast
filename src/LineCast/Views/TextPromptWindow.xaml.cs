using System.Windows;
using LineCast.Services;

namespace LineCast.Views;

public partial class TextPromptWindow : Window
{
    public TextPromptWindow(string heading, string hint, string value)
    {
        InitializeComponent();

        Heading.Text = heading;
        Hint.Text = hint;
        Input.Text = value;

        Loaded += (_, _) =>
        {
            Input.Focus();
            Input.SelectAll();
        };

        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
    }

    public string Value => Input.Text.Trim();

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Input.Text))
        {
            Input.Focus();
            return;
        }

        DialogResult = true;
    }
}
