using System.Reflection;
using System.Windows.Media;

namespace MistSail.Consts;

internal abstract class AboutConst : AboutMultilangConst
{
    public static Color AccentBlueColor => (Color)ColorConverter.ConvertFromString("#2196F3");
    public static Color AccentRedColor => (Color)ColorConverter.ConvertFromString("#F44336");

    public static string DeveloperButtonUrl => "https://github.com/CuteEthanLeo";
    public static string VersionButtonVersionContent => Assembly.GetExecutingAssembly().GetName().Version!.ToString();
    public static string VersionButtonUrl => "https://github.com/CuteEthanLeo/MistSail/releases";
    public static string EmailButtonUrl => "https://github.com/CuteEthanLeo/MistSail/issues";

    public static string DocumentationButtonUrl => "https://github.com/CuteEthanLeo/MistSail#readme";
    public static string RepositoryButtonUrl => "https://github.com/CuteEthanLeo/MistSail";

    public static string PolicyButtonUrl => "https://github.com/CuteEthanLeo/MistSail#注意事项与免责声明";
    public static string AgreementButtonUrl => "https://github.com/CuteEthanLeo/MistSail#注意事项与免责声明";

    internal static string ReleaseApiUrl => "https://api.github.com/repos/CuteEthanLeo/MistSail/releases/latest";
    internal static string ReleaseApiUserAgent => "MistSail";
}