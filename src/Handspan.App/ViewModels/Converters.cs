using System.Globalization;
using Avalonia.Data.Converters;

namespace Handspan.App.ViewModels;

/// <summary>
/// Makes a selection tick fully opaque when selected and faint when not.
/// </summary>
/// <remarks>
/// A hidden checkbox is undiscoverable — a user cannot select what they cannot see they may select — while a
/// fully drawn one on every tile would clutter the grid. A faint mark reads as "available" and a solid one as
/// "chosen".
/// </remarks>
public sealed class SelectionOpacityConverter : IValueConverter
{
    public static SelectionOpacityConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? 1.0 : 0.35;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
