using System;
using System.Globalization;
using System.Windows.Data;

namespace Sheas_Cealer.Convs;

internal class MainPowerButtonIsEnabledConv : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        bool isNginxExist = values[0] is bool exist && exist;
        bool isNginxIniting = values[1] is bool initing && initing;
        bool isAdmin = values[2] is bool admin && admin;

        return isNginxExist && !isNginxIniting && isAdmin;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotImplementedException();
}
