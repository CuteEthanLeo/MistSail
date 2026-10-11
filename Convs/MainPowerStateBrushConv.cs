using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Sheas_Cealer.Convs;

internal class MainPowerStateBrushConv : IMultiValueConverter
{
    private static readonly SolidColorBrush Idle = Freeze(Color.FromRgb(0x9E, 0x9E, 0x9E));
    private static readonly SolidColorBrush Connecting = Freeze(Color.FromRgb(0xFF, 0xA0, 0x00));
    private static readonly SolidColorBrush Connected = Freeze(Color.FromRgb(0x43, 0xA0, 0x47));

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        bool isNginxRunning = values[0] is bool running && running;
        bool isNginxIniting = values[1] is bool initing && initing;

        return isNginxIniting ? Connecting : isNginxRunning ? Connected : Idle;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotImplementedException();

    private static SolidColorBrush Freeze(Color color)
    {
        SolidColorBrush brush = new(color);
        brush.Freeze();
        return brush;
    }
}
