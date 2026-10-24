[CmdletBinding()]
param(
    [switch]$SkipChecks
)

$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$taskArtifacts = Join-Path $taskRoot 'artifacts'
$taskBuildId = Get-Date -Format 'yyyyMMdd-HHmmss'
$taskStage = Join-Path $taskArtifacts "stage\$taskBuildId"
$taskOutput = Join-Path $taskStage 'MistSail-win-x64'
$taskUpstream = Join-Path $taskRoot 'vendor\upstream-1.1.4'
$taskArchive = Join-Path $taskArtifacts "MistSail-v1.1.4.7-win-x64-$taskBuildId.zip"

if (!(Test-Path -LiteralPath (Join-Path $taskUpstream 'Cealing-Nginx.exe'))) {
    throw '缺少 vendor/upstream-1.1.4，请先解压官方 1.1.4 免安装发行包到该目录。'
}
foreach ($taskCore in @('Ona-Core.dll', 'Sheas-Core.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $taskRoot "lib\$taskCore"))) {
        throw "缺少 lib/$taskCore，请从官方 1.1.4 发行包恢复该文件。"
    }
}
if (!(Test-Path -LiteralPath (Join-Path $taskRoot 'vendor\Cealing-Host-U.json'))) {
    throw '缺少 vendor/Cealing-Host-U.json 的上游规则快照。'
}
if (!(Test-Path -LiteralPath (Join-Path $taskRoot 'Cealing-Host-L.json'))) {
    throw '缺少 Cealing-Host-L.json 的本地规则（含 google.com.hk）。'
}
foreach ($taskDriver in @('WinDivert.dll', 'WinDivert64.sys')) {
    if (!(Test-Path -LiteralPath (Join-Path $taskRoot "vendor\windivert\$taskDriver"))) {
        throw "缺少 vendor/windivert/$taskDriver（WinDivert 2.2.2 x64，内核级 desync 需要）。"
    }
}
if (Test-Path -LiteralPath (Join-Path $taskRoot 'Cealing-Host-X.json')) {
    throw 'Cealing-Host-X.json 已废弃（X 改由分片中继处理），请删除后再打包。'
}

Push-Location $taskRoot
try {
    if (-not $SkipChecks) {
        # Regenerates direct-config.json from the built-in defaults, then runs the loopback-only self checks.
        & dotnet run --project '.\Verification\RelayCheck' -c Release -- $taskRoot --write-config
        if ($LASTEXITCODE -ne 0) { throw "RelayCheck 自检失败：$LASTEXITCODE" }
    }
    $null = Get-Content -LiteralPath (Join-Path $taskRoot 'direct-config.json') -Raw | ConvertFrom-Json
    $null = Get-Content -LiteralPath (Join-Path $taskRoot 'vendor\Cealing-Host-U.json') -Raw | ConvertFrom-Json

    New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
    & dotnet publish '.\MistSail.csproj' -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=false -p:PublishReadyToRun=false -o $taskOutput --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败：$LASTEXITCODE" }
}
finally { Pop-Location }

foreach ($taskEngine in @('Cealing-Nginx.exe', 'Cealing-Mihomo.exe')) {
    Copy-Item -LiteralPath (Join-Path $taskUpstream $taskEngine) -Destination $taskOutput
}
foreach ($taskDocument in @('README.md', 'README_DIRECT.md', 'README_X_RELAY.md', 'direct-config.json')) {
    Copy-Item -LiteralPath (Join-Path $taskRoot $taskDocument) -Destination $taskOutput
}
Copy-Item -LiteralPath (Join-Path $taskRoot 'vendor\Cealing-Host-U.json') -Destination $taskOutput
Copy-Item -LiteralPath (Join-Path $taskRoot 'Cealing-Host-L.json') -Destination (Join-Path $taskOutput 'Cealing-Host-L.json')
Set-Content -LiteralPath (Join-Path $taskOutput 'nginx.conf') -Value '' -NoNewline -Encoding utf8
Set-Content -LiteralPath (Join-Path $taskOutput 'config.yaml') -Value '' -NoNewline -Encoding utf8
Remove-Item -LiteralPath (Join-Path $taskOutput 'MistSail.pdb') -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $taskOutput 'RelayCheck.exe') -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $taskOutput 'RelayCheck.dll') -ErrorAction SilentlyContinue

$taskQuickStart = @'
雾帆 MistSail 1.1.4.7 —— 一键连接 + X 分片直连 + 自动寻路。修复 WinDivert 接收线程在长时间运行后被单个异常包带崩的闪退；X 连不上时会自动刷新候选 IP、找仍有响应的边缘 IP 并逐个试 ClientHello 发法（1–2 分钟），找到就自动切换并重测。

1. 将压缩包完整解压到一个新目录，不要覆盖正在运行的旧版。
2. 在旧版里先停止“全局伪造 / 全局净化”，再退出旧程序。
3. 关闭 Clash Verge 等梯子（包括 TUN 和系统代理）；TUN 开着时诊断会直接报“本轮无效”。
4. 右键 MistSail.exe，以管理员身份运行；或直接打开后点“以管理员身份重新打开”。
5. 点击中间的大圆按钮。变绿后下方“连通性”卡片会自动检测 YouTube、X、GitHub。
6. 新开标签访问 https://x.com，检查页面、登录、图片和视频。
7. 不行就点“复制诊断信息”，把内容和 logs/relay.log、logs/error.log、截图一起发回来。
8. 用完点一下大按钮断开；直接关窗口也会先断开再退出。

YouTube 页面仍用规则表；视频动态域名（*.googlevideo.com 等）在连接期间由本机 PAC 交给 127.0.0.1 的回环转发器，经 DoH 解析与握手分片后直连，断开时自动恢复系统代理设置。
X 的域名改走 127.0.0.2 上的分片中继，不再经过 nginx；google.com.hk 由 Cealing-Host-L.json 本地规则接管；原理、配置和日志含义见 README_X_RELAY.md。
旧版的全部按钮都在底部“更多功能”里。
'@
Set-Content -LiteralPath (Join-Path $taskOutput '先读我.txt') -Value $taskQuickStart -Encoding utf8
$taskManifest = Get-ChildItem -LiteralPath $taskOutput -Recurse -File | Sort-Object FullName | ForEach-Object {
    $taskRelative = $_.FullName.Substring($taskOutput.Length + 1)
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash, $taskRelative
}
Set-Content -LiteralPath (Join-Path $taskOutput 'SHA256SUMS.txt') -Value $taskManifest -Encoding utf8

Compress-Archive -LiteralPath $taskOutput -DestinationPath $taskArchive -CompressionLevel Optimal
$taskHash = (Get-FileHash -LiteralPath $taskArchive -Algorithm SHA256).Hash
Set-Content -LiteralPath "$taskArchive.sha256.txt" -Value "$taskHash  $([IO.Path]::GetFileName($taskArchive))" -Encoding utf8
Write-Output "PACKAGE=$taskArchive"
Write-Output "FOLDER=$taskOutput"
Write-Output "SHA256=$taskHash"
Write-Output '只完成离线自检、编译和打包；未运行程序或进行连通性测试。'
