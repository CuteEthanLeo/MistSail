using MaterialDesignThemes.Wpf;
using MistSail.Preses;
using System;
using System.Globalization;
using System.Windows.Data;

namespace MistSail.Convs;

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
