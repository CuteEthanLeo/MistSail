using MistSail.Consts;
using MistSail.Direct;
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MistSail.Utils;

internal static class NginxCleaner
{
    internal static readonly SemaphoreSlim IsNginxCleaningSemaphore = new(1);

    private static readonly string StartToken = MainConst.HostsConfStartMarker.TrimEnd('\r', '\n');

    // 成对区块：非贪婪匹配，一次覆盖多个堆叠区块，也能吃下
    // "# Cealing Nginx End# Cealing Nginx Start" 这种缺少换行的粘连写法。
    private static readonly Regex BlockRegex = new(
        $"{Regex.Escape(StartToken)}.*?{Regex.Escape(MainConst.HostsConfEndMarker)}",
        RegexOptions.Singleline | RegexOptions.Compiled);

    // 落单的标记：上次写盘被强杀截断时会留下半截区块。
    private static readonly Regex StrayStartRegex = new(
        $"(?m)^[^\\S\\r\\n]*{Regex.Escape(StartToken)}[^\\S\\r\\n]*\\r?\\n?", RegexOptions.Compiled);

    private static readonly Regex StrayEndRegex = new(
        $"(?m)^[^\\S\\r\\n]*{Regex.Escape(MainConst.HostsConfEndMarker)}[^\\S\\r\\n]*\\r?\\n?", RegexOptions.Compiled);

    internal static void LogFailure(string what, Exception ex) =>
        DirectLog.Write($"[清理] {what}：{ex.GetType().Name} {ex.Message}");

    /// <summary>hosts 中是否仍留着本程序写入的区块。用于启动自检与定时巡检。</summary>
    internal static bool HasHostsResidue()
    {
        try
        {
            string hostsContent = File.ReadAllText(MainConst.HostsConfPath);

            return hostsContent.Contains(StartToken, StringComparison.Ordinal) ||
                   hostsContent.Contains(MainConst.HostsConfEndMarker, StringComparison.Ordinal);
        }
        catch { return false; }
    }

    /// <summary>恢复 hosts 并移除自签根证书。幂等，失败只记日志，绝不向外抛异常。</summary>
    internal static async Task Clean()
    {
        if (!await IsNginxCleaningSemaphore.WaitAsync(0).ConfigureAwait(true))
            return;

        try { CleanCore(); }
        finally { IsNginxCleaningSemaphore.Release(); }
    }

    /// <summary>注销/进程退出阶段用：不排队等待异步调度，尽力而为。</summary>
    internal static void CleanSync()
    {
        if (!IsNginxCleaningSemaphore.Wait(0))
            return;

        try { CleanCore(); }
        finally { IsNginxCleaningSemaphore.Release(); }
    }

    private static void CleanCore()
    {
        // 清理是最后一道防线：这里是唯一允许失败的地方，但绝不能再把异常抛出去
        // （定时器与 ProcessExit 都是 fire-and-forget 调用）。
        try { RemoveHostsBlocks(); }
        catch (Exception ex) { LogFailure("恢复 hosts 失败", ex); }

        // A crashed global session may also have left the per-user PAC URL pointing at a dead
        // loopback port. Restore it here so the next start, stop or ProcessExit fixes both remnants.
        try { SystemProxy.Restore(); }
        catch (Exception ex) { LogFailure("恢复系统自动配置脚本失败", ex); }

        try { RemoveRootCerts(); }
        catch (Exception ex) { LogFailure("清理根证书失败", ex); }
    }

    private static void RemoveHostsBlocks()
    {
        string hostsContent;

        try { hostsContent = File.ReadAllText(MainConst.HostsConfPath); }
        catch (Exception ex) { LogFailure("读取 hosts 失败", ex); return; }

        bool hasStart = hostsContent.Contains(StartToken, StringComparison.Ordinal);
        bool hasEnd = hostsContent.Contains(MainConst.HostsConfEndMarker, StringComparison.Ordinal);

        if (!hasStart && !hasEnd)
            return;

        // 每次连接都会追加一块，异常退出后就会堆叠；结束标记缺少换行时还会和下一块粘在同一行。
        // 所以这里按整块删，而不是逐行解析。
        string cleaned = BlockRegex.Replace(hostsContent, string.Empty);
        cleaned = StrayStartRegex.Replace(cleaned, string.Empty);
        cleaned = StrayEndRegex.Replace(cleaned, string.Empty);

        // 兜底：即使标记出现在行中间这种异常位置，也不能把它留在 hosts 里，
        // 否则 HasHostsResidue 会一直为真、每次巡检都重复清理。
        cleaned = cleaned.Replace(StartToken, string.Empty, StringComparison.Ordinal);
        cleaned = cleaned.Replace(MainConst.HostsConfEndMarker, string.Empty, StringComparison.Ordinal);

        cleaned = Regex.Replace(cleaned, "(\r?\n){3,}", Environment.NewLine + Environment.NewLine);
        if (cleaned.Length > 0 && !cleaned.EndsWith('\n'))
            cleaned += Environment.NewLine;

        try
        {
            File.SetAttributes(MainConst.HostsConfPath, File.GetAttributes(MainConst.HostsConfPath) & ~FileAttributes.ReadOnly);
            File.WriteAllText(MainConst.HostsConfPath, cleaned);
        }
        catch (Exception ex)
        {
            // 没有管理员权限时写 hosts 必然失败。必须留下痕迹，
            // 否则用户面对的是一台"明明有代理却上不了网"的机器。
            LogFailure("写入 hosts 失败（需要管理员权限）", ex);
        }
    }

    private static void RemoveRootCerts()
    {
        // 当前用户与机器级都清一遍：用户手动导入过、或旧版本装到机器级时都要能收掉。
        StoreLocation[] locations = MainConst.IsAdmin
            ? [StoreLocation.CurrentUser, StoreLocation.LocalMachine]
            : [StoreLocation.CurrentUser];

        foreach (StoreLocation location in locations)
            try
            {
                using X509Store certStore = new(StoreName.Root, location, OpenFlags.ReadWrite);

                // 先取快照再删除：一边枚举一边改集合会漏掉后面的同名副本。
                X509Certificate2[] victims = certStore.Certificates
                    .Where(cert => string.Equals(cert.Subject, MainConst.NginxRootCertSubjectName, StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                foreach (X509Certificate2 victim in victims)
                    for (int attempt = 1; ; attempt++)
                        try
                        {
                            certStore.Remove(victim);

                            break;
                        }
                        catch (Exception ex)
                        {
                            // 旧实现这里是 while(true) + 空 catch：删除持续失败时会把 UI 线程转死，
                            // 退出流程永远走不完——"关不掉的窗口"就是这么来的。改成有限重试。
                            if (attempt >= 3)
                            {
                                LogFailure($"删除根证书失败（{location}）", ex);

                                break;
                            }

                            Thread.Sleep(50);
                        }
            }
            catch (Exception ex) { LogFailure($"打开证书存储失败（{location}）", ex); }
    }
}
