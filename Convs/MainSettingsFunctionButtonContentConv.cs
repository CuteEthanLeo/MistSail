using MistSail.Consts;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Data;

namespace MistSail.Convs;

internal class MainSettingsFunctionButtonContentConv : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        MainConst.SettingsMode settingsMode = (MainConst.SettingsMode)value;

        return settingsMode switch
        {
            MainConst.SettingsMode.BrowserPathMode => MainConst.SettingsFunctionButtonBrowserPathContent,
            MainConst.SettingsMode.UpstreamUrlMode => MainConst.SettingsFunctionButtonUpstreamUrlContent,
            MainConst.SettingsMode.ExtraArgsMode => MainConst.SettingsFunctionButtonExtraArgsContent,
            _ => throw new UnreachableException()
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}