<h1 align="center">雾帆 MistSail</h1>
<h3 align="center">— 让握手藏进雾里 —</h3>
<p align="center">基于开源项目 <a href="https://github.com/SpaceTimee/Sheas-Cealer">Sheas Cealer 1.1.4</a> 的本地改版 · WPF / .NET 8 · Windows</p>

## 这是什么

HTTPS 加密了网页内容，但 TLS 握手第一个数据包 ClientHello 里的 SNI 仍是明文，链路上的中间设备可以据此识别、劫持或重置连接。

雾帆在本机接管握手，不使用任何境外代理服务器、VPN 或 TUN：

| 路线 | 适用 | 做法 |
|---|---|---|
| 本机 nginx 改写 SNI | 常规站点 | hosts 把域名指向 `127.0.0.1`，本机 nginx 用自签证书接管浏览器连接，再以改写后的 SNI 连向真实服务器 |
| 回环中继分片 | 拒绝改写 SNI 的 Cloudflare 类站点 | hosts 把域名指向 `127.0.0.2`，进程内 `SniRelay` 把首个 ClientHello 按 TLS record 切片后透传，浏览器与真实服务器仍是端到端 TLS |

主界面只有一个电源按钮，旧功能收进「更多功能」；连通性卡片、线路探测与「复制诊断信息」用于排查。

## 下载

到 [Releases](https://github.com/CuteEthanLeo/MistSail/releases) 下载 `MistSail-v*-win-x64.zip`，完整解压到新目录后右键 `MistSail.exe` 以管理员身份运行。压缩包为自包含 x64 版本，无需另装 .NET。

## 关于根证书，请如实了解

- 全局模式启动时会生成一枚**自签根证书**并安装到**当前用户**的受信任根证书存储，仅用于让浏览器信任本机 nginx。
- 出站到真实服务器仍是正常 TLS。回环分片中继这条路线不经过 nginx，也不解密任何流量。
- 停止后证书会被清理；也可以随时在 `certmgr.msc` 里手动删除。

## 构建

依赖：.NET 8 SDK、Windows 10 17763+。

1. 从官方 [Sheas Cealer 1.1.4](https://github.com/SpaceTimee/Sheas-Cealer/releases) 免安装压缩包解压到 `vendor/upstream-1.1.4/`（提供 `Cealing-Nginx.exe`、`Cealing-Mihomo.exe` 等二进制，本仓库不包含）。
2. `lib/` 内已附带官方发行包中的 `Sheas-Core.dll`、`Ona-Core.dll`，用于解决原始源码缺少两个兄弟项目导致无法构建的问题。
3. 运行：

```powershell
.\Build-Package.ps1
```

脚本会先执行 `Verification/RelayCheck` 离线自检（只走回环地址，不访问外网），再发布自包含 x64 包到 `artifacts/`。

## 配置

`direct-config.json` 集中了全部参数，常用项：

| 键 | 默认 | 说明 |
|---|---|---|
| `FragmentMode` | `tls-record` | 分片方式；`tcp` 为 TCP 写入分片；`off` 关闭 |
| `FragmentBytes` | 8 | 域名区域的分片字节数，域名总会被从中间切开 |
| `FragmentDelayMs` | 8 | 片与片之间的发送间隔 |
| `RelayListenAddress` | `127.0.0.2` | 分片中继监听的回环地址 |
| `RelayHosts` | … | 走分片中继的域名与候选 IP |
| `DohServers` | Cloudflare / Quad9 / Google | 加密 DNS，带引导 IP |

修改后停止并重新连接生效。更多说明见 [README_DIRECT.md](README_DIRECT.md) 与 [README_X_RELAY.md](README_X_RELAY.md)。

## 日志与诊断

- `logs/relay.log`：分片中继的每条连接结果（目标 IP、握手阶段、错误形态），不记录 URL、Cookie 或请求头。
- `logs/direct.log`：实验性浏览器直连模式的日志。
- 主界面「复制诊断信息」会把连通性结果与线路对照结论一起复制到剪贴板。

## 能力边界

加密 DNS 与握手处理只能改善 DNS 污染和部分握手识别导致的问题，**不改变路由，不能保证突破目标 IP 封锁、全部协议识别或站点自身的地区/账户限制**。这是客户端方案的边界。

## 注意事项与免责声明

1. 本项目及所有相关资源仅供**抵御网络非法监听**和**开展网络安全研究**使用，无意绕过任何国家审查设备的审查。
2. 使用前请阅读上游项目的[隐私政策](https://thoughts.teambition.com/share/6264eda98adeb10041b92fda#title=Sheas_Cealer_隐私政策)与[使用协议](https://thoughts.teambition.com/share/6264edd78adeb10041b92fdb#title=Sheas_Cealer_使用协议)，并遵守所在地法律法规。
3. 本改版处于实验阶段，不保证任何站点可用；请勿与其他代理、VPN 或 TUN 软件同时使用。

## 目录说明

| 路径 | 内容 |
|---|---|
| `Direct/` | 本改版新增：`SniRelay`、`FragmentingStream`、`DohResolver`、`DirectRelay`、`LineProbe` |
| `Wins/` `Consts/` `Convs/` `Preses/` `Proces/` `Utils/` `Props/` | 上游 WPF 源码及本改版的界面修改 |
| `Verification/RelayCheck/` | 离线自检程序 |
| `vendor/Cealing-Host-U.json` | 上游规则快照（文本） |
| `docs/upstream/` | 上游项目原始 README |
| `docs/video/` | 项目介绍视频的离线渲染脚本（Pillow + numpy + ffmpeg） |

## 致谢

- **Space Time** 的 [Sheas Cealer](https://github.com/SpaceTimee/Sheas-Cealer)、[Cealing Host](https://github.com/SpaceTimee/Cealing-Host)：本项目的全部基础。
- **kit** 与 **NiceBowl**：SNI 伪造原理与[说明文章](https://nicebowl.fun/24_8)。
- upb-syssec 2023 年关于 TLS record 分片的公开测量。

## 许可

上游仓库未附带 LICENSE 文件，上游代码版权归原作者所有。本仓库对上游的修改与新增代码（`Direct/`、界面与构建脚本）以与上游相同的条件提供；若上游作者另有要求，将随时调整。
