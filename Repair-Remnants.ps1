# Repair-Remnants.ps1
# 紧急修复：MistSail / Sheas Cealer 非正常退出后留下的 hosts 劫持与根证书残留。
# 用法：右键“以管理员身份运行 PowerShell”，然后执行
#   powershell -ExecutionPolicy Bypass -File .\Repair-Remnants.ps1
#
# 本脚本做四件事：删除 hosts 中的 Cealing 区块、删除 Cealing 根证书、恢复被本程序改写的
# 系统自动配置脚本（PAC）、刷新 DNS 缓存。不会改动固定代理（Clash 等自行管理）。

$ErrorActionPreference = 'Stop'

$HostsPath  = Join-Path $env:SystemRoot 'System32\drivers\etc\hosts'
$StartToken = '# Cealing Nginx Start'
$EndToken   = '# Cealing Nginx End'
$CertSubject = 'CN=Cealing Cert Root'

function Assert-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($id)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "需要管理员权限：hosts 文件只有 Administrators 有写权限。请以管理员身份重新运行。"
    }
}

Write-Host '=== 1/5 检查权限 ===' -ForegroundColor Cyan
Assert-Admin
Write-Host 'OK：已获得管理员权限'

Write-Host ''
Write-Host '=== 2/5 清理 hosts ===' -ForegroundColor Cyan

$original = Get-Content -LiteralPath $HostsPath -Raw
$startCount = ([regex]::Matches($original, [regex]::Escape($StartToken))).Count
$endCount   = ([regex]::Matches($original, [regex]::Escape($EndToken))).Count
Write-Host "发现 $startCount 个起始标记、$endCount 个结束标记"

if ($startCount -gt 0 -or $endCount -gt 0) {
    $backup = "$HostsPath.mistsail-backup-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
    Copy-Item -LiteralPath $HostsPath -Destination $backup -Force
    Write-Host "已备份到：$backup" -ForegroundColor DarkGray

    # (?s) 让 . 匹配换行；非贪婪逐块删除，可处理多个堆叠区块，
    # 以及 '# Cealing Nginx End# Cealing Nginx Start' 这种缺换行的粘连写法。
    $cleaned = [regex]::Replace($original, "(?s)$([regex]::Escape($StartToken)).*?$([regex]::Escape($EndToken))", '')

    # 收尾：抹掉尾部空白、折叠连续空行、补回行尾换行
    $cleaned = [regex]::Replace($cleaned, '(\r?\n){3,}', "`r`n`r`n")
    $cleaned = $cleaned.TrimEnd() + "`r`n"

    Set-ItemProperty -LiteralPath $HostsPath -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
    [System.IO.File]::WriteAllText($HostsPath, $cleaned, [System.Text.Encoding]::UTF8)

    $check = Get-Content -LiteralPath $HostsPath -Raw
    if ($check -match [regex]::Escape($StartToken) -or $check -match [regex]::Escape($EndToken)) {
        throw 'hosts 中仍有残留标记，清理未完成。'
    }
    Write-Host "已删除 Cealing 区块（$startCount 块）" -ForegroundColor Green
} else {
    Write-Host 'hosts 中没有 Cealing 区块，无需清理' -ForegroundColor Green
}

Write-Host ''
Write-Host '=== 3/5 清理根证书 ===' -ForegroundColor Cyan
$removedTotal = 0
foreach ($location in @('CurrentUser', 'LocalMachine')) {
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store('Root', $location)
    try {
        $store.Open('ReadWrite')
        $victims = @($store.Certificates | Where-Object { $_.Subject -eq $CertSubject })
        foreach ($cert in $victims) {
            $store.Remove($cert)
            $removedTotal++
            Write-Host "  已删除 [$location] $($cert.Thumbprint)"
        }
        $store.Close()
    } catch {
        Write-Host "  跳过 [$location]：$($_.Exception.Message)" -ForegroundColor Yellow
    } finally {
        $store.Close()
    }
}
Write-Host "共删除 $removedTotal 枚 $CertSubject" -ForegroundColor Green

Write-Host ''
Write-Host '=== 4/5 恢复系统自动配置脚本 ===' -ForegroundColor Cyan
$ProxySettingsKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
$ProxyBackup = Join-Path $env:LOCALAPPDATA 'MistSail\proxy-backup.json'
$currentPac = (Get-ItemProperty -LiteralPath $ProxySettingsKey -Name AutoConfigURL -ErrorAction SilentlyContinue).AutoConfigURL
if ($currentPac -match '^http://127\.0\.0\.1:\d+/mistsail\.pac$') {
    $restoreBackup = $null
    if (Test-Path -LiteralPath $ProxyBackup) {
        try { $restoreBackup = [IO.File]::ReadAllText($ProxyBackup) | ConvertFrom-Json } catch { }
    }
    $restorePac = if ($null -ne $restoreBackup) { $restoreBackup.AutoConfigUrl } else { $null }
    if ([string]::IsNullOrWhiteSpace($restorePac)) {
        Remove-ItemProperty -LiteralPath $ProxySettingsKey -Name AutoConfigURL -ErrorAction SilentlyContinue
        Write-Host '已移除本程序的 PAC 自动配置脚本' -ForegroundColor Green
    } else {
        Set-ItemProperty -LiteralPath $ProxySettingsKey -Name AutoConfigURL -Value $restorePac
        Write-Host "已恢复原自动配置脚本：$restorePac" -ForegroundColor Green
    }
    # 如果当时为启用 PAC 暂停了固定代理，也要把它恢复，否则用户会“代理被关了”。
    if ($null -ne $restoreBackup -and $restoreBackup.PausedFixedProxy -eq $true) {
        New-ItemProperty -LiteralPath $ProxySettingsKey -Name ProxyEnable -Value ([int]$restoreBackup.ProxyEnable) -PropertyType DWord -Force | Out-Null
        if (-not [string]::IsNullOrWhiteSpace([string]$restoreBackup.ProxyServer)) {
            New-ItemProperty -LiteralPath $ProxySettingsKey -Name ProxyServer -Value ([string]$restoreBackup.ProxyServer) -PropertyType String -Force | Out-Null
        }
        Write-Host '已恢复被暂停的固定代理' -ForegroundColor Green
    }
    Remove-Item -LiteralPath $ProxyBackup -Force -ErrorAction SilentlyContinue
    try {
        Add-Type -Namespace MistSailRepair -Name WinInet -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("wininet.dll", SetLastError = true)]
public static extern bool InternetSetOption(System.IntPtr hInternet, int dwOption, System.IntPtr lpBuffer, int dwBufferLength);
'@ -ErrorAction SilentlyContinue
        [MistSailRepair.WinInet]::InternetSetOption([IntPtr]::Zero, 39, [IntPtr]::Zero, 0) | Out-Null
        [MistSailRepair.WinInet]::InternetSetOption([IntPtr]::Zero, 37, [IntPtr]::Zero, 0) | Out-Null
    } catch { }
} else {
    Write-Host '当前 AutoConfigURL 不是本程序的 PAC，无需处理' -ForegroundColor Green
}

Write-Host ''
Write-Host '=== 5/5 刷新 DNS 缓存 ===' -ForegroundColor Cyan
ipconfig /flushdns | Out-Null
Write-Host '已刷新 DNS 缓存' -ForegroundColor Green

Write-Host ''
Write-Host '--- 解析结果核对（应指向真实公网 IP，而不是 127.0.0.1 / 127.0.0.2）---' -ForegroundColor Cyan
foreach ($name in @('x.com', 'api.x.com', 'github.com', 'www.google.com')) {
    $ips = @()
    try { $ips = (Resolve-DnsName -Name $name -Type A -ErrorAction Stop | Where-Object { $_.IPAddress }).IPAddress } catch { }
    if ($ips.Count -eq 0) { Write-Host ("  {0,-16} -> 解析失败/无 A 记录" -f $name) }
    else {
        $bad = @($ips | Where-Object { $_ -like '127.*' })
        $line = "  {0,-16} -> {1}" -f $name, ($ips -join ', ')
        if ($bad.Count -gt 0) { Write-Host $line -ForegroundColor Red; Write-Host '      仍有回环残留！' -ForegroundColor Red }
        else { Write-Host $line -ForegroundColor Green }
    }
}

Write-Host ''
$finalPac = (Get-ItemProperty -LiteralPath $ProxySettingsKey -Name AutoConfigURL -ErrorAction SilentlyContinue).AutoConfigURL
if ([string]::IsNullOrWhiteSpace($finalPac)) { $finalPac = '(未设置)' }
Write-Host ''
Write-Host "AutoConfigURL = $finalPac"
Write-Host '完成。请完全退出并重启浏览器（浏览器有自己的 DNS 缓存），必要时重启 Clash。' -ForegroundColor Green
