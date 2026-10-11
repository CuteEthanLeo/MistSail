[CmdletBinding()]
param(
    [switch]$SkipChecks
)

$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$taskArtifacts = Join-Path $taskRoot 'artifacts'
$taskBuildId = Get-Date -Format 'yyyyMMdd-HHmmss'
$taskStage = Join-Path $taskArtifacts "stage\$taskBuildId"
$taskOutput = Join-Path $taskStage 'Sheas-Cealer-Direct4-win-x64'
$taskUpstream = Join-Path $taskRoot 'vendor\upstream-1.1.4'
$taskArchive = Join-Path $taskArtifacts "Sheas-Cealer-Direct4-win-x64-$taskBuildId.zip"

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
    & dotnet publish '.\Sheas-Cealer.csproj' -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=false -p:PublishReadyToRun=false -o $taskOutput --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败：$LASTEXITCODE" }
}
finally { Pop-Location }

foreach ($taskEngine in @('Cealing-Nginx.exe', 'Cealing-Mihomo.exe')) {
    Copy-Item -LiteralPath (Join-Path $taskUpstream $taskEngine) -Destination $taskOutput
}
foreach ($taskDocument in @('README.md', 'README_EN.md', 'README_DIRECT.md', 'README_X_RELAY.md', 'direct-config.json')) {
    Copy-Item -LiteralPath (Join-Path $taskRoot $taskDocument) -Destination $taskOutput
}
Copy-Item -LiteralPath (Join-Path $taskRoot 'vendor\Cealing-Host-U.json') -Destination $taskOutput
Set-Content -LiteralPath (Join-Path $taskOutput 'Cealing-Host-L.json') -Value '[]' -Encoding utf8
Set-Content -LiteralPath (Join-Path $taskOutput 'nginx.conf') -Value '' -NoNewline -Encoding utf8
Set-Content -LiteralPath (Join-Path $taskOutput 'config.yaml') -Value '' -NoNewline -Encoding utf8
Remove-Item -LiteralPath (Join-Path $taskOutput 'Sheas_Cealer.pdb') -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $taskOutput 'RelayCheck.exe') -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $taskOutput 'RelayCheck.dll') -ErrorAction SilentlyContinue

$taskQuickStart = @'
Sheas Cealer Direct.4 —— 一键连接 + X 分片直连 + 线路诊断。X 连不上时会自动做对照握手，把“诊断”那行和复制的诊断信息发回来。

1. 将压缩包完整解压到一个新目录，不要覆盖正在运行的旧版。
2. 在旧版里先停止“全局伪造 / 全局净化”，再退出旧程序。
3. 关闭 Clash Verge 等梯子（包括 TUN 和系统代理）。
4. 右键 Sheas_Cealer.exe，以管理员身份运行；或直接打开后点“以管理员身份重新打开”。
5. 点击中间的大圆按钮。变绿后下方“连通性”卡片会自动检测 YouTube、X、GitHub。
6. 新开标签访问 https://x.com，检查页面、登录、图片和视频。
7. 不行就点“复制诊断信息”，把内容和 logs/relay.log、logs/error.log、截图一起发回来。
8. 用完点一下大按钮断开；直接关窗口也会先断开再退出。

YouTube 等沿用上一版已能播放的规则（Cealing-Host-U.json 未改动）。
X 的域名改走 127.0.0.2 上的分片中继，不再经过 nginx；原理、配置和日志含义见 README_X_RELAY.md。
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
