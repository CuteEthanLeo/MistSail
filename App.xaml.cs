using MaterialDesignThemes.Wpf;
using MistSail.Direct;
using MistSail.Preses;
using MistSail.Props;
using MistSail.Utils;
using MistSail.Wins;
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace MistSail;

public partial class App : Application
{
    private void App_OnStartup(object sender, StartupEventArgs e)
    {
        #region Upgrade Settings
        if (Settings.Default.IsUpgradeRequired)
        {
            Settings.Default.Upgrade();
            Settings.Default.IsUpgradeRequired = false;
            Settings.Default.Save();
        }
        #endregion Upgrade Settings

        #region Primary Color
        PaletteHelper paletteHelper = new();
        Theme newTheme = paletteHelper.GetTheme();
        Color newPrimaryColor = Color.FromRgb(Settings.Default.PrimaryColor.R, Settings.Default.PrimaryColor.G, Settings.Default.PrimaryColor.B);

        newTheme.SetPrimaryColor(newPrimaryColor);
        paletteHelper.SetTheme(newTheme);
        #endregion Primary Color

        #region Background Color
        if (Environment.OSVersion.Version.Build < 22000)
        {
            Style newWindowStyle = new(typeof(Window), Current.Resources["CommonWindow"] as Style);

            newWindowStyle.Setters.Add(new Setter(Window.BackgroundProperty, new DynamicResourceExtension("MaterialDesignBackground")));
            Current.Resources["CommonWindow"] = newWindowStyle;
        }
        #endregion Background Color

        #region Foreground Color
        Style newButtonStyle = new(typeof(Button), Current.Resources[typeof(Button)] as Style);
        (Color? newForegroundColor, Color newAccentForegroundColor) = ForegroundGenerator.GetForeground(newPrimaryColor.R, newPrimaryColor.G, newPrimaryColor.B);

        newButtonStyle.Setters.Add(new Setter(Button.ForegroundProperty, newForegroundColor.HasValue ? new SolidColorBrush(newForegroundColor.Value) : new DynamicResourceExtension("MaterialDesignBackground")));
        Current.Resources[typeof(Button)] = newButtonStyle;

        new SettingsPres().AccentForegroundColor = newAccentForegroundColor;
        #endregion Foreground Color

        // 注销/关机时 WPF 的关闭流程可能走不完（来不及 await），这里做一次同步兜底，
        // 否则重启后 hosts 仍指着没人监听的回环端口，用户会以为"代理坏了"。
        SessionEnding += (_, _) => NginxCleaner.CleanSync();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => NginxCleaner.CleanSync();

        // Raw background threads (notably the WinDivert receive loop) cannot be caught by the WPF
        // dispatcher handler. Without this, one malformed packet terminates the process silently and
        // leaves nginx/hosts/PAC behind. Log the real reason and make one best-effort cleanup pass.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            string detail = args.ExceptionObject?.ToString() ?? "(unknown)";
            DirectLog.Write("crash.log", $"未处理异常（IsTerminating={args.IsTerminating}）：{detail}");
            try { NginxCleaner.CleanSync(); }
            catch (Exception ex) { NginxCleaner.LogFailure("崩溃清理失败", ex); }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            DirectLog.Write("crash.log", $"未观察任务异常：{args.Exception}");
            args.SetObserved();
        };

        new MainWin().Show();
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // 退出流程里的异常以前会在这里被吞掉且不留痕迹，是"窗口关不掉又没报错"的帮凶。
        DirectLog.Write("crash.log", $"UI 未处理异常：{e.Exception}");
        NginxCleaner.LogFailure("未处理异常", e.Exception);
        MessageBox.Show($"Error: {e.Exception.Message}");
        e.Handled = true;
    }
}