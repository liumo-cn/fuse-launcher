# 生成自动更新清单(manifest.json):读取安装包、计算 SHA-256(以及兼容用的 SHA-1),输出可直接上传的清单。
#
# 用法示例(OSS 托管):
#   powershell -File tools\make-manifest.ps1 `
#     -Version 1.0.1 `
#     -SetupPath dist\FuseLauncher-Setup-1.0.1.exe `
#     -BaseUrl "https://你的bucket.oss-cn-shenzhen.aliyuncs.com/fuse" `
#     -Notes "修复了……"
#
# 输出:dist\manifest.json(把 manifest.json 和安装器一起上传到 BaseUrl 对应的目录)
#
# 为什么清单只写 https 地址:启动器会拒绝非 https 的更新源与安装包地址。
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$SetupPath,
    [Parameter(Mandatory = $true)][string]$BaseUrl,
    [string]$OutFile = "",
    [string]$Notes = "",
    [string]$CsprojPath = "src\Fuse.App\Fuse.App.csproj"
)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path $SetupPath)) { throw "安装包不存在: $SetupPath" }
if ($BaseUrl.EndsWith('/')) { $BaseUrl = $BaseUrl.TrimEnd('/') }

# 版本号:启动器只接受 2~4 段纯数字,否则会拒绝更新
if ($Version -notmatch '^[0-9]+(\.[0-9]+){1,3}$') {
    throw "版本号不合法: $Version(只允许 2~4 段数字,例如 1.0.1)"
}

# 与 csproj 的 <Version> 对齐 —— 不一致会导致"更新完仍然提示有新版"
if (Test-Path $CsprojPath) {
    [xml]$proj = Get-Content $CsprojPath -Encoding UTF8
    $declared = $proj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if ($declared -and ($declared.Trim() -ne $Version)) {
        Write-Warning "清单版本 $Version 与 $CsprojPath 里的 <Version>$declared</Version> 不一致"
    }
}

# 更新源必须是 https(启动器会拒绝 http)
if ($BaseUrl -notmatch '^https://') {
    throw "BaseUrl 必须是 https 地址(启动器会拒绝 http),当前: $BaseUrl"
}

# 哈希:以 SHA-256 为准,SHA-1 仅保留给旧客户端
function Get-HashHex([string]$path, [string]$algo) {
    $h = [System.Security.Cryptography.HashAlgorithm]::Create($algo)
    $fs = [System.IO.File]::OpenRead($path)
    try { return [BitConverter]::ToString($h.ComputeHash($fs)).Replace('-', '').ToLowerInvariant() }
    finally { $fs.Dispose(); $h.Dispose() }
}

$sha256 = Get-HashHex $SetupPath 'SHA256'
$sha1 = Get-HashHex $SetupPath 'SHA1'
$bytes = (Get-Item $SetupPath).Length

$fileName = [System.IO.Path]::GetFileName($SetupPath)
$manifest = [ordered]@{
    version = $Version
    url     = "$BaseUrl/$fileName"
    sha256  = $sha256
    sha1    = $sha1
    size    = $bytes
    notes   = $Notes
}
$json = $manifest | ConvertTo-Json

if ($OutFile -eq "") { $OutFile = "dist\manifest.json" }
# 不写 BOM:带 BOM 的 JSON 会让一部分解析器直接抛错
[System.IO.File]::WriteAllText($OutFile, $json, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "manifest written: $OutFile"
Write-Host $json
Write-Host ""
Write-Host ("安装包: {0}  ({1:N2} MB)" -f $fileName, ($bytes / 1MB))
Write-Host "下一步:"
Write-Host "  1. 上传 $SetupPath 与 $OutFile 到 OSS 的对应前缀目录"
Write-Host "  2. 安装包设 Cache-Control: public, max-age=31536000, immutable(文件名带版本号,内容不会变)"
Write-Host "  3. manifest.json 设 Cache-Control: no-store(必须每次现取)"
Write-Host "  4. 确认 $BaseUrl/manifest.json 可公开访问且是 https"
