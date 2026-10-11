using Ona_Core;
using MistSail.Consts;
using MistSail.Preses;
using MistSail.Utils;
using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MistSail.Wins;

public partial class AboutWin : Window
{
    private readonly AboutPres AboutPres;
    private readonly HttpClient AboutClient = new(new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator });

    internal AboutWin()
    {
        InitializeComponent();

        DataContext = AboutPres = new();
    }
    private void AboutWin_SourceInitialized(object sender, EventArgs e)
    {
        IconRemover.RemoveIcon(this);
        BorderThemeSetter.SetBorderTheme(this, AboutPres.IsLightTheme);
    }
    private async void AboutWin_Loaded(object sender, RoutedEventArgs e)
    {
        await Task.Run(async () =>
        {
            try
            {
                AboutClient.DefaultRequestHeaders.Add("User-Agent", AboutConst.ReleaseApiUserAgent);

                JsonElement releaseInfoObject = JsonDocument.Parse(await Http.GetAsync<string>(AboutConst.ReleaseApiUrl, AboutClient)).RootElement;

                AboutClient.DefaultRequestHeaders.Clear();

                foreach (JsonProperty releaseInfoContent in releaseInfoObject.EnumerateObject())
                    if (releaseInfoContent.Name == "tag_name" && releaseInfoContent.Value.ToString().TrimStart('v') != AboutConst.VersionButtonVersionContent)
                        AboutPres.IsMistSailUtd = false;
            }
            catch { }
        });
    }

    private void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        Button senderButton = (Button)sender;

        ProcessStartInfo processStartInfo = new((string)senderButton.ToolTip) { UseShellExecute = true };

        try { Process.Start(processStartInfo); }
        catch (UnauthorizedAccessException)
        {
            processStartInfo.Verb = "RunAs";
            Process.Start(processStartInfo);
        }
    }

    private void AboutWin_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Close();
    }
}