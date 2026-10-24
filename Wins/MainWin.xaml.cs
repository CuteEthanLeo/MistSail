using MaterialDesignThemes.Wpf;
using Microsoft.Win32;
using NginxConfigParser;
using Ona_Core;
using MistSail.Consts;
using MistSail.Direct;
using MistSail.Preses;
using MistSail.Proces;
using MistSail.Utils;
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

namespace MistSail.Wins;

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
    private DirectRelay? VideoProxy;
    private PacServer? VideoPac;
    private DateTime LastAutoProbe = DateTime.MinValue;
    private bool DirectStarting;
    private bool WindowClosing;
    private bool IsExiting;
    // nginx 连续多少次巡检都不在，用于把"真的没了"和启动/停止的瞬时状态分开。
    private int NginxMissingTicks;
    private SniRelay? Relay;
    private WinDivertDesync? Desync;
    private string? DesyncError; // why the kernel engine is not running, for the verdict and diagnostics
    private DirectOptions? RelayOptions;
    private readonly SemaphoreSlim SiteCheckSemaphore = new(1);
    private readonly SemaphoreSlim LineProbeSemaphore = new(1);
    private LineProbe.Report? LineReport;
    // Connectivity checks must follow the hosts file like a browser would: no system proxy, any certificate.
    private readonly HttpClient CheckClient = new(new SocketsHttpHandler
    {
        UseProxy = false,
        UseCookies = false,
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(8),
        SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true }
    }) { Timeout = TimeSpan.FromSeconds(25) };

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

        // 上一次会话被强杀时，hosts 可能还写着本机回环地址而 nginx 已经不在了。此时命中的域名
        // （X 以及规则表里的所有站点）都会连向没人监听的端口，开着代理也上不去，必须先清掉。
        if (!MainPres.IsNginxRunning && (NginxCleaner.HasHostsResidue() || SystemProxy.HasResidue()))
        {
            // Clean() restores hosts, certificates and the per-user PAC URL. Only the hosts part
            // needs administrator rights; SystemProxy works in the current user's registry.
            await NginxCleaner.Clean();
            if (NginxCleaner.HasHostsResidue() && !MainConst.IsAdmin)
                MessageBox.Show(MainConst._HostsResidueNeedsAdminMsg);
        }

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

        if (MainPres.IsNginxRunning && !MainPres.IsNginxIniting &&
            MessageBox.Show(MainConst._ExitWhileConnectedPrompt, string.Empty, MessageBoxButton.YesNo) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }

        // Keep the window alive until hosts and certificates are restored, then close for real.
        e.Cancel = true;
        IsExiting = true;
        WindowClosing = true;
        IsEnabled = false;

        // 无论清理成功与否都必须走到 Shutdown。旧实现里 StopGlobalAsync 一抛异常就会从
        // async void 逃出去，而窗口已经"取消关闭 + 禁用"，于是程序留在内存里不响应——这就是"退不掉"。
        try
        {
            if (MainPres.IsNginxRunning)
                await StopGlobalAsync();
        }
        catch (Exception ex) { NginxCleaner.LogFailure("停止全局模式失败", ex); }

        try { await FinishClosingAsync(); }
        catch (Exception ex) { NginxCleaner.LogFailure("退出清理失败", ex); }

        Application.Current.Shutdown();
    }
    private async Task FinishClosingAsync()
    {
        // 连接流程可能还在进行（hosts 刚写好、nginx 正在起）。先等它落地再清理，
        // 否则它会在我们清完之后又把 hosts 区块写回去。
        if (await IsNginxLaunchingSemaphore.WaitAsync(TimeSpan.FromSeconds(15)))
            IsNginxLaunchingSemaphore.Release();

        StopDirectTransport();
        StopVideoProxy();
        StopRelay();
        ProxyTimer.Stop();
        CealHostWatcher.EnableRaisingEvents = false;
        NginxConfWatcher.EnableRaisingEvents = false;
        MihomoConfWatcher.EnableRaisingEvents = false;
        if (MainPres.IsMihomoIniting)
            await File.WriteAllTextAsync(MainConst.MihomoConfPath, ExtraMihomoConfs);

        // 不能只看 IsNginxRunning：nginx 被外部杀掉时它已经是 false，而 hosts 区块还在。
        // 清理是幂等的，漏清却会让整台机器上不了网，所以退出前一律清一遍。
        await NginxCleaner.Clean();
        if (await NginxCleaner.IsNginxCleaningSemaphore.WaitAsync(TimeSpan.FromSeconds(5)))
            NginxCleaner.IsNginxCleaningSemaphore.Release();
        MainPres.IsNginxRunning = false;
    }

    // hosts 末行没有换行时直接追加，新旧区块会粘成一行（旧的 "# Cealing Nginx End# Cealing Nginx Start"）。
    private static bool HostsFileNeedsLeadingNewline()
    {
        try
        {
            using FileStream hostsStream = new(MainConst.HostsConfPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            if (hostsStream.Length == 0)
                return false;

            hostsStream.Seek(-1, SeekOrigin.End);

            return hostsStream.ReadByte() is not (10 or 13); // \n 或 \r
        }
        catch { return false; }
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

                // 结束标记后必须补回换行，否则下一次连接追加的起始标记会粘在同一行。
                hostsConfAppendContent += MainConst.HostsConfEndMarker + Environment.NewLine;

                File.SetAttributes(MainConst.HostsConfPath, File.GetAttributes(MainConst.HostsConfPath) & ~FileAttributes.ReadOnly);
                if (HostsFileNeedsLeadingNewline())
                    hostsConfAppendContent = Environment.NewLine + hostsConfAppendContent;
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
        LineReport = null;
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
                "MistSail", "BrowserProfiles", $"{browserName}-{DirectTransport.Port}");
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
            DirectState? state = DirectState.Load();
            StartDesync(options, state);
            // Only send plain ClientHellos when a desync strategy is actually armed (pinned or
            // replayed from state); otherwise the relay keeps shaping in user space as before.
            Relay = new SniRelay(options, state: state, forcePlain: Desync?.Armed is not null);
            Relay.Status += Relay_Status;
            Relay.HostFailure += Relay_HostFailure;
            Relay.Start();
            StartVideoProxy();
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
    // Brings up the WinDivert desync engine when DesyncMode is enabled. Failure (missing driver,
    // not elevated) is non-fatal: the relay still runs in user-space mode, and the status says so.
    private void StartDesync(DirectOptions options, DirectState? state)
    {
        DesyncError = null;
        if (!options.DesyncEnabled)
        {
            DesyncError = "direct-config.json 的 DesyncMode 为 off";
            return;
        }
        try
        {
            var engine = new WinDivertDesync(options.DesyncEdges());
            engine.Status += Relay_Status;
            engine.EngineFailed += () =>
            {
                if (!ReferenceEquals(Desync, engine)) return;
                if (Relay is not null) Relay.ForcePlain = false;
                DirectLog.Write(SniRelay.LogFile, "desync 接收线程异常停止：中继回退用户态分片，请断开重连。");
            };
            engine.Start();
            // Pin from config, else replay the finder's last winner from state; else leave disarmed
            // until the finder runs on the first X failure.
            DesyncStrategy? pinned = options.ConfiguredDesync;
            if (pinned is null && state?.Desync is { } saved && DesyncStrategy.TryParse(saved, out var learned))
                pinned = learned;
            if (pinned is not null)
                engine.Arm(pinned);
            Desync = engine;
        }
        catch (Exception ex) when (ex is DllNotFoundException or InvalidOperationException or ArgumentException)
        {
            Desync = null;
            DesyncError = ex.Message;
            DirectLog.Write(SniRelay.LogFile, $"desync 引擎未启动（继续用户态分片）：{ex.Message}");
            MainPres.RelayStatus = string.Format(MainConst.RelayStatusFailed, ex.Message);
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
        StopVideoProxy();
        if (Relay is not null)
        {
            Relay.Status -= Relay_Status;
            Relay.HostFailure -= Relay_HostFailure;
            Relay.Dispose();
            Relay = null;
        }
        if (Desync is not null)
        {
            Desync.Status -= Relay_Status;
            Desync.Dispose();
            Desync = null;
        }
        MainPres.IsRelayRunning = false;
        MainPres.RelayStatus = MainConst.RelayStatusIdle;
    }

    // Repeated X failures trigger one finder run in the background. The SiteCheck path already does
    // this after its HTTP probe; this catches the user who opens the browser before that check ends.
    private void Relay_HostFailure(string host, int attempts)
    {
        if (WindowClosing || Dispatcher.HasShutdownStarted || !MainPres.IsNginxRunning || Relay is null || RelayOptions is null)
            return;
        if (host is not ("x.com" or "www.x.com" or "twitter.com" or "www.twitter.com"))
            return;
        if (DateTime.UtcNow - LastAutoProbe < TimeSpan.FromMinutes(3))
            return;
        LastAutoProbe = DateTime.UtcNow;
        Dispatcher.BeginInvoke(new Action(() => _ = RunLineProbeAsync()));
    }

    // Global mode also brings up the loopback CONNECT relay and a PAC script for the dynamic video
    // hosts that the hosts file cannot wildcard. Optional and non-fatal: if it cannot start, the
    // hosts + nginx path keeps working exactly as before.
    private void StartVideoProxy()
    {
        if (RelayOptions is null || !RelayOptions.DynamicProxyEnabled || RelayOptions.DynamicProxyHosts.Length == 0)
        {
            DirectLog.Write(SniRelay.LogFile, "动态视频代理未启用（配置关闭或域名列表为空）");
            return;
        }
        StopVideoProxy();
        try
        {
            VideoProxy = new DirectRelay(RelayOptions, ephemeral: true, allowSystemDnsFallback: true);
            VideoProxy.Status += VideoProxy_Status;
            VideoProxy.Start();
            VideoPac = new PacServer(RelayOptions.DynamicProxyHosts, VideoProxy.Port);
            VideoPac.Status += VideoProxy_Status;
            VideoPac.Start();
            if (!SystemProxy.Enable(VideoPac.PacUrl, RelayOptions.DynamicProxyOverrideExisting, out string? reason))
            {
                DirectLog.Write(SniRelay.LogFile, $"动态视频 PAC 未启用：{reason}");
                StopVideoProxy();
                return;
            }
            DirectLog.Write(SniRelay.LogFile,
                $"动态视频代理已启用：{VideoPac.PacUrl} -> 127.0.0.1:{VideoProxy.Port}；域名 {string.Join("、", RelayOptions.DynamicProxyHosts)}");
        }
        catch (Exception ex)
        {
            DirectLog.Write(SniRelay.LogFile, $"动态视频代理启动失败：{ex.GetType().Name} {ex.Message}");
            StopVideoProxy();
        }
    }

    private void StopVideoProxy()
    {
        // Restore first: a dead loopback PAC port must never outlive this process.
        SystemProxy.Restore();
        if (VideoPac is not null)
        {
            try
            {
                VideoPac.Status -= VideoProxy_Status;
                VideoPac.Dispose();
            }
            catch (Exception ex) { DirectLog.Write(SniRelay.LogFile, $"停止 PAC 失败：{ex.GetType().Name}"); }
            VideoPac = null;
        }
        if (VideoProxy is not null)
        {
            try
            {
                VideoProxy.Status -= VideoProxy_Status;
                VideoProxy.Dispose();
            }
            catch (Exception ex) { DirectLog.Write(SniRelay.LogFile, $"停止视频转发器失败：{ex.GetType().Name}"); }
            VideoProxy = null;
        }
    }

    private void VideoProxy_Status(string text) => DirectLog.Write(SniRelay.LogFile, "[video] " + text);

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
        if (Relay is not null && MainPres.SiteChecks.Any(check => check.Host == "x.com" && check.State == SiteCheckState.Fail) && LineReport is null)
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
            LastAutoProbe = DateTime.UtcNow;
            DirectOptions options = RelayOptions;
            SniRelay? relay = Relay;
            WinDivertDesync? desync = Desync;
            string? desyncError = DesyncError;
            using CancellationTokenSource cancel = new(TimeSpan.FromMinutes(8));
            // Give the finder the current DoH view before probing: a pinned list can be stale for
            // weeks while Cloudflare rotates the edges that serve the zone.
            if (relay is not null)
            {
                try { await relay.RefreshCandidatesAsync(["x.com", "pbs.twimg.com"], cancel.Token); }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException)
                {
                    DirectLog.Write(SniRelay.LogFile, $"诊断前刷新候选失败：{ex.GetType().Name}");
                }
            }
            LineProbe.Report report = await Task.Run(() => LineProbe.RunAsync(options,
                host => relay is not null && relay.LastClientHelloByHost.TryGetValue(host, out byte[]? hello) ? hello : null,
                text => DirectLog.Write(SniRelay.LogFile, text),
                (ip, until) => relay?.MarkUnreachable(ip, until),
                desync,
                desyncError,
                cancel.Token,
                host => relay is not null ? relay.DynamicCandidatesFor(host) : Array.Empty<IPAddress>()));
            LineReport = report;
            MainPres.LineVerdict = string.Format(MainConst.LineProbeVerdict, report.Text);

            // The relay may have been stopped or restarted while the finder ran: apply only to the same instance.
            if (report.Valid && relay is not null && ReferenceEquals(relay, Relay) && !WindowClosing)
            {
                foreach (IPAddress ip in report.Good)
                    relay.MarkGood(ip);
                if (report.Winner is not null && !relay.IsPinnedByConfig)
                {
                    relay.ApplyStrategy(report.Winner);
                    if (report.FastlyWinner is not null && report.FastlyWinner != report.Winner)
                        foreach (string host in options.RelayHosts.Keys.Where(host => host.EndsWith("twimg.com", StringComparison.OrdinalIgnoreCase)))
                            relay.ApplyStrategy(report.FastlyWinner, host);
                }
                // Kernel-level winner: arm the engine for every live connection and remember it.
                if (report.DesyncWinner is not null && desync is not null && ReferenceEquals(desync, Desync) && options.ConfiguredDesync is null)
                {
                    desync.Arm(report.DesyncWinner);
                    relay.DesyncName = report.DesyncWinner.Name;
                    relay.ForcePlain = true; // the wire layer now frames the ClientHello
                }
                relay.Snapshot().Save();
                if (report.Winner is not null || report.DesyncWinner is not null)
                    _ = RunSiteChecksAsync();
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException or InvalidOperationException)
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
        LineReport = null;
        await RunLineProbeAsync();
    }
    private async Task CheckSiteAsync(SiteCheckItem check)
    {
        Stopwatch watch = Stopwatch.StartNew();
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, check.Url);
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) MistSail-Check");
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
        text.AppendLine($"MistSail {Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion} · {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        text.AppendLine($"admin={MainConst.IsAdmin} nginx={MainPres.IsNginxRunning} relay={MainPres.IsRelayRunning} ports={NginxHttpPort}/{NginxHttpsPort} connectedAt={MainPres.ConnectedAt:HH:mm:ss}");
        text.AppendLine($"relay-status: {MainPres.RelayStatus}");
        text.AppendLine($"desync: {(Desync is not null ? $"running armed={Desync.Armed?.Name ?? "-"}" : $"not running ({DesyncError ?? "relay stopped"})")}");
        text.AppendLine($"video-proxy: {(VideoProxy is not null && VideoPac is not null ? $"running proxy=127.0.0.1:{VideoProxy.Port} pac={VideoPac.PacUrl}" : "not running")}");
        text.AppendLine($"video-hosts: {(RelayOptions is not null && RelayOptions.DynamicProxyEnabled ? string.Join(",", RelayOptions.DynamicProxyHosts) : "disabled")}");
        text.AppendLine($"system-pac: {SystemProxy.CurrentAutoConfigUrl ?? "-"}");
        foreach (SiteCheckItem check in MainPres.SiteChecks)
            text.AppendLine($"{check.Name}: {check.State} · {check.Detail}");
        if (Relay is not null)
        {
            text.AppendLine($"relay-strategy: {Relay.CurrentStrategy.Name}{(Relay.IsPinnedByConfig ? " (pinned by direct-config.json)" : string.Empty)}");
            foreach (KeyValuePair<string, string> pair in Relay.LastResultByHost.OrderBy(pair => pair.Key))
                text.AppendLine($"relay {pair.Key}: {pair.Value}");
        }
        if (LineReport is not null)
        {
            text.AppendLine("--- line probe ---");
            foreach (LineProbe.Outcome outcome in LineReport.Outcomes)
                text.AppendLine($"{outcome.Step.Name} [{outcome.Step.Ip} sni={outcome.Step.Sni} strategy={outcome.Step.Strategy?.Name ?? "-"}] -> {outcome.Text}{(outcome.Injected ? " (injected)" : string.Empty)} {outcome.Milliseconds}ms connect={outcome.ConnectMs}ms");
            text.AppendLine($"reachable: {string.Join(",", LineReport.Reachable.Select(ip => ip.ToString()))} dead: {string.Join(",", LineReport.Dead.Select(ip => ip.ToString()))}");
            text.AppendLine($"winner: {LineReport.Winner?.Name ?? "-"} fastly: {LineReport.FastlyWinner?.Name ?? "-"} desync: {LineReport.DesyncWinner?.Name ?? "-"}");
            text.AppendLine($"verdict: {LineReport.Text}");
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

        // 启动过程中 hosts 已写好但 nginx 还没起来，这段时间不能判定成"nginx 没了"。
        if (MainPres.IsNginxRunning || MainPres.IsNginxIniting || IsNginxLaunchingSemaphore.CurrentCount == 0)
        {
            NginxMissingTicks = 0;

            return;
        }

        // nginx 已经不在（被任务管理器杀掉、崩溃、或上次会话被强杀）。旧版本在这里假设"退出回调
        // 已经清掉了 hosts 区块"，于是只停了中继：hosts 继续把域名指向没人监听的回环端口，
        // 连同代理一起失效。现在真的去清，并连续多次确认，避免和启动/停止流程互相抢。
        if (++NginxMissingTicks < 10 || WindowClosing || Dispatcher.HasShutdownStarted)
            return;

        NginxMissingTicks = 0;

        if (Relay is not null)
        {
            StopRelay();
            MainPres.ConnectedAt = null;
            foreach (SiteCheckItem check in MainPres.SiteChecks)
                check.Reset();
        }

        if (VideoProxy is not null || VideoPac is not null || SystemProxy.HasResidue())
            StopVideoProxy();

        // hosts 写入需要管理员权限；PAC 恢复只写当前用户注册表，普通权限也应执行。没有管理员时
        // 不要每秒重试 hosts 清理刷日志，交给启动提示。
        if (MainConst.IsAdmin && (NginxCleaner.HasHostsResidue() || SystemProxy.HasResidue()))
            _ = NginxCleaner.Clean();
        else if (!MainConst.IsAdmin && SystemProxy.HasResidue())
            SystemProxy.Restore();
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
