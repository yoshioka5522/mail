using System.Windows.Media;

namespace Hayabumi.App.ViewModels;

/// <summary>差出人やアカウントの頭文字と、名前から決まる色（一覧で見分けやすくするため）。</summary>
public static class Avatar
{
    private static readonly Color[] s_palette =
    [
        Color.FromRgb(0x25, 0x63, 0xEB), Color.FromRgb(0x05, 0x96, 0x69), Color.FromRgb(0xD9, 0x77, 0x06),
        Color.FromRgb(0xDB, 0x27, 0x77), Color.FromRgb(0x7C, 0x3A, 0xED), Color.FromRgb(0x08, 0x91, 0xB2),
        Color.FromRgb(0xDC, 0x26, 0x26), Color.FromRgb(0x4F, 0x46, 0xE5), Color.FromRgb(0x65, 0xA3, 0x0D),
        Color.FromRgb(0x0D, 0x94, 0x88),
    ];

    private static readonly Dictionary<int, SolidColorBrush> s_brushes = [];

    public static string Initials(string? name)
    {
        var text = (name ?? string.Empty).Trim().Trim('"');
        if (text.Length == 0)
        {
            return "?";
        }

        var first = char.ConvertFromUtf32(char.ConvertToUtf32(text, 0));
        if (first.Length == 1 && char.IsAsciiLetter(first[0]))
        {
            // 英字の名前は「Taro Yamada」→「TY」
            var parts = text.Split([' ', '.', '_', '-', '@'], StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 && char.IsAsciiLetter(parts[1][0])
                ? string.Concat(char.ToUpperInvariant(parts[0][0]), char.ToUpperInvariant(parts[1][0]))
                : first.ToUpperInvariant();
        }

        // 日本語などは先頭の 1 文字
        return first;
    }

    public static Brush Brush(string? key)
    {
        var hash = 0;
        foreach (var c in (key ?? string.Empty).ToLowerInvariant())
        {
            hash = unchecked((hash * 31) + c);
        }

        var index = (int)((uint)hash % (uint)s_palette.Length);
        lock (s_brushes)
        {
            if (!s_brushes.TryGetValue(index, out var brush))
            {
                brush = new SolidColorBrush(s_palette[index]);
                brush.Freeze();
                s_brushes[index] = brush;
            }

            return brush;
        }
    }
}
