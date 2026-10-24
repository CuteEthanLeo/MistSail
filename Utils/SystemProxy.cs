using Microsoft.Win32;
using MistSail.Direct;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace MistSail.Utils;

// Keeps the per-user WinINET auto-config URL that points at PacServer. Only needed while a global
// connection is up; stop, exit, ProcessExit and the next-start repair all restore the exact value
// that was there before. A fixed proxy is only paused when the user explicitly opts into overriding
// an existing proxy, and its original values are restored from the same backup.
internal static class SystemProxy
{
    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private sealed record Backup(string? AutoConfigUrl, int ProxyEnable, string? ProxyServer, bool PausedFixedProxy, DateTime SavedAt);

    // Overridable so the offline checks can exercise the full write/restore path in a temporary
    // registry subtree instead of touching the user's real Internet Settings.
    internal static string SettingsKeyPath { get; set; } = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    internal static string BackupFilePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MistSail", "proxy-backup.json");

    internal static string BackupPath => BackupFilePath;

    internal static string? CurrentAutoConfigUrl
    {
        get
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath);
                return key?.GetValue("AutoConfigURL") as string;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
        }
    }

    // Returns false when an existing PAC/fixed proxy is present and overrideExisting is false, when
    // the backup cannot be written, or when group policy immediately overwrites the value. In every
    // failure path the previous settings are left (or put back) untouched.
    internal static bool Enable(string pacUrl, bool overrideExisting, out string? reason)
    {
        lock (Gate)
        {
            reason = null;
            if (!IsOwnPacUrl(pacUrl))
            {
                reason = "PAC URL 无效";
                return false;
            }
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath, writable: true)
                    ?? Registry.CurrentUser.CreateSubKey(SettingsKeyPath, writable: true);
                if (key is null)
                {
                    reason = "无法打开 Internet Settings";
                    return false;
                }
                string? current = key.GetValue("AutoConfigURL") as string;
                if (IsOwnPacUrl(current))
                {
                    // A previous crashed session left our PAC behind: restore the original first so the
                    // backup below describes the real original value.
                    RestoreLocked(key);
                    current = key.GetValue("AutoConfigURL") as string;
                }
                else if (File.Exists(BackupPath))
                {
                    // A stale backup from a crashed session: restore a fixed proxy we may have
                    // paused, but never overwrite a PAC the user (or a policy) set in the meantime.
                    RestoreLocked(key);
                    current = key.GetValue("AutoConfigURL") as string;
                }
                int proxyEnable = key.GetValue("ProxyEnable") is int value ? value : 0;
                string? proxyServer = key.GetValue("ProxyServer") as string;
                if (!overrideExisting && (!string.IsNullOrWhiteSpace(current) || (proxyEnable == 1 && !string.IsNullOrWhiteSpace(proxyServer))))
                {
                    reason = !string.IsNullOrWhiteSpace(current) ? "已存在自动配置脚本（PAC）" : "已存在固定代理";
                    return false;
                }
                string? directory = Path.GetDirectoryName(BackupPath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);
                bool pauseFixedProxy = overrideExisting && proxyEnable == 1 && !string.IsNullOrWhiteSpace(proxyServer);
                File.WriteAllText(BackupPath, JsonSerializer.Serialize(new Backup(current, proxyEnable, proxyServer, pauseFixedProxy, DateTime.UtcNow), JsonOptions));
                key.SetValue("AutoConfigURL", pacUrl, RegistryValueKind.String);
                // A fixed proxy wins over AutoConfigURL. Only an explicit override may disable it,
                // and the original value is restored from the backup on stop.
                if (pauseFixedProxy)
                    key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                string? read = key.GetValue("AutoConfigURL") as string;
                if (!string.Equals(read, pacUrl, StringComparison.OrdinalIgnoreCase))
                {
                    RestoreLocked(key);
                    reason = "系统策略覆盖了自动配置脚本";
                    return false;
                }
                if (overrideExisting && proxyEnable == 1 && key.GetValue("ProxyEnable") is int stillEnabled && stillEnabled != 0)
                {
                    RestoreLocked(key);
                    reason = "系统策略保持固定代理，PAC 未启用";
                    return false;
                }
                Notify();
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or JsonException)
            {
                reason = ex.Message;
                Restore();
                return false;
            }
        }
    }

    internal static void Restore()
    {
        lock (Gate)
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath, writable: true);
                if (key is null)
                    return;
                RestoreLocked(key);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Log($"恢复系统自动配置脚本失败：{ex.Message}");
            }
        }
    }

    internal static bool HasResidue()
    {
        try { return File.Exists(BackupPath) || IsOwnPacUrl(CurrentAutoConfigUrl); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }

    // Only our own loopback PAC is ever restored automatically; a value the user or a policy changed
    // while connected is never overwritten.
    internal static bool IsOwnPacUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            return false;
        return uri.Scheme == Uri.UriSchemeHttp && uri.Host == "127.0.0.1" &&
               uri.AbsolutePath.Equals("/mistsail.pac", StringComparison.OrdinalIgnoreCase) &&
               uri.Query.Length == 0;
    }

    private static void RestoreLocked(RegistryKey key)
    {
        try
        {
            string? current = key.GetValue("AutoConfigURL") as string;
            Backup? backup = ReadBackup();
            bool ownPac = IsOwnPacUrl(current);
            if (!ownPac && backup is null)
            {
                TryDeleteBackup();
                return;
            }
            if (ownPac)
            {
                if (!string.IsNullOrWhiteSpace(backup?.AutoConfigUrl))
                    key.SetValue("AutoConfigURL", backup.AutoConfigUrl, RegistryValueKind.String);
                else
                    key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
            }
            // Only put the fixed proxy back when this run actually paused it; a backup that never
            // touched ProxyEnable must not overwrite a value the user changed meanwhile.
            if (backup is { PausedFixedProxy: true })
            {
                key.SetValue("ProxyEnable", backup.ProxyEnable, RegistryValueKind.DWord);
                if (!string.IsNullOrWhiteSpace(backup.ProxyServer))
                    key.SetValue("ProxyServer", backup.ProxyServer, RegistryValueKind.String);
            }
            TryDeleteBackup();
            Notify();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log($"恢复系统自动配置脚本失败：{ex.Message}");
        }
    }

    private static Backup? ReadBackup()
    {
        try
        {
            if (!File.Exists(BackupPath))
                return null;
            return JsonSerializer.Deserialize<Backup>(File.ReadAllText(BackupPath), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void TryDeleteBackup()
    {
        try { File.Delete(BackupPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void Notify()
    {
        try
        {
            InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }

    private static void Log(string message)
    {
        try { DirectLog.Write(message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
}
