using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MistSail.Convs;

internal class MainNonAdminVisibilityConv : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool isAdmin = value is bool admin && admin;

        return isAdmin ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}
