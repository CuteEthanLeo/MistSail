using Sheas_Cealer.Preses;
using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Sheas_Cealer.Convs;

internal class MainSiteCheckBrushConv : IValueConverter
{
    private static readonly SolidColorBrush Pending = Freeze(Color.FromRgb(0x9E, 0x9E, 0x9E));
    private static readonly SolidColorBrush Checking = Freeze(Color.FromRgb(0xFF, 0xA0, 0x00));
    private static readonly SolidColorBrush Ok = Freeze(Color.FromRgb(0x43, 0xA0, 0x47));
    private static readonly SolidColorBrush Fail = Freeze(Color.FromRgb(0xE5, 0x39, 0x35));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        SiteCheckState.Checking => Checking,
        SiteCheckState.Ok => Ok,
        SiteCheckState.Fail => Fail,
        _ => Pending
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();

    private static SolidColorBrush Freeze(Color color)
    {
        SolidColorBrush brush = new(color);
        brush.Freeze();
        return brush;
    }
}
