# 雾帆 MistSail · Direct 实验说明（基于 Sheas Cealer 1.1.4）

> Direct.3 起主界面只有一个“连接”大按钮（全局模式）；本文描述的“浏览器直连（实验）”按钮和“直连 / 分片配置”已移到底部“更多功能”折叠区。X 的处理方式见 README_X_RELAY.md。

构建日期：2026-10-11。基础版本：SpaceTimee/Sheas-Cealer 1.1.4。

**这是未经过实际网络验证的实验版本，不是“完整国际互联网解锁”的保证。**
按本次要求，仅修改源码、进行编译和打包；没有启动本程序、浏览器、本机转发器、Nginx 或 Mihomo，没有测试 YouTube、X、GitHub、DNS 连通性或出口 IP。

## 使用方法

1. 将压缩包全部解压到一个可写的新文件夹，勿覆盖正在运行的旧版。
2. 关闭现有梯子，包括 VPN/TUN、系统代理和代理软件后台路由；先在旧版停止“全局伪造”和“全局净化”，再退出旧版。
3. 双击 `MistSail.exe`，普通用户权限即可。自包含 x64 包附带 .NET 8 运行时。
4. 浏览器路径选择当前已安装的 Edge、Chrome 或 Brave。
5. 点击下方新增的 **浏览器直连（实验）**。保持这个程序窗口打开，在新打开的独立浏览器窗口内访问站点。
6. 查看 YouTube 视频实际能否播放、拖动进度；检查 X 的图片/视频，以及 GitHub 的页面、raw 和下载。页面封面或 TCP 连接成功不能代表完整功能成功。
7. 结束时关闭独立浏览器，点击“停止直连”或退出程序。

## 真实出口 IP 与本机代理

此模式在 `127.0.0.1:17881` 上运行 HTTP CONNECT 本机转发器。技术上它属于**本机代理**；它没有境外代理服务器、远程节点或 VPN，实际 TCP 连接直接从本机发往目标站点。关闭其他梯子及其 TUN 路由后，网站通常看到宽带连接的公共出口 IP（家庭路由器/运营商 NAT 后的 IP），本版本没有进行出口 IP 验证。

如果你的要求包含“连本机转发器也完全不能用”，新增模式不满足这一条；原有 SNI 模式仍然保留，但不能保证修复动态视频域名或所有受限网站。

仅新打开的浏览器使用此模式。**不会自动让 Git 命令、桌面客户端、UDP 或整台电脑的所有程序直连。** 浏览器的 WebRTC/UDP 等流量也不属于本机 HTTP 转发器的覆盖范围。

## 代码修改

- 新增 `Direct/DirectRelay.cs`：仅监听 IPv4 loopback，直接连接目标 IP；HTTPS 采用 CONNECT 字节转发，HTTP 采用单连接单请求转发，支持长连接视频数据流和取消清理。
- 新增 `Direct/DohResolver.cs`：RFC 8484 DoH 二进制查询，验证响应问题及 DNS 记录；根据 CNAME 关系筛选真实地址、遵守短期 TTL，合并并发解析请求。并行访问配置的 DNS 服务，使用首个成功答案；DNS 服务也用指定 IP 引导及正常 HTTPS 证书校验，不回退系统 DNS。
- 新增 `Direct/FragmentingStream.cs`：仅首个 TLS ClientHello 的 SNI 区域分段；可选 TLS record、TCP 写入分段或关闭。分段不修改握手消息、应用数据或视频内容。跨多个原始 TLS record 的 ClientHello 保持原样。
- 新模式保留浏览器正常证书校验，不使用原模式的 `--ignore-certificate-errors`，不安装根证书，不编辑 hosts，不设置系统代理，不建立 TUN。
- 使用独立浏览器资料目录 `%LOCALAPPDATA%\MistSail\BrowserProfiles`，不会强制结束日常浏览器进程，不复用旧的附加参数。禁用 QUIC，使 HTTPS 使用 TCP 路径。
- 原有规则加载改为等待完成后启动，并发布完整规则快照；修复文件名解析及创建、删除、重命名规则时的重新加载。
- 原有静态规则中空目标不再自动替换成 `127.0.0.1`。上游 `*.googlevideo.com` 的空目标会被跳过；新模式逐个解析实际的视频 CDN 主机名。
- 打开主界面不自动进行网络更新或清理 hosts/证书。上游更新由按钮触发；“关于项目”仍保留原来的版本检查行为。
- 固定 NuGet 包版本，随源码提供官方 1.1.4 发行包中的 `Sheas-Core.dll` 和 `Ona-Core.dll`，解决原始压缩源码缺少两个兄弟项目导致无法构建的问题。

## 配置与失败反馈

点击“直连配置”编辑 `direct-config.json`。编辑后停止并重新启动直连，使新配置生效。

- `FragmentMode`：默认 `tls-record`；兼容性问题可以改成 `tcp`；`off` 只做加密 DNS 和真实 TCP 直连。
- `FragmentBytes`：默认 8，范围 1～1024；`FragmentDelayMs`：默认 8，范围 0～100。分段只给握手增加延迟，后续视频数据连续转发。
- `FragmentDomains`：默认 `["*"]`，尝试处理全部 TLS 域名；也可限定 `youtube.com`、`googlevideo.com`、`x.com`、`twimg.com`、`github.com`、`githubusercontent.com`、`githubassets.com` 及三个 DoH 服务域名。
- `EnableIPv6`：默认关闭；只有确认本机 IPv6 网络可用时再开启。本次没有检查 IPv6。
- `HostOverrides`：可以为**确切域名**指定经过你验证的真实 IP 数组。默认留空，不臆造可用地址，不改变证书校验。
- `DohServers`：默认 Cloudflare、Quad9、Google。配置各项均使用 HTTPS 443 及显式引导 IP；部分大陆网络可能封锁这些 IP，届时会解析失败。DoH 服务提供方可以看到你的 DNS 查询。
- `ListenPort`：默认 17881。如果本机端口已占用，启动会报错；可修改此值。不同端口使用不同独立浏览器资料目录。

失败时请提供包内 `logs/direct.log` 及浏览器报错截图。日志记录目标域名、目标 IP、连接和错误阶段，**不记录完整 URL、Cookie、请求头或视频内容**；它仍然包含浏览域名。2 MB 后轮转为 `direct.log.previous`。

“TCP 已连接”只代表 TCP 建立；“收到目标响应”可能是 TLS 握手或错误响应，不能证明站点或视频功能可用。若日志显示所有 DoH 失败、所有目标 IP 连接失败、连接被重置，需要继续根据关闭梯子后的实际证据分析。

## 能力边界

加密 DNS 和握手分段可能改善 DNS 污染及部分握手识别导致的问题，但**不会改变网络路由，也不能保证突破目标 IP 封锁、所有协议识别、服务端地区/账户限制或所有网站的策略**。这是客户端方案的边界，不能靠添加域名规则消除。

原有“启动伪造 / 全局伪造 / 全局净化”功能保留，仍具有原有的证书、hosts 和 TUN 行为；不要与新增模式同时使用。新模式也不能确保企业浏览器强制代理策略、浏览器扩展或第三方安全软件不会影响请求。

## 重新构建

```powershell
dotnet build .\MistSail.sln -c Release
dotnet publish .\MistSail.csproj -c Release -r win-x64 --self-contained true -o .\artifacts\MistSail-win-x64
```

完整压缩包用 `Build-Package.ps1` 生成。构建脚本只恢复依赖、编译和打包，不启动任何网络功能或做连通性测试。

## 原始项目和协议资料

- 上游源码与发行包：https://github.com/SpaceTimee/Sheas-Cealer/releases/tag/1.1.4
- 上游规则快照：https://github.com/SpaceTimee/Cealing-Host/blob/main/Cealing-Host.json
- DoH：https://www.rfc-editor.org/rfc/rfc8484.html
- TLS 1.3 record 规则：https://www.rfc-editor.org/rfc/rfc8446.html#section-5.1
- Chromium 代理原理：https://chromium.googlesource.com/chromium/src/+/HEAD/net/docs/proxy.md

本版本是本地修改版本，原始作者和项目归属保持不变。
