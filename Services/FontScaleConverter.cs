using System.Globalization;
using System.Windows.Data;

namespace SessionDeck.Services;

/// <summary>
/// Multiplies a design-time font size by the user's task-font scale (A+ / A− on the
/// toolbar). Bound as: ConverterParameter = the size the control would have had, value =
/// the scale.
///
/// A converter rather than plain WPF font inheritance: FontSize does inherit, but every
/// TextBlock on a task card sets its own size on purpose (the id is smaller than the name,
/// which is smaller than nothing else), and an inherited value loses to a local one. Going
/// through the scale keeps those relative sizes intact instead of flattening the card to
/// one size.
/// </summary>
public sealed class FontScaleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double scale = value is double d && d > 0.1 ? d : 1.0;
        double baseSize = 12.0;
        if (parameter is string s &&
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            baseSize = parsed;
        return Math.Round(baseSize * scale, 2);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
