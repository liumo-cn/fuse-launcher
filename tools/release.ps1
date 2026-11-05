#Requires -Version 5.1
<#
.SYNOPSIS
    Fuse Launcher 一键发布：打包 → 生成清单 → 上传 OSS → 上线自检。

.DESCRIPTION
    把发布流程里最容易出错的四件事固定下来：
      1. 版本号只有一个来源（csproj），安装包文件名自动跟随；
      2. 清单里的 sha256 现算，绝不手填；
      3. 上传顺序固定为「先安装包、后清单」——
         反过来的话，清单会短暂指向一个不存在的包，这段时间检查更新的用户全部失败；
      4. 上传完立刻用公网地址回读清单，核对 sha256 与文件大小，
         确认"用户真的能拿到这一版"，而不是只相信刚才那条命令没报错。

.PARAMETER Version
    要发布的版本号。留空则从 src\Fuse.App\Fuse.App.csproj 的 <Version> 读取。

.PARAMETER Notes
    更新说明，显示在客户端的「发现新版本」弹窗里。

.PARAMETER BaseUrl
    清单与安装包的公网目录（必须是 https）。

.PARAMETER Upload
    真正执行上传。不加这个开关时只做本地准备并打印将要执行的命令。

.EXAMPLE
    # 只准备，不传（先看看会做什么）
    powershell -File tools\release.ps1 -Notes "修复了 xxx"

.EXAMPLE
    # 真正发布
    powershell -File tools\release.ps1 -Notes "修复了 xxx" -Upload
#>
param(
    [string]$Version = "",
    [string]$Notes = "",
    [string]$BaseUrl = "https://fuse-launcher-update.oss-cn-guangzhou.aliyuncs.com/fuse",
    [string]$OssTarget = "oss://fuse-launcher-update/fuse/",
    [switch]$SkipBuild,
    [switch]$Upload
)

$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 默认可能还在用 TLS 1.0/1.1，访问 OSS 会直接"基础连接已关闭"。
# 显式开到 TLS 1.2（1.3 在部分系统上不支持，用位或安全叠加）。
try {
    $proto = [Net.SecurityProtocolType]::Tls12
    if ([Enum]::GetNames([Net.SecurityProtocolType]) -contains 'Tls13') {
        $proto = $proto -bor [Net.SecurityProtocolType]::Tls13
    }
    [Net.ServicePointManager]::SecurityProtocol = $proto
} catch { }

$root = Split-Path -Parent $PSScriptRoot
$manifestScript = Join-Path $PSScriptRoot 'make-manifest.ps1'
$buildScript = Join-Path $root 'src\FuseSetup\build-installer.ps1'
$manifestPath = Join-Path $root 'dist\manifest.json'

function Step($n, $text) { Write-Host ''; Write-Host ("[$n] " + $text) -ForegroundColor Cyan }
function Fail($text) { Write-Host ('  ✗ ' + $text) -ForegroundColor Red; exit 1 }
function Ok($text) { Write-Host ('  ✓ ' + $text) -ForegroundColor Green }

# ---------- 1) 版本号 ----------
Step '1/5' '确定版本号'
if ($Version) {
    if ($Version -notmatch '^[0-9]+(\.[0-9]+){1,3}$') { Fail "版本号不合法：$Version（只允许 2~4 段数字）" }
    Ok "命令行指定：$Version"
}
else {
    [xml]$proj = Get-Content (Join-Path $root 'src\Fuse.App\Fuse.App.csproj') -Encoding UTF8
    $Version = ($proj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
    if (-not $Version) { Fail 'csproj 里没有 <Version>' }
    if ($Version -notmatch '^[0-9]+(\.[0-9]+){1,3}$') { Fail "csproj 版本号不合法：$Version" }
    Ok "从 csproj 读取：$Version"
}

# ---------- 2) 打包 ----------
Step '2/5' '打安装包'
$setupPath = Join-Path $root ("dist\FuseLauncher-Setup-$Version.exe")
if ($SkipBuild) {
    if (-not (Test-Path $setupPath)) { Fail "跳过打包，但安装包不存在：$setupPath" }
    Ok "跳过打包（-SkipBuild），复用已有安装包"
}
else {
    # 用 & 在当前进程的子作用域里调用，而不是再起一个 powershell -File 子进程：
    # 子进程那条路会把中文参数转码 —— -Notes 里的中文会变成乱码写进清单，
    # 最终显示在客户端的更新弹窗里（实测踩过）。同进程调用没有这个转码环节，
    # 而且子脚本里的 throw 能直接传回来，错误原因不会被吞掉。
    try {
        & $buildScript
    }
    catch {
        Fail ('打包失败：' + $_.Exception.Message)
    }
    if ($LASTEXITCODE -ne 0) { Fail '打包失败' }
    if (-not (Test-Path $setupPath)) { Fail "打包完成但没找到：$setupPath" }
    Ok ("安装包 " + [math]::Round((Get-Item $setupPath).Length / 1MB, 2) + ' MB')
}

# ---------- 3) 清单 ----------
Step '3/5' '生成清单（sha256 现算）'
# 同上：同进程调用，避免中文 notes 被转码
try {
    & $manifestScript -Version $Version -SetupPath $setupPath -BaseUrl $BaseUrl `
        -OutFile $manifestPath -Notes $Notes `
        -CsprojPath (Join-Path $root 'src\Fuse.App\Fuse.App.csproj') | Out-Null
}
catch {
    Fail ('生成清单失败：' + $_.Exception.Message)
}
if (-not (Test-Path $manifestPath)) { Fail '清单没有生成' }

$manifest = Get-Content $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.sha256 -notmatch '^[0-9a-f]{64}$') { Fail '清单里的 sha256 格式不对' }
if ($manifest.url -notmatch '^https://') { Fail '清单里的 url 不是 https' }
$baseHost = ([Uri]$BaseUrl).Host
$urlHost = ([Uri]$manifest.url).Host
if ($urlHost -ne $baseHost) { Fail "安装包与清单不同源（$urlHost vs $baseHost），客户端会拒绝" }
Ok ('sha256 ' + $manifest.sha256.Substring(0, 16) + '…  url 同源 ✓')

# ---------- 4) 远端版本比较（防重复发布/倒退发布） ----------
Step '4/5' '检查远端当前版本'
$remoteUrl = $BaseUrl.TrimEnd('/') + '/manifest.json'
$remote = $null
try {
    $r = Invoke-WebRequest -Uri $remoteUrl -UseBasicParsing -TimeoutSec 15 -ErrorAction Stop
    $remote = $r.Content | ConvertFrom-Json
    Ok ('远端当前：' + $remote.version)
}
catch {
    # 只有 404 才算"首次发布"。网络故障不能当成首次发布 ——
    # 否则"防倒退"这道检查会在最需要它的时候（网络异常）静默失效。
    $status = $null
    try { $status = [int]$_.Exception.Response.StatusCode } catch { }
    if ($status -eq 404) {
        Ok '远端还没有清单（首次发布）'
    }
    else {
        $why = if ($status) { "HTTP $status" } else { $_.Exception.Message }
        Write-Host ('  ! 无法确认远端版本（' + $why + '），本次跳过防倒退检查') -ForegroundColor Yellow
    }
}

if ($remote -and $remote.version) {
    # 补零到 4 段比较，避免 1.0.1 被判成小于 1.0.1.0
    function Norm([string]$v) {
        $p = ($v.Trim() -replace '^[vV]', '').Split('.')
        $n = @(0, 0, 0, 0)
        for ($i = 0; $i -lt [Math]::Min(4, $p.Length); $i++) { $n[$i] = [int]$p[$i] }
        return ('{0}.{1}.{2}.{3}' -f $n[0], $n[1], $n[2], $n[3])
    }
    $cmp = ([Version](Norm $Version)).CompareTo([Version](Norm $remote.version))
    if ($cmp -eq 0) { Fail "远端已经是 $Version，版本号没有升（客户端不会认为有更新）" }
    if ($cmp -lt 0) { Fail "远端版本 $($remote.version) 比要发布的 $Version 还新，拒绝倒退发布" }
    Ok "从 $($remote.version) 升到 $Version"
}

# ---------- 5) 上传 ----------
Step '5/5' '上传到 OSS'
$setupName = Split-Path $setupPath -Leaf
$ossutil = Get-Command ossutil -ErrorAction SilentlyContinue

if (-not $Upload) {
    Write-Host '  当前未加 -Upload，只打印命令：' -ForegroundColor Yellow
    Write-Host ''
    Write-Host '  # 1) 版本化安装包（长缓存）'
    Write-Host ("  ossutil cp `"$setupPath`" $OssTarget --meta `"Cache-Control:public, max-age=31536000, immutable`"")
    Write-Host '  # 2) latest 别名（不缓存，下载页兜底用）'
    Write-Host ("  ossutil cp `"$setupPath`" ${OssTarget}FuseLauncher-Setup-latest.exe --meta `"Cache-Control:no-store`"")
    Write-Host '  # 3) 清单（必须最后传）'
    Write-Host ("  ossutil cp `"$manifestPath`" $OssTarget --meta `"Cache-Control:no-store`"")
    Write-Host ''
    Write-Host '  确认无误后加 -Upload 重跑；或把上面三条贴到已登录 ossutil 的终端执行。'
}
else {
    if (-not $ossutil) {
        Fail @'
没有找到 ossutil。两种办法：
  A. 装 ossutil（阿里云官方命令行工具），装好后重跑本脚本；
  B. 用 OSS 控制台手工上传，并给对象设置 HTTP 头：
     安装包 → Cache-Control: public, max-age=31536000, immutable
     清单   → Cache-Control: no-store
'@
    }

    # ossutil 1.x 用 --meta，2.x 用 --metadata；先探测再用，避免版本差异
    $verText = (& ossutil version 2>&1 | Out-String)
    $metaFlag = if ($verText -match 'v?2\.\d') { '--metadata' } else { '--meta' }
    Ok ('ossutil 使用 ' + $metaFlag)

    Write-Host '  先传安装包（长缓存）…'
    & ossutil cp $setupPath $OssTarget $metaFlag 'Cache-Control:public, max-age=31536000, immutable'
    if ($LASTEXITCODE -ne 0) { Fail '安装包上传失败' }

    # latest 别名：下载页在清单读不到时兜底用。
    # 每次发布都必须跟着更新 —— 忘了传的话，兜底链接会指向上一个版本，
    # 用户下到旧包还以为是最新的（比 404 更糟）。所以必须 no-store。
    Write-Host '  传 latest 别名（不缓存）…'
    & ossutil cp $setupPath ($OssTarget + 'FuseLauncher-Setup-latest.exe') $metaFlag 'Cache-Control:no-store'
    if ($LASTEXITCODE -ne 0) { Fail 'latest 别名上传失败' }

    Write-Host '  最后传清单（不缓存）…'
    & ossutil cp $manifestPath $OssTarget $metaFlag 'Cache-Control:no-store'
    if ($LASTEXITCODE -ne 0) { Fail '清单上传失败（此时安装包已在 OSS 上，可安全重试本步）' }
}

# ---------- 上线自检 ----------
Step '自检' '用公网地址回读'
$selfCheckDone = $false
if (-not $Upload) {
    Ok '未上传，跳过公网回读'
}
else {
try {
    $r = Invoke-WebRequest -Uri $remoteUrl -UseBasicParsing -TimeoutSec 20 -ErrorAction Stop
    $live = $r.Content | ConvertFrom-Json
    # 这两个是"真不一致"，必须报错：用户会拿到错的版本
    if ($live.version -ne $Version) { Fail "公网清单版本是 $($live.version)，不是 $Version（上传的清单不是这一版？）" }
    if ($live.sha256 -ne $manifest.sha256) { Fail '公网清单的 sha256 与本地不一致（有缓存或传错了文件）' }
    Ok "清单可达且版本正确（$Version）"
    $selfCheckDone = $true

    $exeUrl = $live.url
    $head = Invoke-WebRequest -Uri $exeUrl -Method Head -UseBasicParsing -TimeoutSec 20 -ErrorAction Stop
    $len = [int64]$head.Headers['Content-Length']
    if ($len -ne [int64]$manifest.size) { Fail "公网安装包大小 $len 与清单 size $($manifest.size) 不一致" }
    Ok ('安装包可下载，大小一致（{0:N2} MB）' -f ($len / 1MB))
    $cache = $head.Headers['Cache-Control']
    if ($cache -and $cache -match 'immutable') { Ok "安装包缓存头：$cache" }
    else { Write-Host ("  ! 安装包缓存头是 '" + $cache + "'，建议设为 public, max-age=31536000, immutable") -ForegroundColor Yellow }

    # latest 别名：下载页的兜底链接。它必须是这一版，否则用户会下到旧包。
    $latestUrl = $BaseUrl.TrimEnd('/') + '/FuseLauncher-Setup-latest.exe'
    $latestHead = Invoke-WebRequest -Uri $latestUrl -Method Head -UseBasicParsing -TimeoutSec 20 -ErrorAction Stop
    $latestLen = [int64]$latestHead.Headers['Content-Length']
    if ($latestLen -ne [int64]$manifest.size) {
        Fail "latest 别名大小 $latestLen 与本版 $($manifest.size) 不一致 —— 它可能是上一个版本的包"
    }
    Ok 'latest 别名已更新为本版'
    $latestCache = $latestHead.Headers['Cache-Control']
    if ($latestCache -and $latestCache -match 'no-store') { Ok "latest 缓存头：$latestCache" }
    else { Write-Host ("  ! latest 缓存头是 '" + $latestCache + "'，建议 no-store（否则兜底链接可能被缓存成旧包）") -ForegroundColor Yellow }
}
catch {
    # 注意：这里**不能 Fail**。上传已经成功，只是本机读不回来 ——
    # 本机 .NET 的 HTTPS 栈坏了（schannel）时 Invoke-WebRequest 会直接报
    # "基础连接已经关闭"，跟 OSS 无关。把发布判成失败会误导人重复上传。
    Write-Host ('  ! 公网回读失败：' + $_.Exception.Message) -ForegroundColor Yellow
    Write-Host '    上传可能已经成功，只是本机 HTTPS 读不回来。请用浏览器打开下面地址人工确认：' -ForegroundColor Yellow
    Write-Host ('      ' + $remoteUrl) -ForegroundColor Yellow
}
}

Write-Host ''
Write-Host '发布完成。客户端配置：' -ForegroundColor Green
Write-Host ('  "UpdateManifestUrl": "' + $remoteUrl + '"')
Write-Host ''
