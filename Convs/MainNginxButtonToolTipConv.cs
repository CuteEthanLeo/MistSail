using MistSail.Consts;
using System;
using System.Globalization;
using System.Windows.Data;

namespace MistSail.Convs;

internal class MainNginxButtonToolTipConv : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool isNginxRunning = (bool)value;

        return isNginxRunning ? MainConst.NginxButtonIsRunningToolTip : MainConst.NginxButtonIsStoppedToolTip;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}