# 自更新(自动更新)运维指南

启动器内置自更新:从远端"更新清单"获取新版本信息,下载新版安装包,**SHA-256 校验通过**后静默重装(保留用户 config 与游戏目录),完成后自动重启。

## 0. 安全模型(先读这段)

本项目**没有对安装包做代码签名**(未购买证书),所以信任链只有两层:

1. **强制 HTTPS** —— 清单与安装包都必须是 `https://`,且不允许重定向降级到 http;
2. **SHA-256 + 同源约束** —— 安装包地址必须与清单**同源**(同一域名),哈希必填。

这意味着:能改写你 OSS 内容的人,就能给所有用户推送任意程序。**保护 OSS 的 AccessKey 等同于保护发布权**。

> 将来若要更强的保证:买代码签名证书,在构建时 `signtool sign`,客户端下载后调
> `WinVerifyTrust` 校验签名并 pin 证书指纹。这样即使 OSS 被拿下也装不上木马。

## 1. 清单格式

```json
{
  "version": "1.0.1",
  "url": "https://your-bucket.oss-cn-shenzhen.aliyuncs.com/fuse/FuseLauncher-Setup-1.0.1.exe",
  "sha256": "安装包的 SHA-256(64 位小写十六进制,必填)",
  "sha1": "兼容旧客户端用,可省略",
  "size": 52428800,
  "notes": "本次更新说明(显示在发现新版本弹窗里)"
}
```

- `version`:2~4 段纯数字,必须与安装包内程序集版本一致(否则更新后还会提示)。
- `sha256`:**必填**。缺失时客户端直接拒绝更新(不再静默跳过校验)。
- `sha1`:仅为老客户端保留;新客户端优先用 `sha256`,只有清单没有 sha256 时才回退并记警告。
- `size`:可选,用于下载前预判(客户端仍以哈希为准)。
- `url` 的域名必须与清单**同源**,否则拒绝。

## 2. 发布一次更新的流程

**一条命令搞定**(推荐):

```powershell
# 1) 先把 src/Fuse.App/Fuse.App.csproj 的 <Version> 改成新版本号
# 2) 预览:不打上传,只显示将要做什么
powershell -File tools\release.ps1 -Notes "修复了 xxx"

# 3) 真正发布
powershell -File tools\release.ps1 -Notes "修复了 xxx" -Upload
```

`release.ps1` 会依次:

1. 从 csproj 读版本号(唯一来源,不用手写文件名);
2. 调 `src/FuseSetup/build-installer.ps1` 打包;
3. 调 `tools/make-manifest.ps1` 现算 sha256 生成清单;
4. 拉一次**远端现有清单**做比较 —— 版本没升或要倒退时**直接拒绝发布**;
5. 按「**先安装包、后清单**」的顺序上传,并带上正确的 `Cache-Control`;
6. 用**公网地址回读**清单与安装包,核对版本、sha256、文件大小。

第 6 步是重点:它验证的是"用户真的能拿到这一版",而不是"刚才那条命令没报错"。

**手工流程**(没有 ossutil 或想分步确认时):

1. 改 csproj 的 `<Version>` → `powershell -File src\FuseSetup\build-installer.ps1`
   → 产出 `dist/FuseLauncher-Setup-<版本>.exe`
2. `powershell -File tools\make-manifest.ps1 -Version <版本> -SetupPath dist\FuseLauncher-Setup-<版本>.exe -BaseUrl "https://your-bucket.oss-cn-guangzhou.aliyuncs.com/fuse" -Notes "…"`
   → 产出 `dist/manifest.json`
3. 上传:先安装包,再清单(见第 5 节)

**发布顺序很重要**:先传安装包,再传 `manifest.json`。
反过来的话,清单已经指向一个还不存在的包,这段时间内检查更新的用户会失败。

## 3. 用户端配置

`%APPDATA%\FuseLauncher\config.json`:

```json
"AutoUpdate": true,
"UpdateManifestUrl": "https://your-bucket.oss-cn-shenzhen.aliyuncs.com/fuse/manifest.json"
```

- `UpdateManifestUrl` 为空 = 未配置更新源,检查更新会明确提示。
- **必须是 https**:填 http 会被直接拒绝并在界面上报错。
- 清单不可达/格式错误:自动检查静默跳过(写 boot.log),手动检查显示具体原因。

## 4. 安全机制

| 机制 | 说明 |
| --- | --- |
| 强制 HTTPS | 清单与安装包都要求 https;跟随重定向后仍校验最终地址是 https |
| 同源约束 | 安装包 address 的域名必须等于清单的域名 |
| SHA-256 必填 | 缺失即拒绝;先写 `.tmp`,校验通过才转正 |
| 大小上限 | 清单 64KB、安装包 300MB,超限中止(防塞满磁盘) |
| 版本号白名单 | 只接受 2~4 段数字,顺带避免版本号进文件路径时的意外 |
| 接力安装 | 本进程退出后才覆盖文件(运行中的 exe 不能被替换) |
| 路径不拼脚本 | 安装路径经环境变量传入 PowerShell,路径含 `'` 也不会破坏语句 |
| 按 PID 结束进程 | 不再按进程名杀,避免误杀同名实例 |
| 并发闸门 | 自动检查、手动检查、下载安装不会叠加 |
| 超时 | 清单 15s、下载 240s,失败有明确报错 |

## 5. OSS 托管(阿里云对象存储)

**当前使用的存储**:

| 项 | 值 |
| --- | --- |
| Bucket | `your-bucket` |
| 地域 | 广州 `oss-cn-guangzhou` |
| 访问域名 | `https://your-bucket.oss-cn-guangzhou.aliyuncs.com` |
| 清单地址 | `https://your-bucket.oss-cn-guangzhou.aliyuncs.com/fuse/manifest.json` |

**状态**:对象公开可读、HTTPS 正常、DNS 正常;桶根 `GET /` 返回
403 `AccessDenied` 属于正常现象 —— 那是"不允许匿名列举",不影响单个对象读取
(不存在的对象返回的是 404 `NoSuchKey`,不是 403)。

**目录结构**(前缀 `fuse/`):

```
fuse/
  manifest.json                      ← 每次都覆盖
  FuseLauncher-Setup-1.0.0.exe       ← 按版本累积,不要覆盖
  FuseLauncher-Setup-1.1.0.exe
  FuseLauncher-Setup-latest.exe      ← 指向最新版,每次发布跟着更新
```

**缓存头(关键,配错会"发版了用户看不到")**:

| 对象 | Cache-Control |
| --- | --- |
| `manifest.json` | `no-store` |
| `FuseLauncher-Setup-*.exe` | `public, max-age=31536000, immutable` |

安装包文件名带版本号,内容永不变,所以可以长缓存;清单必须每次现取
(客户端还会额外发 `Cache-Control: no-cache`,但服务端头更可靠)。

**上传**:

```powershell
# 有 ossutil 时:release.ps1 -Upload 会自动按顺序上传并设好缓存头。
# 手工上传就用这两条(注意顺序):
ossutil cp dist\FuseLauncher-Setup-1.0.1.exe oss://your-bucket/fuse/ `
  --meta "Cache-Control:public, max-age=31536000, immutable"
ossutil cp dist\manifest.json oss://your-bucket/fuse/ `
  --meta "Cache-Control:no-store"
```

**没有 ossutil 时**:用 OSS 控制台上传,并在对象的「HTTP 头」里分别设置上面两个
`Cache-Control`。ossutil 2.x 的参数是 `--metadata` 而不是 `--meta`,`release.ps1`
会自动识别版本。

**Bucket 要求**:
- 读权限:公共读(当前已满足);
- 必须走 HTTPS 域名(当前已满足);
- 建议给 bucket 设**流量封顶**与防盗链,避免被刷流量。

## 5.5 网页下载页

下载页可以挂在任意静态站点上：

| 用法 | 地址 |
| --- | --- |
| 给人看的短地址 | `https://launcher.example.com/fuse`（301 → `/#/fuse`） |
| 站内路由 | `https://launcher.example.com/#/fuse` |
| 站内入口 | 关于页 →「我的项目」→ Fuse Launcher |

页面只做展示：从清单拿版本号 / 大小 / SHA-256 / 更新说明，点「下载」**直连 OSS**。
安装包不经过网页服务器 —— 所以没有带宽、Range、大文件缓冲这些问题。

**清单走同源反代**：`launcher.example.com/fuse/manifest.json` → nginx（缓存 60s）→ OSS。

- 好处：不用给 OSS 配 CORS，站点 CSP 的 `connect-src 'self'` 也不用放宽；
- 只反代这一个几百字节的对象，且只允许 GET/HEAD；
- 该 location **刻意不写 `add_header`** —— nginx 里子级一旦写了 add_header，
  就会丢掉父级全部响应头（`X-Frame-Options` 这类会一起消失）。

> ⚠️ **`https://launcher.example.com/fuse/manifest.json` 只能给网页用，不能填进客户端的
> `UpdateManifestUrl`。** 启动器有同源约束：清单在自有域名、安装包在 OSS，
> 会被判成不同源并**拒绝更新**。客户端更新源永远是
> `https://your-bucket.oss-cn-guangzhou.aliyuncs.com/fuse/manifest.json`

**页面的安全处理**（代码在 `meme-site/app/components/FusePage.jsx`，
另有构建期守卫 `.shots/check-build.mjs` 的「下载页安全守卫」12 项）：

| 项 | 处理 |
| --- | --- |
| 不信任清单 | `version` 必须 2~4 段数字，不合格整份清单作废 |
| 下载地址自拼 | 固定域名 + `FuseLauncher-Setup-<version>.exe`；清单的 `url` 只做交叉校验 |
| 渲染 | 一律纯文本（不用 innerHTML），notes 截断 400 字 |
| 超时 / 体积 | 5 秒超时、64KB 上限 |
| 降级 | 清单读不到 → 按钮指向 `FuseLauncher-Setup-latest.exe` |

**`FuseLauncher-Setup-latest.exe` 是兜底别名**，每次发布由 `release.ps1` 自动覆盖上传
（`Cache-Control: no-store`）。手工发布时**别忘了它** —— 忘了的话兜底链接会指向上一个版本，
用户下到旧包还以为是最新的（比 404 更糟）。

## 6. 回滚

新版本有问题时:

1. **立刻**把 `manifest.json` 改回上一个版本的 `version` 与 `sha256`
   (安装包都还在 OSS 上,直接复用即可);
2. 已升级的用户:启动器发现"远端版本比本地旧"不会自动降级,
   需要用户手动装回旧包 —— 所以**发布前先自测**比回滚更重要;
3. 若新版一启动就崩,用户连自动更新都跑不起来,只能手动下载旧安装包重装。
   这也是为什么第 2 节要求**先传包、再传清单**。

## 7. 相关代码

- `src/Fuse.App/Updater.cs` — 清单/下载/校验/接力安装
- `src/Fuse.App/App.xaml.cs` — 启动后台检查 + 发现新版弹窗
- `src/Fuse.App/Panels/SettingsPanel.cs` — 自动更新开关 + 检查更新按钮
- `tools/make-manifest.ps1` — 清单生成(sha256 / sha1 / size)
- `src/FuseSetup/build-installer.ps1` — 打包安装器(文件名跟随 `<Version>`)
