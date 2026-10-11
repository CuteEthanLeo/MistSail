using Sheas_Cealer.Consts;
using System;
using System.Globalization;
using System.Windows.Data;

namespace Sheas_Cealer.Convs;

internal class MainStatusCaptionConv : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        bool isNginxRunning = values[0] is bool running && running;
        bool isNginxIniting = values[1] is bool initing && initing;

        return isNginxRunning && !isNginxIniting ? MainConst.StatusCaptionConnected : MainConst.StatusCaptionIdle;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotImplementedException();
}
