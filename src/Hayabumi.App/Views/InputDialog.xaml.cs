using System.Windows;

namespace Hayabumi.App.Views;

/// <summary>1 行の文字を入力してもらう小さな画面。</summary>
public partial class InputDialog : Window
{
    public InputDialog(string title, string prompt, string initialValue = "")
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        ValueBox.Text = initialValue;
        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    public string Value => ValueBox.Text.Trim();

    public static string? Ask(Window owner, string title, string prompt, string initialValue = "")
    {
        var dialog = new InputDialog(title, prompt, initialValue) { Owner = owner };
        return dialog.ShowDialog() == true && dialog.Value.Length > 0 ? dialog.Value : null;
    }

    private void OnOkClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
