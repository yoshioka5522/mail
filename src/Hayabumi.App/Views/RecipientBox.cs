using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Hayabumi.Core.Contacts;

namespace Hayabumi.App.Views;

/// <summary>
/// 宛先の入力欄。入力中の語（最後のカンマ以降）でアドレス帳と履歴を探し、候補を表示する。
/// </summary>
public sealed class RecipientBox : TextBox
{
    private readonly Popup _popup;
    private readonly ListBox _list;
    private bool _replacing;

    public RecipientBox()
    {
        // TextBox を継承したコントロールにも、標準（Fluent）の TextBox の見た目を使う
        SetResourceReference(StyleProperty, typeof(TextBox));
        Padding = new Thickness(4, 3, 4, 3);
        VerticalContentAlignment = VerticalAlignment.Center;
        _list = new ListBox { MaxHeight = 240, MinWidth = 320, DisplayMemberPath = nameof(ContactSuggestion.Label) };
        _list.PreviewMouseLeftButtonUp += (_, _) => Accept();
        _popup = new Popup
        {
            Child = new Border { Child = _list, BorderThickness = new Thickness(1), BorderBrush = SystemColors.ActiveBorderBrush },
            PlacementTarget = this,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = false,
        };
        LostKeyboardFocus += (_, e) =>
        {
            if (!_list.IsKeyboardFocusWithin && e.NewFocus is not ListBoxItem)
            {
                _popup.IsOpen = false;
            }
        };
    }

    public ContactRepository? Contacts { get; set; }

    protected override void OnTextChanged(TextChangedEventArgs e)
    {
        base.OnTextChanged(e);
        if (_replacing || Contacts is null || !IsKeyboardFocusWithin)
        {
            return;
        }

        var token = CurrentToken();
        if (token.Length < 1)
        {
            _popup.IsOpen = false;
            return;
        }

        var suggestions = Contacts.Suggest(token);
        _list.ItemsSource = suggestions;
        _list.SelectedIndex = suggestions.Count > 0 ? 0 : -1;
        _popup.IsOpen = suggestions.Count > 0;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_popup.IsOpen)
        {
            switch (e.Key)
            {
                case Key.Down:
                    _list.SelectedIndex = Math.Min(_list.SelectedIndex + 1, _list.Items.Count - 1);
                    _list.ScrollIntoView(_list.SelectedItem);
                    e.Handled = true;
                    return;
                case Key.Up:
                    _list.SelectedIndex = Math.Max(_list.SelectedIndex - 1, 0);
                    _list.ScrollIntoView(_list.SelectedItem);
                    e.Handled = true;
                    return;
                case Key.Enter:
                case Key.Tab:
                    if (_list.SelectedItem is not null)
                    {
                        Accept();
                        e.Handled = true;
                        return;
                    }

                    break;
                case Key.Escape:
                    _popup.IsOpen = false;
                    e.Handled = true;
                    return;
            }
        }

        base.OnPreviewKeyDown(e);
    }

    private string CurrentToken()
    {
        var before = Text[..Math.Min(CaretIndex, Text.Length)];
        var start = before.LastIndexOfAny([',', ';', '、', '；']);
        return before[(start + 1)..].Trim();
    }

    private void Accept()
    {
        if (_list.SelectedItem is not ContactSuggestion suggestion)
        {
            return;
        }

        var caret = Math.Min(CaretIndex, Text.Length);
        var before = Text[..caret];
        var after = Text[caret..];
        var start = before.LastIndexOfAny([',', ';', '、', '；']) + 1;
        var nextSeparator = after.IndexOfAny([',', ';', '、', '；']);
        var rest = nextSeparator >= 0 ? after[nextSeparator..].TrimStart(',', ';', '、', '；', ' ') : string.Empty;

        var prefix = before[..start].TrimEnd();
        var inserted = (prefix.Length > 0 ? prefix + " " : string.Empty) + suggestion.Value + ", ";
        _replacing = true;
        Text = inserted + rest;
        _replacing = false;
        CaretIndex = inserted.Length;
        _popup.IsOpen = false;
        Focus();
    }
}
