# Direct.3–8：X 分片直连中继 + 一键连接界面

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

## Direct.5 追加（2026-10-15，第三次实测后）

10‑13 与 10‑15 两次 `relay.log`（Clash 关闭时采集）把失败拆成三件事：

1. **专用 IP 是整段丢包。** `162.159.140.229`、`172.66.0.227`（x.com）和 `151.101.76.159`（twimg）能完成 TCP 握手，但之后任何 ClientHello——哪怕 SNI 是 `www.cloudflare.com`、不分片——都没有回应，服务器约 15 s 后 FIN。这与 SNI 无关，换分片没用；而它们排在候选列表第一位，中继每条连接都先在上面耗 15–40 s，浏览器自己先超时。
2. **通用 anycast 可达，但现有分片布局被识破。** `104.16.123.96` 对无关 SNI 握手正常，x.com 的 SNI 用 Direct.4 的 4 条 record 分片在 20–60 ms 内就收到 RST，比到 Cloudflare 的 RTT 还快，是注入的。公开研究到 2024–2025 年仍称 GFW 不重组 TLS record（gfw.report S&P ’25、Paderborn 2025），Xray 维护者 2025‑02 怀疑 GFW 已能识别“首个 TCP 包是完整 record 却不是完整 ClientHello”这一特征。本机无法复测，所以本版不再押注某一种发法。
3. **旧诊断结论误导。** 只要专用 IP 对照失败就说“换 IP 即可”，把“通用 IP + 分片也被 RST”这条证据吞掉了，而且探测器用的是 SChannel 的 300 字节 ClientHello，与 Chrome 1.75 KB 的布局不同。

这版的做法：

- **ClientHello 发法变成可枚举的策略**（`Direct/HelloShaper.cs`），只改 record 边界、TCP 写边界和时序，握手字节永远不动：

  | 名称 | 发法 | 针对的假设 |
  | --- | --- | --- |
  | `record-misalign`（默认） | 4 条 record，但每次 TCP 写截在下一条 record 头部第 2 字节 | 首包不再是完整 record |
  | `record-misalign-b` | 同上，首次写截在第一条 record 体内 | 同上，另一种触发 |
  | `record-2` | 两条 record，域名中间切开，一次写出 | 单段内不重组 record |
  | `tcp-mid` | 单条 record，TCP 在域名中间切开 | 预期失败，用来确认 TCP 重组 |
  | `record-bytes` | 域名每字节一条 record，写边界错开 | 更细粒度 |
  | `record-mid-delay` / `tcp-mid-delay` | 两段之间停 1.2 s | 探重组缓存超时 |
  | `record-zero4` | 前置 4 条空 record（只对 Cloudflare 试；RFC 8446 禁止，Fastly 可能拒绝） | 扰乱解析 |
  | `record-mid` | Direct.4 的发法（复现用） | — |
  | `plain` | 原样（对照，放最后：GFW 对同一 IP 有 1–3 分钟残留封锁） | — |

- **线路诊断变成查找器**（`Direct/LineProbe.cs`）：先并行找仍有响应的边缘 IP（无关 SNI；只要回任何 TLS record 就算可达，无响应/EOF 才算死），再在可达 IP 之间交替、按上表逐个试 x.com 的 SNI，拿到 ServerHello 后换一个 IP、换一种布局的 ClientHello 再确认一次才算胜者；twimg 在 Fastly 上同样处理；本机有公网 IPv6 时再试 Cloudflare 的 IPv6 边缘（先明文）。探测用的 ClientHello 优先重放浏览器刚发过的那条，否则用 Chrome 形态的合成包（`Direct/ClientHelloBuilder.cs`）。检测到 Clash/Mihomo 等 TUN 网卡时直接报“本轮无效”。
- **中继单连接内自动切换**（`Direct/SniRelay.cs`）：ClientHello 已缓存、浏览器还没收到任何字节，所以目标 RST/EOF/4 s 无响应时直接换下一个 IP，第三次换备用策略，最多 3 次、24 s。死 IP（无响应）与“某 IP 上某策略被 RST”分开记账：前者 12 h 内不再参与竞速并持久化，后者只在 3 分钟内降权；候选顺序打平时随机，不再回到配置顺序。
- **结果持久化** `direct-state.json`（与 `direct-config.json` 并列）：胜出策略、各域名组策略、确认可用的 IP、死 IP；换网络或 12 小时后作废。查找器永远不改 `direct-config.json`。
- 默认候选顺序改为通用 anycast 在前、专用 IP 在后，并加入 Cloudflare IPv6 边缘（仅在本机有公网 IPv6 时使用）。
- 新配置项：`FragmentMode` 默认 `auto`（用诊断结果；填具体策略名则固定，旧值 `tls-record` / `tcp` / `off` 仍可用）、`HandshakeTimeoutSeconds`（默认 4）、`FragmentDelayMs` 默认 15（Windows 会把连续 send 合并，低于 15 ms 分不开段）。

诊断结论怎么读（卡片下方与 `relay.log` 的“诊断结论”）：

- “已找到可用方案：…” → 中继已切换并自动重测 X；若页面仍不开，看浏览器报错。
- “所有 Cloudflare 候选 IP 发出 ClientHello 后都无响应” → IP 级丢包，在 `RelayHosts` 补其他 Cloudflare IP。
- “通用 IP 可达……N 种布局都被 RST” → 该线路的 GFW 已能重组 record 与 TCP 分段，用户态分片对它无效；剩下只有内核级假包（WinDivert，zapret/GoodbyeDPI 路线）或 ECH，本版不做。
- “检测到 TUN/代理网卡” → 关掉 Clash 等的 TUN 后再点“线路诊断”。

## Direct.6 追加（2026-10-15，转内核级 WinDivert desync）

Direct.5 的实测证实：这条线路的 GFW 已完整重组 TCP 分段与 TLS record，17 种用户态分片/错位/延迟布局全部在快于 RTT 的时间里被 RST，专用 IP 与残留封锁也已确认。用户态改 ClientHello 这条路到头了，转内核级。

做法沿用“可枚举策略 + 寻路器”的骨架，只是把改写从用户态 socket 移到网线层：

- **中继改发明文**：装填了 desync 策略后，`SniRelay` 把 ClientHello 当作一条普通 record 一次发出（`ForcePlain`），不再在用户态分片；选 IP / 竞速 / 重试逻辑不变。
- **WinDivert 在网线层改写**（`Direct/WinDivertDesync.cs` + `Direct/PacketDesync.cs`）：只拦截发往 X 的那几个 Cloudflare/Fastly IPv4 边缘的 443 出站 ClientHello，注入一个**真实服务器会丢弃、但 GFW 会收进重组缓冲区**的假包，再把真实段发出，使 GFW 与真实 TCP 栈重组出的流不一致，GFW 看到的是无害 SNI（`www.bing.com`）。服务器最终重组到的握手字节永远是原始 ClientHello 的精确拷贝，transcript 哈希不变。
- **假包怎么让服务器丢弃**：主用低 IP TTL——假包只够到 GFW，到服务器前 TTL 耗尽；辅以坏 TCP 校验和（GFW 可能不校验、服务器会丢）。TTL 到 GFW 的跳数因线路而异，所以把 `方法 × fooling × TTL × 切点` 做成可枚举策略（`Direct/DesyncStrategy.cs`），由寻路器逐个试、拿 ServerHello 两次才算胜出。
- **autottl**：读入站 SYN-ACK 的 IP TTL 估算到服务器的跳数，把假包 TTL 上限压在它之下，既加快搜索，也保证假包永远到不了服务器。

  | 方法 | 发法 | 针对的假设 |
  | --- | --- | --- |
  | `fake-ttlN` | 低 TTL 假 ClientHello + 真实 ClientHello（同 seq） | GFW 对同 seq 重叠段“保留先到” |
  | `fakedsplit-ttlN` | 低 TTL 假包 + 真实头 + 真实尾 | 假包占住头部，真实被切开 |
  | `fakeddisorder-ttlN` | 低 TTL 假包 + 真实尾 + 真实头（乱序） | 叠加乱序扰乱重组 |
  | `fake-badsum` / `fakeddisorder-badsum` | 坏校验和替代低 TTL | TTL 不依赖、但可能被网卡 offload 修正 |
  | `disorder` | 真实尾先发、头后发（无假包，对照） | 纯乱序能否奏效 |

- **仍走寻路器**：用户态几种布局先试（线路若本来就吃用户态分片，仍用用户态，不加载内核改写）；都被 RST 后才转入内核 desync 枚举。胜出的 desync 策略持久化到 `direct-state.json` 的 `Desync`，换网络或 12 小时后作废。
- **作用范围极窄**：WinDivert 过滤器只匹配 X 用的那几个 Cloudflare/Fastly IPv4 边缘的 443，机器上其余流量一概不碰；其它包原样重新注入。本版只做 IPv4。
- **驱动**：随发行包附带 `WinDivert.dll` + `WinDivert64.sys`（WinDivert 2.2.2，LGPLv3，`vendor/windivert/`，作者 EV 签名）。程序以管理员运行时首次调用自动加载驱动；加载失败则回退到用户态分片并在 `relay.log` 记一行。
- **新配置项** `DesyncMode`：默认 `auto`（有 WinDivert 就加载，诊断自动决定用哪种）；`off` 关闭内核路径、不加载驱动；填具体策略名则固定。

诊断结论补充：

- “已找到内核级 desync 方案：…” → 引擎已装填该策略、中继改发明文，重开 x.com。
- “已尝试内核级 desync 但各策略仍未拿到 ServerHello” → 复制诊断信息反馈，或在 `RelayHosts` 补其他边缘 IP；也可能该线路对假包也做了校验（需要 autottl 更准或换 fooling）。
- `relay.log` 里的 `desync host=… 注入 N 包` 表示某条连接已被改写。

**本版同样没有在真实线路上验证过**，packet 数学由离线自检覆盖（真实段按序重组 == 原始 ClientHello、假包携带无害 SNI、TTL/校验和如预期），但 desync 能否过这条线路的 GFW，必须由你关掉梯子后实测；不行就把“诊断”那行和 `relay.log` 发回。

## Direct.7 修复（2026-10-24，对应 issue #1 / #2）

针对 [issue #1](https://github.com/CuteEthanLeo/MistSail/issues/1) 与 [issue #2](https://github.com/CuteEthanLeo/MistSail/issues/2)：

1. **#1 不再把一条连接卡在已被封锁的专用 IP 上。** 单条浏览器连接的总候选预算固定为 15 s（每次握手仍默认 4 s）；静默目标会被记为不可达（查找器确认后写入 `direct-state.json`），下一次连接不会再先试它。
2. **#1“如何更新/切换 IP”改为自动。** 候选 IP 全部失败后，中继用 DoH 刷新该域名的候选（`RefreshCandidatesFromDoh`），并合并进当前进程的候选池；诊断开始前也会先刷新一次。连续 3 条 X 连接失败会自动跑线路查找器（3 分钟防抖），找到胜者后热切换并持久化到 `direct-state.json`。
3. **#1 诊断文案修正。** 控制 SNI 可达、x.com 的 SNI 各种布局都被 RST 时，明确写“与 IP 无关，换 IP 无效”，指向 WinDivert/ECH；只有 TLS alert（IP 不服务该域名）才建议换边缘 IP。
4. **#1 `google.com.hk` 缺失已补。** 新增本地规则 `Cealing-Host-L.json`（`*google.com.hk` / `accounts.google.com.hk` → SNI `g.cn`、IP `183.56.143.147`），与哈希保护的上游快照分离；打包时复制该文件。
5. **#2 YouTube 视频动态域名进入握手处理。** hosts 无法写 `*.googlevideo.com` 通配，nginx 也不能分片，所以连接期间设置当前用户的 PAC：只把 `DynamicProxyHosts`（默认 `googlevideo.com`、`youtubei.googleapis.com`）交给 `127.0.0.1` 的回环 CONNECT 转发器，经 DoH 解析与 ClientHello 分片后直连；页面其它域名仍走 hosts + nginx。转发器使用临时端口，PAC 也只监听回环。断开、退出、nginx 消失或下次启动都会恢复原 `AutoConfigURL`；检测到已有公司 PAC / 固定代理时默认不覆盖（`DynamicProxyOverrideExisting`）。
6. **残留修复。** `Repair-Remnants.ps1` 增加 PAC 恢复；`NginxCleaner` 在停止/退出/启动自检时同时恢复 hosts、证书与 PAC。

## Direct.8（2026-10-24，修复长时间运行闪退）

2026-10-24 10:31–10:34 的三次崩溃已定位：Windows 事件日志 1026 / .NET Runtime 显示
`System.OverflowException`，栈为 `WinDivertDesync.LearnTtl` ← `Handle` ← `RecvLoop`。
收到首个来自 162.x/172.x 边缘的 SYN-ACK 时，把首字节左移 24 位再转 `uint`，在项目的
checked 构建下溢出；原始后台线程没有兜底，直接终止进程，hosts/证书/PAC 因此留在系统里。

修复：

- `LearnTtl` / `ToKey` 改用 `BinaryPrimitives.ReadUInt32BigEndian`，消除 checked 溢出。
- `RecvLoop` 的单包处理与接收循环全捕获：异常只写日志并重新注入原包，不再杀进程；引擎异常停止时中继回退用户态分片。
- `AppDomain.UnhandledException` / 未观察任务异常写入 `logs/crash.log`，并做一次 hosts/证书/PAC 清理，避免“程序退出但效果还在”。
- 新增离线回归：高位首字节 IP 的 TTL 学习不再溢出。

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
- `FragmentMode` / `FragmentBytes` / `FragmentDelayMs`：用户态分片方式与参数，同时作用于中继和“浏览器直连”。默认 `auto`，由诊断决定。
- `HandshakeTimeoutSeconds`：单个候选发出 ClientHello 后的等待上限，默认 4；单连接总预算固定为 15 s。
- `RefreshCandidatesFromDoh`：候选全部失败后是否用 DoH 刷新该域名的候选，默认 `true`。
- `DynamicProxyEnabled` / `DynamicProxyHosts` / `DynamicProxyOverrideExisting`：动态视频 PAC 的开关、域名后缀（写域名即可，自动匹配子域）与已有代理覆盖策略。
- `DesyncMode`：内核级 WinDivert 假包。默认 `auto`（有驱动就加载，诊断自动决定用哪种 desync 策略）；`off` 完全关闭、不加载驱动；填具体策略名（如 `fake-ttl5`、`fakeddisorder-ttl4`）则固定。需要管理员权限和同目录的 `WinDivert.dll` / `WinDivert64.sys`。
- 修改后需要断开再连接才生效。缺少的字段会使用默认值。

## 怎么判断结果、失败时发什么

连接后看“连通性”卡片和 `logs/relay.log`（只记录域名、IP、阶段、耗时，不记录 URL / Cookie）：

| relay.log 里的结果 | 含义 | 下一步 |
| --- | --- | --- |
| `收到 ServerHello` | TLS 握手已过 GFW，Cloudflare 回应了 | 页面若仍异常，看浏览器报错或补域名 |
| `握手后被对端重置 (RST)` / `关闭 (EOF)` | GFW 仍识别出了 SNI，或服务器拒绝 | 若 `DesyncMode=auto` 诊断会自动转内核级 desync；都失败则只剩 ECH 或真正的代理 |
| `desync host=… 注入 N 包` | 该连接已被 WinDivert 改写 | 配合上一行看是否随后 `收到 ServerHello` |
| `desync 引擎未启动` | 没有 WinDivert 驱动或非管理员 | 确认同目录有 `WinDivert.dll`/`WinDivert64.sys` 且以管理员运行；或设 `DesyncMode=off` 退回用户态 |
| `TCP 连接失败` | 候选 IP 本身被封或不可达 | 在 `RelayHosts` 增加其他 Cloudflare / Fastly IP |
| `候选 IP 已刷新 host +N` | 中继在失败后用 DoH 找到了新候选 | 下一次连接会优先尝试新 IP；通常不需要手动改配置 |
| `动态视频代理已启用` / `[video] ...` | PAC 已把视频域名交给本机 CONNECT 转发器 | 若视频仍失败，看后面的 `[video]` 连接结果 |
| `动态视频 PAC 未启用：已存在...` | 已有公司 PAC / 固定代理，未覆盖 | 关闭已有代理，或把 `DynamicProxyOverrideExisting` 设为 true |

反馈时点“复制诊断信息”粘贴即可，另附 `logs/relay.log`、`logs/error.log` 和截图。

## 已知边界

- 分片 / desync 只能对付基于 SNI 的检测，不能对付 IP 封锁、协议特征识别或 Cloudflare 自身的验证页。
- 内核级 desync 只做 IPv4，且只改写 X 用的那几个 Cloudflare/Fastly 边缘 IP；坏校验和假包可能被网卡 checksum offload 修正而失效，TTL 类策略更可靠。
- 浏览器可能先向 `127.0.0.2:443` 尝试 QUIC（UDP 无人监听）再回退 TCP，首次打开可能略慢。
- Clash Verge 等的 TUN / 系统代理会接管流量，使用前必须关闭。
- 动态视频 PAC 只影响读取系统自动配置脚本的程序（Chrome/Edge 等）；Firefox 请用“浏览器直连（实验）”。PAC 在连接期间只监听 127.0.0.1，断开/退出时恢复；异常强杀后重启 MistSail 或运行 `Repair-Remnants.ps1`。
- 若线路已经能重组 TLS record / TCP 分段，视频走本机转发器仍会失败；这属于线路能力边界，不是 PAC 端口问题。
- 打开本程序时如果上一版的 nginx 仍在运行，请先在上一版停止并退出，否则 hosts 里是旧规则。

## 离线自检与重新构建

```powershell
dotnet run --project .\Verification\RelayCheck -- . 
dotnet build .\MistSail.sln -c Release
.\Build-Package.ps1
```

`RelayCheck` 在进程内用回环地址验证：默认配置覆盖 X 域名、`direct-config.json` 与默认值一致、SNI 解析、全部 `HelloStrategy` 字节原样、中继可绑定 127.0.0.2 并把分片后的 ClientHello 送到假服务器、静默候选在 3 次尝试内快速关闭、不可达目标被正确记录、PAC 脚本生成与回环 HTTP、本地 `Cealing-Host-L.json`、上游规则快照未改动、desync 包数学。它不访问任何外部网络。

资料：

- [Circumventing the GFW with TLS Record Fragmentation (upb-syssec, 2023)](https://upb-syssec.github.io/blog/2023/record-fragmentation/)
- [Cloudflare 对 SNI / Host 不一致返回 403 的第三方测试](https://digi.ninja/blog/cloudflare_example.php)
- [Cealing-Host 规则格式文档](https://github.com/SpaceTimee/Cealing-Host/wiki/Cealing-Host-Documentation)
- [TLS 1.3 record 规则 (RFC 8446 §5.1)](https://www.rfc-editor.org/rfc/rfc8446.html#section-5.1)
