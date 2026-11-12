using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Hayabumi.App.ViewModels;

public sealed class NullToVisibilityConverter : IValueConverter
{
    public static readonly NullToVisibilityConverter Instance = new(false);
    public static readonly NullToVisibilityConverter InverseInstance = new(true);

    private readonly bool _inverse;

    private NullToVisibilityConverter(bool inverse) => _inverse = inverse;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value is null) ^ _inverse ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class IndentConverter : IValueConverter
{
    public static readonly IndentConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => new Thickness(value is double d ? d : 0, 0, 0, 0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
