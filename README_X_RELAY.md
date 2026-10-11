# Direct.3：X 分片直连中继 + 一键连接界面

2026-10-11。基于 Direct.2 本地版本。**本版只做了离线构建、离线自检和打包，没有做任何实际网络验证**；X 能否打开必须由你在关闭梯子后自行确认。

## 为什么上一版的 X 一定打不开

1. Direct.2 的 `Cealing-Host-X.json` 把 x.com 指向 X 自家旧 IP `69.195.183.139`，twimg 指向 Fastly `146.75.72.157`，并按规则格式把空 SNI 替换成随机占位名。你的 `x-error.log` 显示：对这两个 IP 的 TLS 握手全部被对端关闭、重置或连接超时，`x-access.log` 全部 502/504 且 `connect=-`。
2. 通过 Google DoH 查询（只是 DNS 数据，与你的线路无关）：`x.com / api.x.com / upload.x.com / t.co / twitter.com` 现在都 CNAME 到 `dynamic.x.com.cdn.cloudflare.net`（Cloudflare，`162.159.140.229 / 172.66.0.227`）；`abs/pbs/video/ton.twimg.com` 在 Fastly；上游 Cealing-Host 项目已删除全部 X 规则。
3. Cloudflare 要求 SNI 与 Host 属于同一站点，不一致直接 403，不发 SNI 则握手失败。所以“本地 nginx 改 SNI”这条路对 Cloudflare 上的 x.com 行不通，换任何 IP 都一样。

## 这版怎么做

- 全局模式启动时，hosts 把 X 相关域名（见 `direct-config.json` 的 `RelayHosts`）指向 `127.0.0.2`，而不是 nginx 的 `127.0.0.1`。
- 程序内的 `SniRelay` 监听 `127.0.0.2:443`：读取浏览器发出的 ClientHello，从 SNI 取域名，按配置的候选 IP（Cloudflare / Fastly）直连，然后把这条 ClientHello 按 **TLS record 分片**写出去（`Direct/FragmentingStream.cs`，默认 `tls-record`，8 字节，间隔 8 ms），之后的字节原样双向转发。
- 浏览器和 Cloudflare 之间是端到端 TLS：真实证书、浏览器自己的指纹、原生 HTTP/2 与 WebSocket。X 不再经过 nginx，也不使用 Cealing 证书。
- nginx 继续负责 YouTube 等已有规则，监听改为 `127.0.0.1:443`，避免和中继冲突；80 端口仍监听全部回环地址，`http://t.co/...` 会被 301 到 https。
- 依据：upb-syssec 2023 年公开测量《Circumventing the GFW with TLS Record Fragmentation》——GFW 只做部分 TCP 重组，对 TLS record 分片无法识别，92% 的站点接受分片后的 ClientHello。这是 2023 年的结果，GFW 可能已变化。

## Direct.4 追加（2026-10-11 第一次实测后）

第一次实测结果：YouTube、GitHub 可用；X 全部失败，`relay.log` 里是三种形态混合：TCP 能连上但发出 ClientHello 后约 15 秒被对端关闭 (EOF)、约 20 秒 TCP 重传超时（数据没被确认）、40 秒毫无响应。这无法区分“专用 IP 被丢包”和“GFW 已能重组分片”。因此：

- 分片修正：原来 8 字节分片对 `x.com` 这种短域名根本没把域名拆开（整个 "x.com" 在第一个 record 里）。现在域名总是从中间拆开，并且域名前也单独断一次。
- 线路诊断：X 检测失败时自动对同一批 IP 做对照握手（无关 SNI vs x.com SNI、分片 vs 不分片、专用 IP vs 通用 anycast、Fastly 同理），结论显示在卡片下方，也写进 `relay.log`。
- 候选 IP 扩充：Cloudflare 通用 anycast、Fastly 同宿主位的其他子网；连上后又死掉的 IP 会被降级，让备选 IP 轮到。

诊断结论怎么读：

- “对照握手都失败” → 专用 IP 本身被封/丢包，与 SNI 无关；看通用 anycast 对照是否成功。
- “无关 SNI 正常、x.com 分片与否都失败” → GFW 已能重组 TLS record 分片，当前方法在该线路无效。
- “分片后握手成功” → 线路可行，问题在 HTTP 层或域名没列全。

## 其他修复

- nginx 运行配置改为单独的 `nginx-running.conf`，不再“写入 nginx.conf 又立即清空”。Direct.2 的 `error.log` 里 `[emerg] no "events" section` 与 worker 退出就是清空导致的（Windows 下 worker 会重读配置文件）。`nginx.conf` 仍是你的附加配置，“编辑 Nginx 配置”行为不变。
- 退出程序时若仍处于连接状态，会先断开并恢复 hosts / 证书，再退出。
- 停止时显式等待 hosts、证书清理完成，并删除 `nginx-running.conf`。

## 界面

- 主界面只有一个大圆形按钮：灰=未连接，琥珀=连接中，绿=已连接。点击即启动 / 停止全局模式（需要管理员权限；非管理员时下方有“以管理员身份重新打开”）。
- “连通性”卡片在连接成功后自动请求 `https://www.youtube.com/generate_204`、`https://x.com/`、`https://github.com/`，显示 HTTP 状态码和耗时；失败时附中继证据。可“重新检测”，“复制诊断信息”会把检测结果和 `logs/relay.log` 末尾复制到剪贴板。
- 旧版的全部按钮（浏览器伪造、全局净化、编辑规则 / 配置、更新上游规则、浏览器直连等）收在底部“更多功能”折叠区，功能不变。

## 配置（`direct-config.json`）

- `RelayListenAddress`：默认 `127.0.0.2`，必须是 127.0.0.1 以外的回环地址。
- `RelayHosts`：域名 → 候选 IP 数组。数组为空表示用 DoH 解析（DoH 服务器在大陆常常不可达，所以主要域名都内置了固定 IP）。hosts 不支持通配符，没列出的子域名仍会被 DNS 污染，需要时自行补充。
- `FragmentMode` / `FragmentBytes` / `FragmentDelayMs`：分片方式与参数，同时作用于中继和“浏览器直连”。
- 修改后需要断开再连接才生效。缺少的字段会使用默认值。

## 怎么判断结果、失败时发什么

连接后看“连通性”卡片和 `logs/relay.log`（只记录域名、IP、阶段、耗时，不记录 URL / Cookie）：

| relay.log 里的结果 | 含义 | 下一步 |
| --- | --- | --- |
| `收到 ServerHello` | TLS 握手已过 GFW，Cloudflare 回应了 | 页面若仍异常，看浏览器报错或补域名 |
| `握手后被对端重置 (RST)` / `关闭 (EOF)` | GFW 仍识别出了 SNI，或服务器拒绝 | 改 `FragmentMode` 为 `tcp`、调小 `FragmentBytes`、加大 `FragmentDelayMs` 再试；仍不行说明该线路上分片无效，只剩 ECH 或真正的代理 |
| `TCP 连接失败` | 候选 IP 本身被封或不可达 | 在 `RelayHosts` 增加其他 Cloudflare / Fastly IP |

反馈时点“复制诊断信息”粘贴即可，另附 `logs/relay.log`、`logs/error.log` 和截图。

## 已知边界

- 分片只能对付基于 SNI 的检测，不能对付 IP 封锁、协议特征识别或 Cloudflare 自身的验证页。
- 浏览器可能先向 `127.0.0.2:443` 尝试 QUIC（UDP 无人监听）再回退 TCP，首次打开可能略慢。
- Clash Verge 等的 TUN / 系统代理会接管流量，使用前必须关闭。
- 打开本程序时如果上一版的 nginx 仍在运行，请先在上一版停止并退出，否则 hosts 里是旧规则。

## 离线自检与重新构建

```powershell
dotnet run --project .\Verification\RelayCheck -- . 
dotnet build .\Sheas-Cealer.sln -c Release
.\Build-Package.ps1
```

`RelayCheck` 在进程内用回环地址验证：默认配置覆盖 X 域名、`direct-config.json` 与默认值一致、SNI 解析、三种分片模式字节原样、中继可绑定 127.0.0.2 并把分片后的 ClientHello 送到假服务器、不可达目标被正确记录、上游规则快照未改动。它不访问任何外部网络。

资料：

- [Circumventing the GFW with TLS Record Fragmentation (upb-syssec, 2023)](https://upb-syssec.github.io/blog/2023/record-fragmentation/)
- [Cloudflare 对 SNI / Host 不一致返回 403 的第三方测试](https://digi.ninja/blog/cloudflare_example.php)
- [Cealing-Host 规则格式文档](https://github.com/SpaceTimee/Cealing-Host/wiki/Cealing-Host-Documentation)
- [TLS 1.3 record 规则 (RFC 8446 §5.1)](https://www.rfc-editor.org/rfc/rfc8446.html#section-5.1)
