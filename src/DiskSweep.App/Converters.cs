using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using DiskSweep.Core.Models;

namespace DiskSweep.App;

public sealed class RiskToBrushConverter : IValueConverter
{
    /// <summary>"fg" (default) for the strong colour, "bg" for the tint.</summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool tint = parameter as string == "bg";
        string key = value switch
        {
            RiskLevel.Safe => tint ? "SafeTintBrush" : "SafeBrush",
            RiskLevel.Review => tint ? "ReviewTintBrush" : "ReviewBrush",
            _ => tint ? "RiskyTintBrush" : "RiskyBrush",
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool v = value switch
        {
            bool b => b,
            int i => i > 0,
            string s => !string.IsNullOrEmpty(s),
            null => false,
            _ => true,
        };
        return v ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Lets DataGrid columns (which aren't in the visual tree) bind to the tab's view model.</summary>
public sealed class BindingProxy : Freezable
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy));

    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override Freezable CreateInstanceCore() => new BindingProxy();
}
