using MaterialDesignThemes.Wpf;
using Sheas_Cealer.Preses;
using System;
using System.Globalization;
using System.Windows.Data;

namespace Sheas_Cealer.Convs;

internal class MainSiteCheckIconConv : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        SiteCheckState.Checking => PackIconKind.ProgressClock,
        SiteCheckState.Ok => PackIconKind.CheckCircle,
        SiteCheckState.Fail => PackIconKind.CloseCircle,
        _ => PackIconKind.CircleOutline
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}
