using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using ClipRing.Core;
using ClipRing.Services;

namespace ClipRing.UI;

/// <summary>Seconds (double) → "m:ss".</summary>
public sealed class DurationConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => ReplayService.FormatDuration(value is double d ? d : System.Convert.ToDouble(value, CultureInfo.InvariantCulture));

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>ReplayState → the colour of the status light.</summary>
public sealed class StateBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string key = value switch
        {
            ReplayState.Running => "Brush.Live",
            ReplayState.Starting or ReplayState.Initializing or ReplayState.Error => "Brush.Warn",
            _ => "Brush.Faint",
        };
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Visible when the value is 0 / empty (for empty states). Parameter "invert" flips it.</summary>
public sealed class EmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool empty = value switch
        {
            null => true,
            int i => i == 0,
            string s => string.IsNullOrWhiteSpace(s),
            ICollection c => c.Count == 0,
            _ => false,
        };
        if (parameter is string p && p == "invert") empty = !empty;
        return empty ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>bool → Visibility, with "invert" parameter.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool v = value is true;
        if (parameter is string p && p == "invert") v = !v;
        return v ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
