using MaterialDesignThemes.Wpf;
using Microsoft.Win32;
using NginxConfigParser;
using Ona_Core;
using Sheas_Cealer.Consts;
using Sheas_Cealer.Direct;
using Sheas_Cealer.Preses;
using Sheas_Cealer.Proces;
using Sheas_Cealer.Utils;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using File = System.IO.File;

namespace Sheas_Cealer.Wins;

public partial class MainWin : Window
{
    private readonly MainPres MainPres;
    private readonly HttpClient MainClient = new(new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator });
    private DispatcherTimer? HoldButtonTimer;
    private readonly DispatcherTimer ProxyTimer = new() { Interval = TimeSpan.FromSeconds(0.1) };
    private readonly FileSystemWatcher CealHostWatcher = new(Path.GetDirectoryName(MainConst.CealHostPath)!, Path.GetFileName(MainConst.CealHostPath)) { EnableRaisingEvents = true, NotifyFilter = NotifyFilters.LastWrite };
    private readonly FileSystemWatcher NginxConfWatcher = new(Path.GetDirectoryName(MainConst.NginxConfPath)!, Path.GetFileName(MainConst.NginxConfPath)) { EnableRaisingEvents = true, NotifyFilter = NotifyFilters.LastWrite };
    private readonly FileSystemWatcher MihomoConfWatcher = new(Path.GetDirectoryName(MainConst.MihomoConfPath)!, Path.GetFileName(MainConst.MihomoConfPath)) { EnableRaisingEvents = true, NotifyFilter = NotifyFilters.LastWrite };
    private readonly SemaphoreSlim IsNginxLaunchingSemaphore = new(1);
    private readonly SemaphoreSlim RuleLoadingSemaphore = new(1);
    private DirectRelay? DirectTransport;
    private bool DirectStarting;
    private bool WindowClosing;
    private bool IsExiting;
    private SniRelay? Relay;
    private DirectOptions? RelayOptions;
    private readonly SemaphoreSlim SiteCheckSemaphore = new(1);
    private readonly SemaphoreSlim LineProbeSemaphore = new(1);
    private List<LineProbe.Outcome> LineProbeOutcomes = [];
    // Connectivity checks must follow the hosts file like a browser would: no system proxy, any certificate.
    private readonly HttpClient CheckClient = new(new SocketsHttpHandler
    {
        UseProxy = false,
        UseCookies = false,
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(8),
        SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true }
    }) { Timeout = TimeSpan.FromSeconds(12) };

    private SortedDictionary<string, List<(List<(string cealHostIncludeDomain, string cealHostExcludeDomain)> cealHostDomainPairs, string? cealHostSni, string cealHostIp)>?> CealHostRulesDict = [];
    private string CealArgs = string.Empty;
    private NginxConfig? NginxConfs;
    private string? ExtraNginxConfs;
    private string? MihomoConfs;
    private string? ExtraMihomoConfs;

    private int NginxHttpPort = 80;
    private int NginxHttpsPort = 443;
    private int MihomoMixedPort = 7880;

    private int GameClickTime = 0;
    private int GameFlashInterval = 1000;

    internal MainWin()
    {
        InitializeComponent();

        DataContext = MainPres = new();
    }
    private void MainWin_SourceInitialized(object sender, EventArgs e)
    {
        IconRemover.RemoveIcon(this);
        BorderThemeSetter.SetBorderTheme(this, MainPres.IsLightTheme);
    }
    private async void MainWin_Loaded(object sender, RoutedEventArgs e)
    {
        ProxyTimer.Tick += ProxyTimer_Tick;
        CealHostWatcher.Changed += CealHostWatcher_Changed;
        CealHostWatcher.Created += CealHostWatcher_Changed;
        CealHostWatcher.Deleted += CealHostWatcher_Changed;
        CealHostWatcher.Renamed += CealHostWatcher_Renamed;
        NginxConfWatcher.Changed += NginxConfWatcher_Changed;
        MihomoConfWatcher.Changed += MihomoConfWatcher_Changed;
        ProxyTimer.Start();
        await ReloadRulesAsync();
        MihomoConfWatcher_Changed(null!, null!);

        // Nginx left running by an earlier session: hosts still point X at the relay, so bring it back.
        if (MainConst.IsAdmin && MainPres.IsNginxRunning)
        {
            RelayOptions = LoadRelayOptions();
            if (RelayOptions is not null)
                StartRelay(RelayOptions);
            MainPres.ConnectedAt = DateTime.Now;
            _ = RunSiteChecksAsync();
        }

        // Opening the app alone must not edit hosts/certificates or perform network requests.
        // Updates and transports are started only by the corresponding buttons.
        if (Array.Exists(Environment.GetCommandLineArgs(), arg => arg.Equals("-s", StringComparison.OrdinalIgnoreCase)))
            LaunchButton_Click(null, null!);
    }
    private async void MainWin_Closing(object sender, CancelEventArgs e)
    {
        if (IsExiting)
            return;

        if (MainPres.IsNginxRunning && !MainPres.IsNginxIniting)
        {
            if (MessageBox.Show(MainConst._ExitWhileConnectedPrompt, string.Empty, MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            // Keep the window alive until hosts and certificates are restored, then close for real.
            e.Cancel = true;
            IsExiting = true;
            WindowClosing = true;
            IsEnabled = false;
            await StopGlobalAsync();
            await FinishClosingAsync();
            Application.Current.Shutdown();
            return;
        }

        IsExiting = true;
        WindowClosing = true;
        await FinishClosingAsync();

        Application.Current.Shutdown();
    }
    private async Task FinishClosingAsync()
    {
        StopDirectTransport();
        StopRelay();
        ProxyTimer.Stop();
        CealHostWatcher.EnableRaisingEvents = false;
        NginxConfWatcher.EnableRaisingEvents = false;
        MihomoConfWatcher.EnableRaisingEvents = false;
        if (MainPres.IsMihomoIniting)
            await File.WriteAllTextAsync(MainConst.MihomoConfPath, ExtraMihomoConfs);
    }

    private void MainWin_DragEnter(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }
    private void MainWin_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
            MainPres.BrowserPath = (e.Data.GetData(DataFormats.FileDrop) as string[])?[0] ?? string.Empty;
    }

    private void SettingsBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        switch (MainPres.SettingsMode)
        {
            case MainConst.SettingsMode.BrowserPathMode:
                MainPres.BrowserPath = SettingsBox.Text;
                return;
            case MainConst.SettingsMode.UpstreamUrlMode:
                MainPres.UpstreamUrl = SettingsBox.Text;
                return;
            case MainConst.SettingsMode.ExtraArgsMode:
                MainPres.ExtraArgs = SettingsBox.Text;
                return;
        }
    }
    private void SettingsModeButton_Click(object sender, RoutedEventArgs e)
    {
        MainPres.SettingsMode = MainPres.SettingsMode switch
        {
            MainConst.SettingsMode.BrowserPathMode => MainConst.SettingsMode.UpstreamUrlMode,
            MainConst.SettingsMode.UpstreamUrlMode => MainConst.SettingsMode.ExtraArgsMode,
            MainConst.SettingsMode.ExtraArgsMode => MainConst.SettingsMode.BrowserPathMode,
            _ => throw new UnreachableException()
        };
    }
    private void SettingsFunctionButton_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog browserPathDialog = new() { Filter = $"{MainConst._BrowserPathDialogFilterFileType} (*.exe)|*.exe" };

        switch (MainPres.SettingsMode)
        {
            case MainConst.SettingsMode.BrowserPathMode when browserPathDialog.ShowDialog().GetValueOrDefault():
                SettingsBox.Focus();
                MainPres.BrowserPath = browserPathDialog.FileName;
                return;
            case MainConst.SettingsMode.UpstreamUrlMode:
                MainPres.UpstreamUrl = MainConst.DefaultUpstreamUrl;
                return;
            case MainConst.SettingsMode.ExtraArgsMode:
                MainPres.ExtraArgs = string.Empty;
                return;
        }
    }

    private void LaunchButton_Click(object? sender, RoutedEventArgs e)
    {
        if (HoldButtonTimer is { IsEnabled: false })
            return;

        Button? senderButton = sender as Button;

        if (senderButton == NginxButton)
            NginxButtonHoldTimer_Tick(null, null!);
        else if (senderButton == MihomoButton)
            MihomoButtonHoldTimer_Tick(null, null!);
        else
            BrowserButtonHoldTimer_Tick(sender == null, null!);
    }
    private void LaunchButton_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        Button senderButton = (Button)sender;

        HoldButtonTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        HoldButtonTimer.Tick += senderButton == NginxButton ? NginxButtonHoldTimer_Tick : senderButton == MihomoButton ? MihomoButtonHoldTimer_Tick : BrowserButtonHoldTimer_Tick;
        HoldButtonTimer.Start();
    }
    private async void BrowserButtonHoldTimer_Tick(object? sender, EventArgs e)
    {
        HoldButtonTimer?.Stop();
        if (DirectTransport is not null)
        {
            MessageBox.Show(MainConst._DirectStopFirstMsg);
            return;
        }
        await ReloadRulesAsync();

        if ((CealHostRulesDict.ContainsValue(null!) && MessageBox.Show(MainConst._CealHostErrorPrompt, string.Empty, MessageBoxButton.YesNo) != MessageBoxResult.Yes) ||
            (sender is not true && MessageBox.Show(MainConst._KillBrowserProcessPrompt, string.Empty, MessageBoxButton.YesNo) != MessageBoxResult.Yes))
            return;

        foreach (Process browserProcess in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(MainPres.BrowserPath)))
        {
            browserProcess.Kill();
            await browserProcess.WaitForExitAsync();
        }

        await Task.Run(() =>
        {
            new BrowserProc(MainPres.BrowserPath, sender is bool).Run(Path.GetDirectoryName(MainPres.BrowserPath), $"{CealArgs} {MainPres.ExtraArgs.Trim()} --disable-quic");
        });
    }
    private async void NginxButtonHoldTimer_Tick(object? sender, EventArgs e)
    {
        HoldButtonTimer?.Stop();
        await ToggleGlobalAsync();
    }
    private async void PowerButton_Click(object sender, RoutedEventArgs e)
    {
        if (!MainConst.IsAdmin)
        {
            MessageBox.Show(MainConst._PowerNeedsAdminMsg);
            return;
        }

        await ToggleGlobalAsync();
    }
    private async Task ToggleGlobalAsync()
    {
        if (DirectTransport is not null && !MainPres.IsNginxRunning)
        {
            MessageBox.Show(MainConst._DirectStopFirstMsg);
            return;
        }

        if (!MainPres.IsNginxRunning)
        {
            if (NginxCleaner.IsNginxCleaningSemaphore.CurrentCount == 0 || !await IsNginxLaunchingSemaphore.WaitAsync(0))
                return;

            try
            {
                if ((CealHostRulesDict.ContainsValue(null!) && MessageBox.Show(MainConst._CealHostErrorPrompt, string.Empty, MessageBoxButton.YesNo) != MessageBoxResult.Yes) ||
                    (NginxHttpsPort != 443 && MessageBox.Show(string.Format(MainConst._NginxHttpsPortOccupiedPrompt, NginxHttpsPort), string.Empty, MessageBoxButton.YesNo) != MessageBoxResult.Yes) ||
                    (NginxHttpPort != 80 && MessageBox.Show(string.Format(MainConst._NginxHttpPortOccupiedPrompt, NginxHttpPort), string.Empty, MessageBoxButton.YesNo) != MessageBoxResult.Yes) ||
                    (MessageBox.Show(MainConst._LaunchProxyPrompt, string.Empty, MessageBoxButton.YesNo) != MessageBoxResult.Yes) ||
                    (MainPres.IsFlashing && MessageBox.Show(MainConst._LaunchNginxFlashingPrompt, string.Empty, MessageBoxButton.YesNo) != MessageBoxResult.Yes))
                    return;

                if (!File.Exists(MainConst.NginxConfPath))
                    await File.Create(MainConst.NginxConfPath).DisposeAsync();
                if (!Directory.Exists(MainConst.NginxLogsPath))
                    Directory.CreateDirectory(MainConst.NginxLogsPath);
                if (!Directory.Exists(MainConst.NginxTempPath))
                    Directory.CreateDirectory(MainConst.NginxTempPath);

                RSA certKey = RSA.Create(2048);

                #region Root Cert
                CertificateRequest rootCertRequest = new(MainConst.NginxRootCertSubjectName, certKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

                rootCertRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, false));

                using X509Certificate2 rootCert = rootCertRequest.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(100));
                using X509Store certStore = new(StoreName.Root, StoreLocation.CurrentUser, OpenFlags.ReadWrite);

                certStore.Add(rootCert);
                certStore.Close();
                #endregion Root Cert

                #region Child Cert & Hosts
                CertificateRequest childCertRequest = new(MainConst.NginxChildCertSubjectName, certKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                SubjectAlternativeNameBuilder childCertSanBuilder = new();
                string hostsConfAppendContent = MainConst.HostsConfStartMarker;

                // Relay hosts come first: Windows honours the first matching hosts line, and these
                // names must reach the fragmenting relay on RelayListenAddress rather than Nginx.
                RelayOptions = LoadRelayOptions();
                if (RelayOptions is not null)
                    foreach (string relayHost in RelayOptions.RelayHosts.Keys)
                        if (!relayHost.Contains('*'))
                            hostsConfAppendContent += $"{RelayOptions.RelayListenAddress} {relayHost}{Environment.NewLine}";

                foreach (List<(List<(string cealHostIncludeDomain, string cealHostExcludeDomain)> cealHostDomainPairs, string? cealHostSni, string cealHostIp)>? cealHostRules in CealHostRulesDict.Values)
                    foreach ((List<(string cealHostIncludeDomain, string cealHostExcludeDomain)> cealHostDomainPairs, _, _) in cealHostRules ?? [])
                        foreach ((string cealHostIncludeDomain, _) in cealHostDomainPairs)
                        {
                            string cealHostIncludeDomainWithoutWildcard = cealHostIncludeDomain.TrimStart('$').TrimStart('*').TrimStart('.');

                            if (cealHostIncludeDomain.StartsWith('#') || cealHostIncludeDomainWithoutWildcard.Contains('*') || string.IsNullOrWhiteSpace(cealHostIncludeDomainWithoutWildcard))
                                continue;

                            if (cealHostIncludeDomain.TrimStart('$').StartsWith('*'))
                            {
                                childCertSanBuilder.AddDnsName($"*.{cealHostIncludeDomainWithoutWildcard}");
                                hostsConfAppendContent += $"127.0.0.1 www.{cealHostIncludeDomainWithoutWildcard}{Environment.NewLine}";

                                if (cealHostIncludeDomain.TrimStart('$').StartsWith("*."))
                                    continue;
                            }

                            childCertSanBuilder.AddDnsName(cealHostIncludeDomainWithoutWildcard);
                            hostsConfAppendContent += $"127.0.0.1 {cealHostIncludeDomainWithoutWildcard}{Environment.NewLine}";
                        }

                childCertRequest.CertificateExtensions.Add(childCertSanBuilder.Build());

                using X509Certificate2 childCert = childCertRequest.Create(rootCert, rootCert.NotBefore, rootCert.NotAfter, Guid.NewGuid().ToByteArray());

                await File.WriteAllTextAsync(MainConst.NginxCertPath, childCert.ExportCertificatePem());
                await File.WriteAllTextAsync(MainConst.NginxKeyPath, certKey.ExportPkcs8PrivateKeyPem());

                hostsConfAppendContent += MainConst.HostsConfEndMarker;

                File.SetAttributes(MainConst.HostsConfPath, File.GetAttributes(MainConst.HostsConfPath) & ~FileAttributes.ReadOnly);
                await File.AppendAllTextAsync(MainConst.HostsConfPath, hostsConfAppendContent);
                #endregion Child Cert & Hosts

                try
                {
                    MainPres.IsNginxIniting = true;
                    // The generated config gets its own file: nginx.conf stays the user's extras, and the
                    // Windows worker processes (which re-read the file) always find a complete config.
                    NginxConfs!.Save(MainConst.NginxRunningConfPath);

                    await Task.Run(() =>
                    {
                        new NginxProc().Run(Path.GetDirectoryName(MainConst.NginxPath), @$"-c ""{Path.GetRelativePath(Path.GetDirectoryName(MainConst.NginxPath)!, MainConst.NginxRunningConfPath)}""");
                    });

                    while (true)
                    {
                        try
                        {
                            await Http.GetAsync<HttpResponseMessage>($"https://127.0.0.1:{NginxHttpsPort}", MainClient);

                            break;
                        }
                        catch (HttpRequestException ex)
                        {
                            if (ex.InnerException is SocketException innerEx && innerEx.SocketErrorCode != SocketError.ConnectionRefused)
                                break;
                        }

                        if (MainPres.IsNginxRunning)
                            continue;

                        if (MessageBox.Show(MainConst._LaunchNginxErrorPrompt, string.Empty, MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                            Process.Start(new ProcessStartInfo(MainConst.NginxErrorLogsPath) { UseShellExecute = true });

                        break;
                    }

                    bool isNginxUp = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(MainConst.NginxPath)).Length != 0;
                    MainPres.IsNginxRunning = isNginxUp;
                    if (isNginxUp)
                    {
                        if (RelayOptions is not null)
                            StartRelay(RelayOptions);
                        MainPres.ConnectedAt = DateTime.Now;
                        _ = RunSiteChecksAsync();
                    }
                }
                finally
                {
                    MainPres.IsNginxIniting = false;
                }
            }
            finally { IsNginxLaunchingSemaphore.Release(); }
        }
        else
            await StopGlobalAsync();
    }
    private async Task StopGlobalAsync()
    {
        StopRelay();
        MainPres.ConnectedAt = null;
        MainPres.LineVerdict = string.Empty;
        LineProbeOutcomes = [];
        foreach (SiteCheckItem check in MainPres.SiteChecks)
            check.Reset();

        Process[] nginxProcesses = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(MainConst.NginxPath));
        foreach (Process nginxProcess in nginxProcesses)
            try { nginxProcess.Kill(); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        foreach (Process nginxProcess in nginxProcesses)
            try { await nginxProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException) { }

        // NginxProc also cleans on exit; the semaphore makes this idempotent, and waiting on it
        // guarantees hosts and certificates are restored before we return (e.g. on app exit).
        await NginxCleaner.Clean();
        if (await NginxCleaner.IsNginxCleaningSemaphore.WaitAsync(TimeSpan.FromSeconds(10)))
            NginxCleaner.IsNginxCleaningSemaphore.Release();

        try { File.Delete(MainConst.NginxRunningConfPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        NginxHttpPort = 80;
        NginxHttpsPort = 443;
        MainPres.IsNginxRunning = false;
        NginxConfWatcher_Changed(null!, null!);
    }
    private async void MihomoButtonHoldTimer_Tick(object? sender, EventArgs e)
    {
        HoldButtonTimer?.Stop();
        if (DirectTransport is not null && !MainPres.IsMihomoRunning)
        {
            MessageBox.Show(MainConst._DirectStopFirstMsg);
            return;
        }

        if (!MainPres.IsMihomoRunning)
        {
            if (string.IsNullOrWhiteSpace(MihomoConfs))
                throw new(MainConst._MihomoConfErrorMsg);
            if (MessageBox.Show(MainConst._LaunchProxyPrompt, string.Empty, MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                return;

            if (!File.Exists(MainConst.MihomoConfPath))
                await File.Create(MainConst.MihomoConfPath).DisposeAsync();

            try
            {
                MainPres.IsMihomoIniting = true;
                MihomoConfWatcher.EnableRaisingEvents = false;
                await File.WriteAllTextAsync(MainConst.MihomoConfPath, MihomoConfs);

                await Task.Run(() =>
                {
                    new MihomoProc().Run(Path.GetDirectoryName(MainConst.MihomoPath), @$"-d ""{Path.GetDirectoryName(MainConst.MihomoConfPath)}""");
                });

                while (true)
                {
                    try
                    {
                        await Http.GetAsync<HttpResponseMessage>($"http://localhost:{MihomoMixedPort}", MainClient);

                        break;
                    }
                    catch (HttpRequestException ex)
                    {
                        if (ex.InnerException is SocketException innerEx && innerEx.SocketErrorCode != SocketError.ConnectionRefused)
                            break;
                    }

                    if (MainPres.IsMihomoRunning)
                        continue;

                    MessageBox.Show(MainConst._LaunchMihomoErrorMsg);

                    break;
                }
            }
            finally
            {
                await File.WriteAllTextAsync(MainConst.MihomoConfPath, ExtraMihomoConfs);
                MihomoConfWatcher.EnableRaisingEvents = true;
                MainPres.IsMihomoIniting = false;
            }
        }
        else
            foreach (Process mihomoProcess in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(MainConst.MihomoPath)))
            {
                mihomoProcess.Exited += (_, _) =>
                {
                    MihomoMixedPort = 7880;
                    MihomoConfWatcher_Changed(null!, null!);
                };

                mihomoProcess.Kill();
            }
    }

    private async void EditHostButton_Click(object sender, RoutedEventArgs e)
    {
        Button senderButton = (Button)sender;
        string cealHostPath = senderButton == EditLocalHostButton ? MainConst.LocalHostPath : MainConst.UpstreamHostPath;

        if (!File.Exists(cealHostPath))
            await File.Create(cealHostPath).DisposeAsync();

        try { Process.Start(new ProcessStartInfo(cealHostPath) { UseShellExecute = true }); }
        catch (UnauthorizedAccessException) { Process.Start(new ProcessStartInfo(cealHostPath) { UseShellExecute = true, Verb = "RunAs" }); }
    }
    private async void EditConfButton_Click(object sender, RoutedEventArgs e)
    {
        Button senderButton = (Button)sender;
        string confPath;

        if (senderButton == EditHostsConfButton)
        {
            confPath = MainConst.HostsConfPath;

            File.SetAttributes(MainConst.HostsConfPath, File.GetAttributes(MainConst.HostsConfPath) & ~FileAttributes.ReadOnly);
        }
        else
        {
            confPath = senderButton == EditNginxConfButton ? MainConst.NginxConfPath : MainConst.MihomoConfPath;

            if (!File.Exists(confPath))
                await File.Create(confPath).DisposeAsync();
        }

        Process.Start(new ProcessStartInfo(confPath) { UseShellExecute = true });
    }
    private async void UpdateUpstreamHostButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!File.Exists(MainConst.UpstreamHostPath))
                await File.Create(MainConst.UpstreamHostPath).DisposeAsync();

            string upstreamUpstreamHostUrl = (MainPres.UpstreamUrl.StartsWith("http://") || MainPres.UpstreamUrl.StartsWith("https://") ? string.Empty : "https://") + MainPres.UpstreamUrl;
            string upstreamUpstreamHostString = await Http.GetAsync<string>(upstreamUpstreamHostUrl, MainClient);
            string localUpstreamHostString = await File.ReadAllTextAsync(MainConst.UpstreamHostPath);

            try { upstreamUpstreamHostString = Encoding.UTF8.GetString(Convert.FromBase64String(upstreamUpstreamHostString)); }
            catch { }

            if (sender == null && (localUpstreamHostString != upstreamUpstreamHostString && localUpstreamHostString.ReplaceLineEndings() != upstreamUpstreamHostString.ReplaceLineEndings()))
                MainPres.IsUpstreamHostUtd = false;
            else if (sender != null)
                if (localUpstreamHostString == upstreamUpstreamHostString || localUpstreamHostString.ReplaceLineEndings() == upstreamUpstreamHostString.ReplaceLineEndings())
                {
                    MainPres.IsUpstreamHostUtd = true;

                    MessageBox.Show(MainConst._UpstreamHostUtdMsg);
                }
                else
                {
                    MessageBoxResult overrideOptionResult = MessageBox.Show(MainConst._OverrideUpstreamHostPrompt, string.Empty, MessageBoxButton.YesNoCancel);

                    if (overrideOptionResult == MessageBoxResult.Yes)
                    {
                        await File.WriteAllTextAsync(MainConst.UpstreamHostPath, upstreamUpstreamHostString);

                        MainPres.IsUpstreamHostUtd = true;

                        MessageBox.Show(MainConst._UpdateUpstreamHostSuccessMsg);
                    }
                    else if (overrideOptionResult == MessageBoxResult.No)
                        try { Process.Start(new ProcessStartInfo(upstreamUpstreamHostUrl) { UseShellExecute = true }); }
                        catch (UnauthorizedAccessException) { Process.Start(new ProcessStartInfo(upstreamUpstreamHostUrl) { UseShellExecute = true, Verb = "RunAs" }); }
                }
        }
        catch when (sender == null) { }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => new SettingsWin().ShowDialog();
    private async void NoClickButton_Click(object sender, RoutedEventArgs e)
    {
        if (GameFlashInterval <= 10)
        {
            MessageBox.Show(MainConst._GameReviewEndingMsg);

            return;
        }

        switch (++GameClickTime)
        {
            case 1:
                MessageBox.Show(MainConst._GameClickOnceMsg);
                return;
            case 2:
                MessageBox.Show(MainConst._GameClickTwiceMsg);
                return;
            case 3:
                MessageBox.Show(MainConst._GameClickThreeMsg);
                return;
        }

        if (!MainPres.IsFlashing)
        {
            MessageBox.Show(MainConst._GameStartMsg);
            MainPres.IsFlashing = true;
            NginxConfWatcher_Changed(null!, null!);

            Random random = new();

            while (GameFlashInterval > 10)
            {
                Left = random.Next(0, (int)(SystemParameters.PrimaryScreenWidth - ActualWidth));
                Top = random.Next(0, (int)(SystemParameters.PrimaryScreenHeight - ActualHeight));

                PaletteHelper paletteHelper = new();
                Theme newTheme = paletteHelper.GetTheme();
                Color newPrimaryColor = Color.FromRgb((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
                bool isLightTheme = random.Next(2) == 0;

                newTheme.SetPrimaryColor(newPrimaryColor);
                newTheme.SetBaseTheme(isLightTheme ? BaseTheme.Light : BaseTheme.Dark);
                paletteHelper.SetTheme(newTheme);

                foreach (Window currentWindow in Application.Current.Windows)
                    BorderThemeSetter.SetBorderTheme(currentWindow, isLightTheme);

                Style newButtonStyle = new(typeof(Button), Application.Current.Resources[typeof(Button)] as Style);
                (Color? newForegroundColor, Color newAccentForegroundColor) = ForegroundGenerator.GetForeground(newPrimaryColor.R, newPrimaryColor.G, newPrimaryColor.B);

                newButtonStyle.Setters.Add(new Setter(ForegroundProperty, newForegroundColor.HasValue ? new SolidColorBrush(newForegroundColor.Value) : new DynamicResourceExtension("MaterialDesignBackground")));
                Application.Current.Resources[typeof(Button)] = newButtonStyle;

                MainPres.AccentForegroundColor = newAccentForegroundColor;

                if (GameFlashInterval > 100)
                    GameFlashInterval += random.Next(1, 4);

                await Task.Delay(GameFlashInterval);
            }

            MainPres.IsFlashing = false;
            NginxConfWatcher_Changed(null!, null!);
            MessageBox.Show(MainConst._GameEndingMsg);
        }
        else
        {
            switch (GameFlashInterval)
            {
                case > 250:
                    GameFlashInterval -= 150;
                    break;
                case > 100:
                    GameFlashInterval = 100;
                    break;
                case > 10:
                    GameFlashInterval -= 30;
                    break;
            }

            if (GameFlashInterval > 10)
                MessageBox.Show($"{MainConst._GameGradeMsg} {GameFlashInterval}");
        }
    }
    private void AboutButton_Click(object sender, RoutedEventArgs e) => new AboutWin().ShowDialog();

    private void DirectStartButton_Click(object sender, RoutedEventArgs e)
    {
        if (DirectStarting || DirectTransport is not null) return;
        if (MainPres.IsNginxRunning || MainPres.IsMihomoRunning)
        {
            MessageBox.Show(MainConst._DirectStopGlobalFirstMsg);
            return;
        }
        string chosenBrowser = Path.GetFileNameWithoutExtension(MainPres.BrowserPath).ToLowerInvariant();
        if (!File.Exists(MainPres.BrowserPath) || chosenBrowser is not ("msedge" or "chrome" or "brave"))
        {
            MessageBox.Show(MainConst._DirectBrowserUnsupportedMsg);
            return;
        }
        DirectStarting = true;
        DirectStartButton.IsEnabled = false;
        try
        {
            DirectOptions options = DirectOptions.Load();
            DirectTransport = new DirectRelay(options);
            DirectTransport.Status += DirectTransport_Status;
            DirectTransport.Start();
            string browserName = Path.GetFileNameWithoutExtension(MainPres.BrowserPath);
            string profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Sheas-Cealer-Direct", "BrowserProfiles", $"{browserName}-{DirectTransport.Port}");
            Directory.CreateDirectory(profile);
            ProcessStartInfo start = new(MainPres.BrowserPath) { UseShellExecute = false };
            start.ArgumentList.Add($"--user-data-dir={profile}");
            start.ArgumentList.Add($"--proxy-server=http://127.0.0.1:{DirectTransport.Port}");
            start.ArgumentList.Add("--proxy-bypass-list=localhost;127.0.0.1;[::1]");
            start.ArgumentList.Add("--disable-quic");
            start.ArgumentList.Add("--no-first-run");
            start.ArgumentList.Add("--no-default-browser-check");
            start.ArgumentList.Add("--new-window");
            start.ArgumentList.Add("about:blank");
            // Saved ExtraArgs and CealArgs belong to legacy mode. In particular,
            // never inherit --ignore-certificate-errors or an upstream proxy here.
            using Process launchedBrowser = Process.Start(start) ?? throw new IOException("浏览器进程未能启动。");
            DirectStopButton.IsEnabled = true;
            DirectStatus.Text = MainConst.DirectStatusStarted;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or System.ComponentModel.Win32Exception or SocketException)
        {
            StopDirectTransport();
            DirectStatus.Text = MainConst._DirectStartFailedPrefix + ex.Message;
            MessageBox.Show(DirectStatus.Text);
        }
        finally
        {
            DirectStarting = false;
            DirectStartButton.IsEnabled = DirectTransport is null;
        }
    }

    private void DirectTransport_Status(string text)
    {
        if (WindowClosing || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (!WindowClosing && DirectTransport is not null) DirectStatus.Text = text;
        });
    }

    private void StopDirectTransport()
    {
        if (DirectTransport is not null)
        {
            DirectTransport.Status -= DirectTransport_Status;
            DirectTransport.Dispose();
            DirectTransport = null;
        }
        DirectStopButton.IsEnabled = false;
        DirectStartButton.IsEnabled = true;
    }

    private void DirectStopButton_Click(object sender, RoutedEventArgs e)
    {
        StopDirectTransport();
        DirectStatus.Text = MainConst.DirectStatusStopped;
    }

    private void DirectConfigButton_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(DirectOptions.ConfigPath))
            File.WriteAllText(DirectOptions.ConfigPath, JsonSerializer.Serialize(new DirectOptions(), DirectOptions.JsonOptions));
        Process.Start(new ProcessStartInfo("notepad.exe") { ArgumentList = { DirectOptions.ConfigPath }, UseShellExecute = false });
    }

    private DirectOptions? LoadRelayOptions()
    {
        try { return DirectOptions.Load(); }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            MainPres.RelayStatus = string.Format(MainConst.RelayStatusFailed, ex.Message);
            return null;
        }
    }
    private void StartRelay(DirectOptions options)
    {
        StopRelay();
        try
        {
            Relay = new SniRelay(options);
            Relay.Status += Relay_Status;
            Relay.Start();
            MainPres.IsRelayRunning = true;
            MainPres.RelayStatus = string.Format(MainConst.RelayStatusRunning, Relay.EndPoint);
        }
        catch (Exception ex) when (ex is SocketException or IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        {
            StopRelay();
            MainPres.RelayStatus = string.Format(MainConst.RelayStatusFailed, ex.Message);
            MessageBox.Show(string.Format(MainConst._RelayListenFailedMsg, $"{options.RelayListenAddress}:443", ex.Message));
        }
    }
    private void Relay_Status(string text)
    {
        if (WindowClosing || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (!WindowClosing && Relay is not null) MainPres.RelayStatus = text;
        });
    }
    private void StopRelay()
    {
        if (Relay is not null)
        {
            Relay.Status -= Relay_Status;
            Relay.Dispose();
            Relay = null;
        }
        MainPres.IsRelayRunning = false;
        MainPres.RelayStatus = MainConst.RelayStatusIdle;
    }

    private async Task RunSiteChecksAsync()
    {
        if (!await SiteCheckSemaphore.WaitAsync(0))
            return;
        try
        {
            foreach (SiteCheckItem check in MainPres.SiteChecks)
                check.Set(SiteCheckState.Checking, MainConst.SiteCheckChecking);
            await Task.WhenAll(MainPres.SiteChecks.Select(CheckSiteAsync));
        }
        finally { SiteCheckSemaphore.Release(); }

        // X failed through the relay: run the control probes once so the log says why.
        if (Relay is not null && MainPres.SiteChecks.Any(check => check.Host == "x.com" && check.State == SiteCheckState.Fail) && LineProbeOutcomes.Count == 0)
            await RunLineProbeAsync();
    }
    private async Task RunLineProbeAsync()
    {
        if (RelayOptions is null || !await LineProbeSemaphore.WaitAsync(0))
            return;
        try
        {
            MainPres.IsLineProbing = true;
            MainPres.LineVerdict = MainConst.LineProbeRunning;
            DirectOptions options = RelayOptions;
            using CancellationTokenSource cancel = new(TimeSpan.FromMinutes(3));
            List<LineProbe.Outcome> outcomes = await Task.Run(() => LineProbe.RunAsync(options, LineProbe.DefaultSteps(options),
                text => DirectLog.Write(SniRelay.LogFile, text), cancel.Token));
            LineProbeOutcomes = outcomes;
            MainPres.LineVerdict = string.Format(MainConst.LineProbeVerdict, LineProbe.Verdict(outcomes));
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException)
        {
            MainPres.LineVerdict = string.Format(MainConst.LineProbeVerdict, ex.Message);
        }
        finally
        {
            MainPres.IsLineProbing = false;
            LineProbeSemaphore.Release();
        }
    }
    private async void LineProbeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!MainPres.IsNginxRunning)
        {
            MainPres.LineVerdict = MainConst.SiteCheckNotConnected;
            return;
        }
        LineProbeOutcomes = [];
        await RunLineProbeAsync();
    }
    private async Task CheckSiteAsync(SiteCheckItem check)
    {
        Stopwatch watch = Stopwatch.StartNew();
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, check.Url);
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Sheas-Cealer-Check");
            using HttpResponseMessage response = await CheckClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            int statusCode = (int)response.StatusCode;
            bool isOk = statusCode < 400;

            check.Set(isOk ? SiteCheckState.Ok : SiteCheckState.Fail, isOk ?
                string.Format(MainConst.SiteCheckOk, statusCode, watch.ElapsedMilliseconds) :
                string.Format(MainConst.SiteCheckFail, $"HTTP {statusCode}{RelayEvidence(check.Host)}"));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or SocketException or InvalidOperationException)
        {
            string reason = ex is TaskCanceledException ? MainConst.SiteCheckTimeout : ex.InnerException?.Message ?? ex.Message;

            check.Set(SiteCheckState.Fail, string.Format(MainConst.SiteCheckFail, reason + RelayEvidence(check.Host)));
        }
    }
    private string RelayEvidence(string host) =>
        Relay is not null && Relay.LastResultByHost.TryGetValue(host, out string? evidence) ? string.Format(MainConst.SiteCheckRelayEvidence, evidence) : string.Empty;
    private async void RecheckButton_Click(object sender, RoutedEventArgs e)
    {
        if (!MainPres.IsNginxRunning)
        {
            foreach (SiteCheckItem check in MainPres.SiteChecks)
                check.Set(SiteCheckState.Pending, MainConst.SiteCheckNotConnected);
            return;
        }

        await RunSiteChecksAsync();
    }
    private void CopyDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        StringBuilder text = new();
        text.AppendLine($"Sheas Cealer {Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion} · {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        text.AppendLine($"admin={MainConst.IsAdmin} nginx={MainPres.IsNginxRunning} relay={MainPres.IsRelayRunning} ports={NginxHttpPort}/{NginxHttpsPort} connectedAt={MainPres.ConnectedAt:HH:mm:ss}");
        text.AppendLine($"relay-status: {MainPres.RelayStatus}");
        foreach (SiteCheckItem check in MainPres.SiteChecks)
            text.AppendLine($"{check.Name}: {check.State} · {check.Detail}");
        if (Relay is not null)
            foreach (KeyValuePair<string, string> pair in Relay.LastResultByHost.OrderBy(pair => pair.Key))
                text.AppendLine($"relay {pair.Key}: {pair.Value}");
        if (LineProbeOutcomes.Count > 0)
        {
            text.AppendLine("--- line probe ---");
            foreach (LineProbe.Outcome outcome in LineProbeOutcomes)
                text.AppendLine($"{outcome.Step.Name} [{outcome.Step.Ip} sni={outcome.Step.Sni} frag={outcome.Step.Fragment}] -> {outcome.Result} {outcome.Milliseconds}ms");
            text.AppendLine($"verdict: {LineProbe.Verdict(LineProbeOutcomes)}");
        }
        text.AppendLine($"--- {SniRelay.LogFile} (tail) ---");
        try
        {
            string logPath = DirectLog.PathFor(SniRelay.LogFile);
            if (File.Exists(logPath))
                foreach (string line in File.ReadLines(logPath).TakeLast(40))
                    text.AppendLine(line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { text.AppendLine(ex.Message); }

        try
        {
            Clipboard.SetText(text.ToString());
            MessageBox.Show(MainConst._CopyDiagnosticsDoneMsg);
        }
        catch (System.Runtime.InteropServices.ExternalException ex) { MessageBox.Show(ex.Message); }
    }
    private void RelaunchAsAdminButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory });
            Application.Current.Shutdown();
        }
        catch (System.ComponentModel.Win32Exception) { } // UAC prompt cancelled
    }

    private void ProxyTimer_Tick(object? sender, EventArgs e)
    {
        MainPres.IsNginxExist = File.Exists(Path.Combine(AppDomain.CurrentDomain.SetupInformation.ApplicationBase!, Path.GetFileName(MainConst.NginxPath)));
        MainPres.IsNginxRunning = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(MainConst.NginxPath)).Length != 0;
        MainPres.IsMihomoExist = File.Exists(Path.Combine(AppDomain.CurrentDomain.SetupInformation.ApplicationBase!, Path.GetFileName(MainConst.MihomoPath)));
        MainPres.IsMihomoRunning = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(MainConst.MihomoPath)).Length != 0;

        // Nginx stopped elsewhere (killed, crashed): its exit handler removed the hosts block, so the relay is idle.
        if (!MainPres.IsNginxRunning && !MainPres.IsNginxIniting && Relay is not null)
        {
            StopRelay();
            MainPres.ConnectedAt = null;
            foreach (SiteCheckItem check in MainPres.SiteChecks)
                check.Reset();
        }
    }
    private void CealHostWatcher_Changed(object sender, FileSystemEventArgs e)
    {
        if (WindowClosing || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(async () => await ReloadRulesAsync());
    }

    private void CealHostWatcher_Renamed(object sender, RenamedEventArgs e) => CealHostWatcher_Changed(sender, e);

    private async Task ReloadRulesAsync()
    {
        await RuleLoadingSemaphore.WaitAsync();
        try
        {
            var loadedRules = new SortedDictionary<string, List<(List<(string cealHostIncludeDomain, string cealHostExcludeDomain)> cealHostDomainPairs, string? cealHostSni, string cealHostIp)>?>();
            foreach (string path in Directory.GetFiles(CealHostWatcher.Path, CealHostWatcher.Filter))
            {
                string cealHostName = Path.GetFileNameWithoutExtension(path)["Cealing-Host-".Length..];
                loadedRules[cealHostName] = [];
                try
                {
                    await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (stream.Length == 0) continue;
                    using JsonDocument document = await JsonDocument.ParseAsync(stream,
                        new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                    foreach (JsonElement rule in document.RootElement.EnumerateArray())
                    {
                        if (rule.ValueKind != JsonValueKind.Array || rule.GetArrayLength() != 3 || rule[0].ValueKind != JsonValueKind.Array ||
                            rule[1].ValueKind is not (JsonValueKind.String or JsonValueKind.Null) || rule[2].ValueKind != JsonValueKind.String)
                            throw new InvalidDataException("伪造规则格式错误。");
                        string target = rule[2].GetString()!.Trim();
                        // Dynamic video hosts must never be silently mapped to localhost.
                        // Legacy static mode cannot resolve these after changing their SNI.
                        if (target.Length == 0)
                        {
                            DirectLog.Write($"原有 SNI 模式跳过空目标规则 {cealHostName}；动态视频域名请使用浏览器直连。");
                            continue;
                        }
                        if (target.Contains('"') || target.Contains('\r') || target.Contains('\n'))
                            throw new InvalidDataException("伪造目标包含无效字符。");
                        string? sni = rule[1].ValueKind == JsonValueKind.Null ? null : rule[1].GetString()!.Trim();
                        if (sni == string.Empty) sni = $"{cealHostName}{loadedRules[cealHostName]!.Count}.invalid";
                        if (sni?.Contains('"') == true || sni?.Contains('\r') == true || sni?.Contains('\n') == true)
                            throw new InvalidDataException("SNI 包含无效字符。");
                        List<(string, string)> domains = [];
                        foreach (JsonElement domain in rule[0].EnumerateArray())
                        {
                            string value = domain.GetString() ?? string.Empty;
                            if (value.Contains('"') || value.Contains('\r') || value.Contains('\n'))
                                throw new InvalidDataException("规则域名包含无效字符。");
                            string[] pair = value.Split('^', 2, StringSplitOptions.TrimEntries);
                            if (pair[0].TrimStart('#', '$').Length > 0)
                                domains.Add((pair[0], pair.Length == 2 ? pair[1] : string.Empty));
                        }
                        if (domains.Count > 0) loadedRules[cealHostName]!.Add((domains, sni, target));
                    }
                }
                catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or ArgumentException)
                {
                    loadedRules[cealHostName] = null;
                    DirectLog.Write($"规则加载失败 {cealHostName}：{ex.Message}");
                }
            }
            string hostRules = string.Empty;
            string hostResolverRules = string.Empty;
            int nullSniNum = 0;

            foreach (KeyValuePair<string, List<(List<(string cealHostIncludeDomain, string cealHostExcludeDomain)> cealHostDomainPairs, string? cealHostSni, string cealHostIp)>?> cealHostRulesPair in loadedRules)
                foreach ((List<(string cealHostIncludeDomain, string cealHostExcludeDomain)> cealHostDomainPairs, string? cealHostSni, string cealHostIp) in cealHostRulesPair.Value ?? [])
                {
                    string cealHostSniWithoutNull = cealHostSni ?? $"{cealHostRulesPair.Key}{(cealHostRulesPair.Value ?? []).Count + ++nullSniNum}";
                    bool isValidCealHostDomainExist = false;

                    foreach ((string cealHostIncludeDomain, string cealHostExcludeDomain) in cealHostDomainPairs)
                    {
                        if (cealHostIncludeDomain.StartsWith('$'))
                            continue;

                        hostRules += $"MAP {cealHostIncludeDomain.TrimStart('#')} {cealHostSniWithoutNull}," + (!string.IsNullOrWhiteSpace(cealHostExcludeDomain) ? $"EXCLUDE {cealHostExcludeDomain}," : string.Empty);
                        isValidCealHostDomainExist = true;
                    }

                    if (isValidCealHostDomainExist)
                        hostResolverRules += $"MAP {cealHostSniWithoutNull} {cealHostIp},";
                }

            CealArgs = @$"--host-rules=""{hostRules.TrimEnd(',')}"" --host-resolver-rules=""{hostResolverRules.TrimEnd(',')}"" --test-type --ignore-certificate-errors";
            CealHostRulesDict = loadedRules;

            NginxConfWatcher_Changed(null!, null!);
        }
        finally { RuleLoadingSemaphore.Release(); }
    }
    private async void NginxConfWatcher_Changed(object sender, FileSystemEventArgs e)
    {
        if (!MainConst.IsAdmin || !MainPres.IsNginxExist)
            return;

        if (!File.Exists(MainConst.NginxConfPath))
            await File.Create(MainConst.NginxConfPath).DisposeAsync();
        if (!Directory.Exists(MainConst.NginxLogsPath))
            Directory.CreateDirectory(MainConst.NginxLogsPath);
        if (!Directory.Exists(MainConst.NginxTempPath))
            Directory.CreateDirectory(MainConst.NginxTempPath);

        foreach (IPEndPoint activeTcpListener in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
            if (activeTcpListener.Port == NginxHttpPort)
                NginxHttpPort++;
            else if (activeTcpListener.Port == NginxHttpsPort)
                NginxHttpsPort++;
            else if (activeTcpListener.Port > NginxHttpsPort)
                break;

        await using FileStream nginxConfStream = new(MainConst.NginxConfPath, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        NginxConfig extraNginxConfig = NginxConfig.Load(ExtraNginxConfs = new StreamReader(nginxConfStream).ReadToEnd());
        int serverIndex = 0;

        foreach (IToken extraNginxConfigToken in extraNginxConfig.GetTokens())
            if (extraNginxConfigToken is GroupToken extraNginxConfigGroupToken && extraNginxConfigGroupToken.Key.Equals("http", StringComparison.InvariantCultureIgnoreCase))
            {
                foreach (IToken serverToken in extraNginxConfigGroupToken.Tokens)
                    if (serverToken is GroupToken serverGroupToken && serverGroupToken.Key.Equals("server", StringComparison.InvariantCultureIgnoreCase))
                        ++serverIndex;

                break;
            }

        NginxConfs = extraNginxConfig
            .AddOrUpdate("worker_processes", "auto")
            .AddOrUpdate("events:worker_connections", "65536")
            .AddOrUpdate("http:proxy_set_header", "Host $http_host")
            .AddOrUpdate("http:proxy_buffer_size", "64k")
            .AddOrUpdate("http:proxy_buffers", "8 64k")
            .AddOrUpdate("http:proxy_busy_buffers_size", "128k")
            .AddOrUpdate("http:large_client_header_buffers", "4 32k")
            .AddOrUpdate("http:proxy_ssl_server_name", !MainPres.IsFlashing ? "on" : "off")
            .AddOrUpdate($"http:server[{serverIndex}]:listen", $"{NginxHttpPort} default_server")
            .AddOrUpdate($"http:server[{serverIndex}]:return", "https://$host$request_uri");

        foreach (List<(List<(string cealHostIncludeDomain, string cealHostExcludeDomain)> cealHostDomainPairs, string? cealHostSni, string cealHostIp)>? cealHostRules in CealHostRulesDict.Values)
            foreach ((List<(string cealHostIncludeDomain, string cealHostExcludeDomain)> cealHostDomainPairs, string? cealHostSni, string cealHostIp) in cealHostRules ?? [])
            {
                string serverName = "~";

                foreach ((string cealHostIncludeDomain, string cealHostExcludeDomain) in cealHostDomainPairs)
                {
                    if (cealHostIncludeDomain.StartsWith('#'))
                        continue;

                    serverName += "^" + (!string.IsNullOrWhiteSpace(cealHostExcludeDomain) ? $"(?!{cealHostExcludeDomain.Replace(".", "\\.").Replace("*", ".*")})" : string.Empty) +
                                  cealHostIncludeDomain.TrimStart('$').Replace(".", "\\.").Replace("*", ".*") + "$|";
                }

                if (serverName == "~")
                    continue;

                ++serverIndex;

                NginxConfs = NginxConfs
                    .AddOrUpdate($"http:server[{serverIndex}]:server_name", serverName.TrimEnd('|'))
                    .AddOrUpdate($"http:server[{serverIndex}]:listen", $"127.0.0.1:{NginxHttpsPort} ssl")
                    .AddOrUpdate($"http:server[{serverIndex}]:ssl_certificate", Path.GetFileName(MainConst.NginxCertPath))
                    .AddOrUpdate($"http:server[{serverIndex}]:ssl_certificate_key", Path.GetFileName(MainConst.NginxKeyPath))
                    .AddOrUpdate($"http:server[{serverIndex}]:location", "/", true)
                    .AddOrUpdate($"http:server[{serverIndex}]:location:proxy_pass", $"https://{cealHostIp}");

                NginxConfs = cealHostSni == null ?
                    NginxConfs.AddOrUpdate($"http:server[{serverIndex}]:proxy_ssl_server_name", "off") :
                    NginxConfs.AddOrUpdate($"http:server[{serverIndex}]:proxy_ssl_name", cealHostSni);
            }
    }
    private async void MihomoConfWatcher_Changed(object sender, FileSystemEventArgs e)
    {
        if (!MainConst.IsAdmin || !MainPres.IsMihomoExist)
            return;

        try
        {
            if (!File.Exists(MainConst.MihomoConfPath))
                await File.Create(MainConst.MihomoConfPath).DisposeAsync();

            foreach (IPEndPoint activeTcpListener in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
                if (activeTcpListener.Port == MihomoMixedPort)
                    MihomoMixedPort++;
                else if (activeTcpListener.Port > MihomoMixedPort)
                    break;

            await using FileStream mihomoConfStream = new(MainConst.MihomoConfPath, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Dictionary<string, object> mihomoConfDict = new DeserializerBuilder()
                .WithNamingConvention(HyphenatedNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build()
                .Deserialize<Dictionary<string, object>>(ExtraMihomoConfs = await new StreamReader(mihomoConfStream).ReadToEndAsync()) ?? [];

            mihomoConfDict["mixed-port"] = MihomoMixedPort;
            mihomoConfDict["dns"] = new
            {
                enable = true,
                listen = ":53",
                enhancedMode = "redir-host",
                nameserver = MainConst.MihomoNameServers
            };
            mihomoConfDict["tun"] = new
            {
                enable = true,
                stack = "system",
                autoRoute = true,
                autoDetectInterface = true,
                dnsHijack = new[] { "any:53", "tcp://any:53" }
            };

            MihomoConfs = new SerializerBuilder().WithNamingConvention(HyphenatedNamingConvention.Instance).Build().Serialize(mihomoConfDict);
        }
        catch { MihomoConfs = string.Empty; }
    }
    private void MainWin_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyboardDevice.Modifiers != ModifierKeys.Control)
            return;

        if (e.Key == Key.W)
            Application.Current.Shutdown();
        else if (e.Key == Key.H)
        {
            System.Windows.Forms.NotifyIcon notifyIcon = new() { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location), Text = MainConst.NotifyIconText, Visible = true };

            notifyIcon.Click += (_, _) =>
            {
                Show();

                notifyIcon.Dispose();
            };

            Hide();
        }
    }
}
