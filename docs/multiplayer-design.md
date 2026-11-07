# Fuse Launcher 联机功能设计文档（v1 草案）

> 状态：待评审（未提交仓库）
> 作者：liumo · 2026-11-06
> 依据：全网调研（e4mc / World Host / Essential / 樱花Frp / playit.gg / 三大启动器联机公告），调研结论见本文末尾「附录 A」

## 1. 目标与范围

**目标**：让 Fuse Launcher 用户"零门槛、免费、低延迟"地和联机朋友玩 Java 版 Minecraft。

**非目标（本期不做）**
- 不做账号体系、好友系统、大厅匹配（那是平台级产品，成本不对等）
- 不做基岩版互通（Geyser 属远期彩蛋）
- 不自营公共服务器（个人项目不盈利，无带宽预算；P3 只留接口）

**设计原则（吸取 BakaXL/PCL2/HMCL 2022 年联机功能下架的教训）**
1. **通道可插拔**：直连 / 中转 / 联机 mod 三条通道互为兜底，启动器不深度绑定任何付费第三方
2. **默认零成本**：优先 UPnP / IPv6 直连，失败才引导中转
3. **邀请即所得**：房主生成邀请码，好友点一下就进服，全程不需要懂"端口/防火墙/穿透"
4. **随开随关**：只在世界开放期间暴露端口，退出自动清理

## 2. 用户故事

**房主（开房间）**
- 用 Fuse 启动任意实例 → 点「联机 → 开放世界」→ 在游戏内正常"对局域网开放"
- 启动器自动：抓到 LAN 端口 → 尝试 UPnP 映射 / 检测 IPv6 → 放行防火墙 → 生成邀请码
- 点「复制邀请」发到 QQ/微信群

**好友（进房间）**
- 电脑装了 Fuse：点邀请链接 → Fuse 弹确认框（房间名/版本/延迟）→ 自动写入服务器列表 → 自动启动对应版本进服
- 没装 Fuse：拿到纯文本 `地址:端口`，手动添加（兜底）

## 3. 总体架构

```
┌─ Fuse.App（WPF net48）────────────────────────────┐
│  MultiplayerPanel（联机面板）                       │
│  ├─ OpenWorldFlow   开放世界流程（状态机）          │
│  ├─ InviteCodec     邀请码生成/解析                 │
│  ├─ LanPortWatcher  监听 latest.log 抓端口/中转地址 │
│  ├─ UpnpMapper      NATUPnP COM 端口映射           │
│  ├─ FirewallHelper  netsh 入站规则                 │
│  ├─ Ipv6Probe       全局 IPv6 地址枚举             │
│  └─ DeepLinkServer  fuse-launcher:// 协议接收      │
├─ Fuse.Core（netstandard2.0）──────────────────────┤
│  ├─ Multiplayer/LanPortWatcher.cs                  │
│  ├─ Multiplayer/InviteCodec.cs                     │
│  ├─ Multiplayer/ServerListPing.cs（SLP 协议）      │
│  ├─ Multiplayer/UpnpMapper.cs                      │
│  └─ Multiplayer/TunnelRunner.cs（P2: frpc 托管）   │
└─ 既有设施：PlayTracker（进程退出钩子）/ PingProbe /
   ModInstaller（装 e4mc、简单联机）/ ServersDat 读写 /
   quickPlayMultiplayer 自动加入 / LaunchState 防重复启动
```

## 4. P0「邀请码联机」详细设计

### 4.1 LAN 端口捕获（LanPortWatcher）

- 触发：用户点「开放世界」→ 启动游戏 → Fuse 开始 `FileShare.ReadWrite` 轮询该实例 `logs/latest.log` 尾部（复用 CrashAnalyzer/LaunchFx 的读法）
- 匹配正则（按版本逐个验证后固化，先都留着）：
  - `Listening on /0\.0\.0\.0:(\d+)`（1.16+）
  - `Started serving on /(\d+\.\d+\.\d+\.\d+):(\d+)`
  - `Starting to serve .* on port (\d+)`
- 超时 60s 未捕获 → 面板给「游戏里开 LAN 了吗？」引导 + 手动输入端口输入框兜底
- 抓到端口后自动进入「通道探测」状态机

### 4.2 通道探测（按优先级自动降级）

1. **UPnP**：`NATUPnP 1.0 Type Library` COM（net48 直接 COMReference，零外部依赖）
   - `StaticPortMappingCollection.Add(外部随机端口, "TCP", 内部端口, 本机内网IP, true, "Fuse Launcher")`
   - 成功 → 外部地址 = 公网IP:外部端口；**公网 IP 用外部探测**（访问 `https://api.ipify.org` 等双源比对，防内网 IP 当公网地址发出去）
   - 失败（COM 返回 null / 双层 NAT——国内光猫+路由器叠加很常见）→ 降级 2
   - 映射生命周期：世界关闭（PlayTracker Exited 或 LanPortWatcher 检测端口不再监听）→ `Remove()` + 面板状态复位
2. **IPv6 直连**：枚举网卡全局 IPv6（跳过临时地址可选），给出 `[xxxx::xx]:端口` 形式地址 + 路由器防火墙引导文案
   - 面板诚实标注「你的网络支持 IPv6 直连，但朋友的网络也得支持」
3. **仅局域网**：都没成功 → 邀请码仍然生成（同一 WiFi 下可用），并引导 P1/P2 通道

### 4.3 邀请码（InviteCodec）

```
fuse-launcher://join?addr=203.0.113.7:51347&name=小明的房间&mc=1.20.1&v=1
```
- `addr`：IPv6 地址带方括号转义
- 同时提供纯文本 `203.0.113.7:51347`（发给没装 Fuse 的朋友）
- 协议注册：`HKCU\Software\Classes\fuse-launcher\shell\open\command`（安装器/首次运行写入）
- **运行中接收**：现有单实例是互斥体，需加 Named Pipe IPC：第二个实例启动时把 URL 通过管道发给主实例后退出（管道名含版本号）
- 好友端解析 → 确认框（房间名 / SLP ping 的延迟与在线人数 / 当前账号类型）→ 写入 `servers.dat`（复用 ServersDat.Write）→ 询问用哪个版本启动 → `AppLauncher.LaunchAsync(versionId, _, serverAddress)`

### 4.4 房间卡（ServerListPing 升级）

- 在 PingProbe 的 TCP RTT 之上实现完整 SLP：握手 + status 请求 → 房间名 MOTD / 版本 / 在线人数 / favicon
- 修改页「联机」卡片实时显示自己房间的状态；好友端确认框也用它预检「服务器还开着吗」
- 协议包很小（~120 行），纯 .NET TcpClient 实现，netstandard2.0 可测

### 4.5 UI（联机面板）

- 入口：版本详情页修改页新增「联机」卡片（在「备份与还原」上方）：
  ```
  ┌ 联机 ────────────────────────────────┐
  │  ● 未开放                            │
  │  [开放世界]  [加入房间]              │
  └──────────────────────────────────────┘
  ```
- 「开放世界」点击后 → 启动游戏 → 卡片变状态机：等待开 LAN → 已捕获端口 → 通道探测中 → 已开放（显示地址、复制邀请按钮、在线人数）
- 「加入房间」：输入框/粘贴邀请码 + 本机服务器列表（复用本地页「服务器」chip 数据）
- 大圆花瓣不新增（已满 4 个），联机从详情页进

### 4.6 防火墙

- javaw 首次监听 Windows 会弹授权（专用网络通常没问题）；公用网络配置会拦外网入站
- `FirewallHelper`：探测是否已有 javaw 入站规则，没有则经提权执行 `netsh advfirewall firewall add rule dir=in action=allow program=<javaw> protocol=TCP`（复用 ElevationStatus 的 runas 流程；失败不阻断，仅提示）

## 5. P1「一键联机 Mod」详细设计

| Mod | 用途 | 加载器支持 | 来源 |
|---|---|---|---|
| e4mc | 主机开世界 → 中转发 `xxx.e4mc.link` 进聊天 | Fabric（Architectury 多加载器，接入前复核） | Modrinth `qANg5Jrr` |
| World Host | 好友系统/直连/代理三模式 | Fabric（复核 Forge 构建） | Modrinth `2AIZDfYo` |
| 简单联机 Lan Server Properties | LAN 界面加 `online-mode=false` 开关，离线好友可进 | Fabric + Forge（版本覆盖复核） | Modrinth/CurseForge |

- 一键安装复用 `ModInstaller.PlanAsync`（含依赖规划、jar 级 MC 兼容判断）——这层已经全通
- **地址捕获**：LanPortWatcher 扩展匹配 e4mc/World Host 的日志/聊天行（接入时实测正则；抓不到就走「手动粘贴兜底」——面板提供粘贴框，用户从游戏聊天栏复制地址）
- 判定逻辑：房主与好友都无公网通道 → 引导装 e4mc；账号是离线且好友也离线 → 引导装简单联机
- 中转延迟提示：e4mc/World Host 公共中转多在海外，面板明说「延迟可能 150ms+，国内朋友建议 P2 通道」

## 5.5 fuse-link：自研伪局域网隧道（P2P 通道，旗舰）

> **实现状态（2026-11-06）**：已落地并回环实测通过。信令最终**不走 OSS 桶**，改为
> **邀请码带候选 + 好友回执码人工交换**（QQ/微信发一下即可），启动器始终不部署/不运营任何服务器：
> - 邀请码 `fl=` 参数携带房主候选（STUN 映射 + IPv6 + 内网），好友解析后生成"回执码"（自己的候选）
> - 房主把回执码粘进联机卡片 → 双方同时向对方候选刷 punch → 建立 UDP 通道
> - 全锥 NAT 场景好友 punch 先到，房主无需粘回执也能直接通
> - 对称 NAT（实测本机 CGNAT 就是）自动降级为"仅局域网候选"，跨网提示走 frp
> - 好友侧隧道在邀请窗关闭后由 `MultiplayerP2pRegistry` 接管，游戏连 `127.0.0.1:<临时端口>`
> - 代码：`src/Fuse.Core/Multiplayer/FuseLink/{StunClient,FuseLinkPipe,FuseLinkCodec,FuseLinkSession}.cs`
> - 测试：`FuseLinkTests.cs`（Codec/ARQ 顺序与重传/本机双端打洞/TCP 代理回显/抢跑排队）共 7 条；
>   App 实机链路验证：深链 → 邀请窗 → 复制回执 → 房主粘贴 → 隧道建立 → 游戏 `quickPlay` 连本机代理
>   → 房主侧收到 Minecraft 握手字节。



> 依据 Essential 架构情报（ICE=STUN+TURN、伪局域网、`wss://connect.essential.gg/v1` 信令，逆向信息以实测为准；社区共识确认其代理隧道架构）。
> 启动器版"伪局域网"：游戏层 TCP 套进自建 UDP 隧道打洞，两端本地 TCP 代理，好友 quickPlay 连 `127.0.0.1:xxxx`。

### 与 mod 方案（e4mc/World Host）对比

| | fuse-link（启动器隧道） | e4mc / World Host |
|---|---|---|
| 新 MC 版本适配 | 无需（版本无关） | 每版本等 mod 更新 |
| 好友侧 | 只要有 Fuse | 主机装 mod（e4mc 好友侧可不装） |
| 账号 | 不注册任何平台 | Essential 需其账号体系 |
| 对称 NAT | 检测后降级 frp | TURN 中继兜底 |

### 组件（Fuse.Core/Multiplayer/FuseLink/*.cs）

1. **StunClient**：RFC 5389 子集（Binding Request + XOR-MAPPED-ADDRESS），双 STUN 服务器交叉探测——映射端口一致 = 锥形 NAT（可打洞）；不一致 = 对称 NAT（放弃，走 frp）。免费公共 STUN 实测顺序：`stun.miwifi.com`（可用）、`stun.l.google.com`（可用）、`stun.qq.com`（本网络超时）
2. **HolePunch**：双方同时向对方"公网映射地址:端口"互发 UDP 保活包，穿透锥形 NAT（受限锥对受限锥可穿）
3. **ReliableStream**：极简 ARQ（seq/ack/超时重传/滑动窗口）——MC 每玩家带宽 <150KB/s，ARQ 足够。**不用 QUIC**：net48 无 System.Net.Quic，MsQuic 在 Win10 需装系统库
4. **LocalProxy**：两端 `TcpListener`（127.0.0.1），桥接游戏 TCP ↔ 隧道 UDP 字节流
5. **OssSignaling**：复用既有 OSS 桶做信令——房主 PUT offer 对象 → 好友轮询 GET / PUT answer → 建立。秒级延迟可接受（连接建立是低频操作），**零新增服务器**。不用 WebSocket 信令（Essential 有平台养，我们没有）
6. **握手鉴权**：邀请码携带一次性 token，隧道握手校验，防公网扫描蹭网

### 通道降级链（最终形态）

```
UPnP 映射 → IPv6 直连 → fuse-link P2P 打洞(STUN) → frp 中转(P2)
```
每层失败自动降级下一层，面板显示当前走的通道与延迟。

### 工作量与风险

- ≈1200–1500 行 + 单测（ARQ/STUN/打洞/信令均可离线单测：本机双进程回环模拟两端）
- 最大风险：家庭双层 NAT（光猫+路由器）把锥形打洞变成对称——已由降级链兜底
- net48 无 Quic/MsQuic 依赖问题 → 纯托管实现规避

### 不抄清单

- WebSocket 信令基础设施（OSS 信令替代）
- JSON 键名压缩（单次信令 KB 级，无优化价值）
- TURN 自建中继（带宽成本，Essential 靠化妆品变现养；我们用 frp 兜底）

## 6. P2「frp 集成」详细设计（需按平台文档复核；fuse-link 打洞失败时的最终中转兜底）

- 打包 frpc.exe（Apache-2.0，THIRD-PARTY-NOTICES 登记），不内置任何平台 token/账号
- 流程：用户在 OpenFrp/樱花面板建 TCP 隧道 → 复制 token/隧道 ID 粘进启动器 → `TunnelRunner` 拉起对应平台 frpc（参数模板按平台文档生成）→ 面板显示映射地址 → 生成邀请码
- 凭据只存本机 DPAPI；启动器不做平台登录中转
- 平台条款合规提示（实名/流量限额 10Mbps≈1.25MB/s 够 5–10 人）写进引导页

## 7. P3 远期（只留接口）

- `ITunnelProvider` 接口（P2 的 frp 是第一个实现）：未来可插自建 relay / World Host 开源代理自部署
- 迷你服务端：下载 server jar + 一键开服 + 白名单（重，等真实需求）

## 8. 安全设计

1. **最小暴露**：端口仅世界开放期间映射；UPnP 记录打上 `Fuse Launcher` 描述，退出必删（PlayTracker Exited + LanPortWatcher 双保险）
2. **身份提示**：`online-mode=false`（简单联机）时明示「任何拿到地址的人都能进，进服后可踢人」；推荐离线朋友互相之间用小圈子邀请码
3. **防外泄**：邀请码默认纯文本地址不带房间名；公网 IP 探测失败时绝不把内网地址当公网发出
4. **命令行安全**：quickPlay 地址来自邀请码解析，正则白名单校验（host 部分只允许 `[0-9a-zA-Z.\-\[\]:]`），防注入

## 9. 合规/法务

- frpc（Apache-2.0）随包分发合法，NOTICES 登记；樱花/OpenFrp 为第三方平台，启动器不代登录不存平台凭据
- e4mc/World Host/简单联机均为开源 mod，按各自许可（MIT/GPL 等）正常分发 jar，不修改不重打包
- 商标口径不变：功能名「联机」为通用词

## 10. 测试计划

| 用例 | 方式 |
|---|---|
| LAN 端口捕获 | 1.20.1 / 1.12.2 / 26.3 三个版本实机开 LAN，看正则命中 |
| UPnP | 本机路由器实测 Add/Remove；无 UPnP 环境用 Mock 接口测状态机 |
| 公网 IP 探测 | 双源（ipify + ip.sb）一致性 |
| IPv6 | 4G 热点 vs 家宽对比；路由器拦入站的引导文案 |
| 邀请码 e2e | 双机（或虚拟机）点链接 → 自动进服 |
| SLP | 对本地 server / 真实公网服 ping |
| frpc | OpenFrp 免费隧道跑通一场 |
| 安全 | 邀请码注入样本（`&xx=`、引号、空白）全部拒绝 |

## 11. 里程碑

| 阶段 | 内容 | 规模预估 |
|---|---|---|
| M1 | LanPortWatcher + InviteCodec + DeepLink IPC + SLP + 加入流程 | 中（Core 可单测，UI 一张卡片） |
| M2 | UPnP + 防火墙 + IPv6 + 通道降级状态机 | 中（需真实路由器验证） |
| M3 | P1 三 mod 一键 + 地址捕获/粘贴兜底 | 小（复用 ModInstaller） |
| M4 | fuse-link P2P 自研隧道（§5.5） | 大（~1500 行 + 回环单测；需双网络实测打洞） |
| M5 | P2 frpc 托管 | 小-中（平台文档复核） |

## 附录 A：调研结论速记

- [e4mc](https://modrinth.com/project/qANg5Jrr)：主机装 mod，QUIC 外连中转，聊天栏给 `xxx.e4mc.link`，好友直连输入；中转在海外
- [World Host](https://modrinth.com/mod/world-host)（[开源](https://github.com/Gaming32/world-host)）：好友系统 / 直连 / 代理三模式
- [Essential](https://essential.gg)：中央撮合 + 尽量 P2P，化妆品变现支撑免费联机；[社区分析](https://www.reddit.com/r/feedthebeast/comments/1b61ld7/so_about_the_essentials_mod_how_does_it_actually)确认其代理隧道架构；用户逆向情报：ICE(STUN+TURN)、`stun/turn/connect.essential.gg`、`wss://connect.essential.gg/v1` JSON 信令 → 启发 §5.5 fuse-link（只抄 ICE 降级思想与伪局域网形态，不抄信令基础设施/键名压缩/TURN 自建）
- [樱花Frp/OpenFrp](https://docs.openfrp.net)：国内免费 10Mbps，需实名注册；frpc 二进制可集成
- [playit.gg](https://playit.gg)：免费但免费节点在海外，国内延迟高（[社区反馈](https://www.reddit.com/r/admincraft/comments/1rk3ja3/)）
- [UPnP C#](https://www.cnblogs.com/mq0036/p/6589996.html)：NATUPnP COM 可用；部分路由器返回 null
- [IPv6 联机](https://zh.minecraft.wiki/w/Tutorial:IPv6%E8%81%94%E6%9C%BA)、[家宽 IPv6 坑：路由器默认拦入站](https://skyblond.info/archives/1080.html)
- [简单联机 mod](https://www.mcmod.cn/class/1158.html)：解决原版 LAN 校验会话导致离线好友进不来
- [2022 三大启动器联机联合公告](https://www.minebbs.com/threads/bakaxl-pcl-hmcl.14892)：第三方联机服务商收费 → 集体下架 → 本文"通道可插拔"原则来源
- [Tailscale/ZeroTier](https://zhuanlan.zhihu.com/p/599727246)：需注册装客户端，小白不友好，不作为默认通道
