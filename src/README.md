# Fuse Launcher

《我的世界》Java 版启动器，开源（GPL-3.0）、不盈利，作者 **liumo**。
产品名 **Fuse Launcher**，简称 **Fuse**。

当前版本：**1.1.1**（2026-11-05）

## 功能

**下载与安装**
- 原版（正式 / 快照）、Fabric、Forge、NeoForge、OptiFine、Fabric API
- 下载源三档：官方 / 均衡（默认）/ 镜像；启动时并发测速，按实测延迟选源
- 分块断点续传、SHA-1 校验、小文件高并发、429 熔断、镜像故障回退官方
- 任务队列：暂停 / 继续 / 重试 / 删除 / 清除已完成，进度持久化
- Java 自动发现；缺 Java 时按 Mojang 官方 java-runtime 清单自动下载

**启动**
- 多游戏目录（主目录 + 导入目录），版本列表跟随当前目录
- 内存自动估算 / 手动指定；自定义 JVM 参数；进程优先级
- 单实例守护（同一版本不会重复拉起）；开机自启动
- 启动特效（呼吸 → 成功脉冲 / 失败收缩）；游玩时长统计

**账号与皮肤**
- 微软正版：设备码 OAuth 六步链、DPAPI 加密存令牌、启动时静默续期
- 离线账号；外置登录（authlib-injector，LittleSkin / 自定义站点）
- 本地皮肤（离线账号自动装 CustomSkinLoader）、披风、3D 皮肤预览
- 皮肤来源按账号类型锁定：正版走官方、外置走站点、离线走本地

**版本与实例**
- 文件夹列表：新建 / 导入 / 重命名 / 移出 / 删除；**一键添加本机目录**
- 版本详情六个分页：修改 / 模组 / 光影 / 材质 / 本地 / 导出
- 崩溃救援：分析 crash-reports 定位疑似模组、安全模式、校验并修复文件
- 实例备份 / 还原 / 复制；导出 zip → 再导入闭环
- 存档 / 截图管理；服务器列表增删改 + 自动加入
- 整合包原地差量升级 + 回滚；解压式整合包导入

**模组与资源**
- Modrinth 搜索（内置中文名映射与本地排序）、类型筛选、无限滚动
- 模组启停（`.jar` ↔ `.jar.disabled`）、更新检查、一键更新（校验前置版本兼容）
- 光影 / 材质包浏览与安装；光影前置一键配对（Iris + Sodium / Oculus + Embeddium）
- `.mrpack` 整合包安装、更新检查、加载器识别（Fabric / Forge / NeoForge）

**界面与其他**
- 桌面悬浮组件：苦力怕大圆 + 五花瓣，可拖动、位置记忆、托盘常驻
- 玻璃面板、自定义强调色 / 背景色 / 悬停色、开屏动画
- 数据浮窗：FPS / GPU / CPU / RAM / NET / PING（FPS 需 PresentMon）
- 高速下载：任意直链 + 可选 SHA-256 校验
- 日志 / 崩溃查看器；错误弹窗一键导出日志
- 自更新（OSS 清单）、便携模式、安装器 / 卸载助手

## 工程结构

```
Fuse.sln
├─ src/Fuse.Core/            netstandard2.0 纯逻辑（零 UI 依赖）
│  ├─ Download/              分块 / 小文件 / 队列 / 镜像 / 测速
│  ├─ Installer/             原版、加载器、Forge processors、整合包、备份、升级
│  ├─ Launch/                参数组装、natives 提取、启动管线
│  ├─ Accounts/              离线 / 微软 / 外置登录
│  ├─ Versioning/            版本 JSON、继承合并、扫描、服务器列表（NBT）
│  ├─ Sources/               Mojang / Modrinth / OptiFine
│  ├─ Launcher/              游戏目录扫描与识别
│  ├─ Monitoring/            CPU / GPU / 内存 / 网速 / 延迟
│  ├─ Config/                配置、版本档案、游玩统计
│  └─ Logging/               滚动日志
├─ src/Fuse.App/             WPF net48 外壳（悬浮组件、面板、弹窗）
├─ src/FuseSetup/            单文件安装器（向导 / 静默 / 便携）
├─ src/FuseUninstaller/      独立卸载助手
├─ src/Fuse.Core.Tests/      xUnit（106 例）
├─ tools/                    release.ps1、make-manifest.ps1、LaunchDoctor
├─ web/                      下载页
└─ docs/                     自更新、法律风险说明
```

## 构建 / 测试 / 发版

```powershell
dotnet build Fuse.sln -c Release
dotnet test  src/Fuse.Core.Tests/Fuse.Core.Tests.csproj -c Release

# 打包 + 生成清单（只准备，不上传）
powershell -File tools\release.ps1 -Notes "本次更新说明"

# 有 ossutil 且已登录时，一条命令发布
powershell -File tools\release.ps1 -Notes "本次更新说明" -Upload
```

- 版本号唯一来源：`src/Fuse.App/Fuse.App.csproj` 的 `<Version>`
- 产物：`dist/FuseLauncher-Setup-<版本>.exe`、`dist/manifest.json`
- 上传顺序固定：版本化安装包 → `FuseLauncher-Setup-latest.exe` → `manifest.json`
- 清单里的 SHA-256 现算；上传后 `release.ps1` 会用公网地址回读自检

## 运行数据

| 内容 | 位置 |
| --- | --- |
| 配置 / 账号 / 日志 | `%APPDATA%\FuseLauncher`（便携模式为 `<exe>\data`） |
| 游戏文件 | 安装时选择的启动器文件夹（默认 `文档\FuseLauncher`） |
| 皮肤 | `文档\FuseLauncher\skins\local` |
| 启动日志 | `%APPDATA%\FuseLauncher\boot.log`、实例 `logs/latest-launch.log` |
| 崩溃日志 | `%APPDATA%\FuseLauncher\crash.log`、实例 `crash-reports` |

## 已知边界

- **CurseForge** 需要官方 API Key，暂未接入（当前基于 Modrinth 数据）。
- **数据浮窗 FPS** 依赖 PresentMon；PresentMon 需要管理员权限或
  `Performance Log Users` 组成员，未满足时 FPS 显示 `--`。
- **OptiFabric** 只能半自动：本体仅在 CurseForge 发布。
- **Quilt** 整合包暂不支持。
- 26.3 的窗口 / 输入问题（SDL3 后端）是游戏自身已知 bug
  （MC-309397、MC-309445），与启动器无关。

## 商标

Fuse Launcher 名称、Fuse 图标及应用图标不随 GPL 授权；未经书面许可，
不得用于修改版或再分发构建。fork 请使用自己的名称与视觉标识。

## 第三方组件

- 数据浮窗的 FPS 采样使用 Intel 开源的
  [PresentMon](https://github.com/GameTechDev/PresentMon)（MIT 许可）。
- JSON 序列化使用 Newtonsoft.Json（MIT 许可）。
- 完整清单见仓库根目录 `THIRD-PARTY-NOTICES.txt`。
