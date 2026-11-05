# Fuse Launcher 安装包构建脚本:publish 启动器 → 打 payload.zip → 编译单文件安装器
#
# 产物名跟着 <Version> 走(旧脚本写死 1.0.0,导致每次发版都要手动改名;
# 更糟的是所有版本共用同一个文件名 —— 一旦有 CDN/浏览器缓存,
# 客户端会下到旧包、SHA 校验失败,表现为"更新失败"且看不出原因)。
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)   # 仓库根
$appProj = Join-Path $root 'src\Fuse.App\Fuse.App.csproj'

# 0) 从 csproj 读版本号 —— 这是唯一可信来源,别在脚本里再写一份
[xml]$proj = Get-Content $appProj -Encoding UTF8
$version = ($proj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { throw "csproj 里没有 <Version>:$appProj" }
if ($version -notmatch '^[0-9]+(\.[0-9]+){1,3}$') { throw "版本号不合法:$version(只允许 2~4 段数字)" }
Write-Host "version: $version"

# 1) Release 发布启动器
$out = & dotnet publish $appProj -c Release -r win-x64 --self-contained false -o (Join-Path $root 'dist\app') 2>&1
Write-Host ('publish exit=' + $LASTEXITCODE)
$out | Select-Object -Last 6 | ForEach-Object { Write-Host ("| " + $_) }
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

# 1.5) 卸载助手(独立小程序,和安装器外观/体积都不同;放进 payload 一起分发)
$out = & dotnet build (Join-Path $root 'src\FuseUninstaller\FuseUninstaller.csproj') -c Release 2>&1
Write-Host ('uninstaller exit=' + $LASTEXITCODE)
$out | Select-Object -Last 4 | ForEach-Object { Write-Host ("| " + $_) }
if ($LASTEXITCODE -ne 0) { throw 'uninstaller build failed' }
$uninstallerExe = Join-Path $root 'src\FuseUninstaller\bin\Release\net48\Uninstall Fuse Launcher.exe'
if (-not (Test-Path $uninstallerExe)) { throw 'uninstaller exe missing' }

# 2) 打包 payload(剔除 pdb:里面含源码路径,不随安装包分发)
$stage = Join-Path $env:TEMP ('fuse-payload-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
Copy-Item (Join-Path $root 'dist\app\*') $stage -Exclude *.pdb
Copy-Item $uninstallerExe (Join-Path $stage 'Uninstall Fuse Launcher.exe')
$payloadZip = Join-Path $root 'src\FuseSetup\payload.zip'
if (Test-Path $payloadZip) { Remove-Item $payloadZip }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $payloadZip -CompressionLevel Optimal
Remove-Item -Recurse -Force $stage

# 3) 编译安装器(把版本号传进去,保证"设置→应用"里显示的 DisplayVersion 与启动器一致)
& dotnet build (Join-Path $root 'src\FuseSetup\FuseSetup.csproj') -c Release -p:Version=$version
if ($LASTEXITCODE -ne 0) { throw 'setup build failed' }

# 4) 产物落位 + 改正式文件名(带版本号)
$outDir = Join-Path $root 'dist'
$setupName = "FuseLauncher-Setup-$version.exe"
$setupPath = Join-Path $outDir $setupName
Copy-Item (Join-Path $root 'src\FuseSetup\bin\Release\net48\FuseSetup.exe') $setupPath -Force
if (-not (Test-Path $setupPath)) { throw "安装包没有生成:$setupPath" }

Write-Host ('installer: ' + $setupPath)
Write-Host ('size: ' + [math]::Round((Get-Item $setupPath).Length / 1MB, 2) + ' MB')
Write-Host ''
Write-Host '下一步(生成本版本清单):'
Write-Host ("  powershell -File tools\make-manifest.ps1 -Version $version -SetupPath dist\$setupName -BaseUrl `"https://你的bucket.oss-cn-shenzhen.aliyuncs.com/fuse`" -Notes `"本次更新说明`"")
