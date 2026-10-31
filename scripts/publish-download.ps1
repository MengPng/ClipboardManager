#Requires -Version 7.2
<#
.SYNOPSIS
  把 artifacts/release/<Version> 的产物发布到下载页，并更新 latest.json / releases.json。

.EXAMPLE
  # 只在本地生成，检查 artifacts/download-site/ 后再上传
  .\scripts\publish-download.ps1 -DryRun

  # 生成并上传到服务器（需要已配置 SSH 密钥）
  .\scripts\publish-download.ps1 -SshTarget ubuntu@<服务器IP>

  # 只改了页面（site/download），不发新版本
  .\scripts\publish-download.ps1 -PageOnly
#>
param(
    [string]$Version,
    [string]$BaseUrl = "https://clipboard.netkaize.com/download/",
    [string]$SshTarget = $env:CM_DEPLOY_SSH,
    [string]$SshKey = $env:CM_DEPLOY_KEY,
    [string]$RemoteRoot = "/var/www/download",
    [switch]$IncludeMsi,
    [switch]$PageOnly,
    [switch]$DryRun
)
$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $true

$root = Split-Path -Parent $PSScriptRoot
$sshArgs = if ($SshKey) { @('-i', $SshKey) } else { @() }

if ($PageOnly) {
    if (-not $SshTarget) { throw "上传需要 -SshTarget 或环境变量 CM_DEPLOY_SSH。" }
    $site = Join-Path $root "site/download"
    ssh @sshArgs $SshTarget "mkdir -p '$RemoteRoot'"
    scp @sshArgs -r (Join-Path $site "index.html") (Join-Path $site "assets") "${SshTarget}:$RemoteRoot/"
    Write-Output "已更新下载页面（未改动 latest.json / releases.json）。"
    return
}
$contract = Get-Content (Join-Path $root "release/release-contract.json") -Raw | ConvertFrom-Json
if (-not $Version) { $Version = $contract.appVersion }
if ($Version -ne $contract.appVersion) { throw "发布版本 $Version 与发布契约版本 $($contract.appVersion) 不一致。" }
if (-not $DryRun -and -not $SshTarget) { throw "上传需要 -SshTarget 或环境变量 CM_DEPLOY_SSH（例如 ubuntu@1.2.3.4）；只生成文件请加 -DryRun。" }
$BaseUrl = $BaseUrl.TrimEnd('/') + '/'

$releaseDir = Join-Path $root "artifacts/release/$Version"
$sums = @{}
Get-Content (Join-Path $releaseDir "SHA256SUMS.txt") | Where-Object { $_.Trim() } | ForEach-Object {
    $hash, $name = $_ -split '\s+', 2
    $sums[$name.Trim()] = $hash.ToLowerInvariant()
}

$names = [ordered]@{
    exe = "ClipboardManager-$Version-win-x64.exe"
    zip = "ClipboardManager-$Version-win-x64-portable.zip"
}
if ($IncludeMsi) { $names.msi = "ClipboardManager-$Version-win-x64.msi" }

$files = @(foreach ($kind in $names.Keys) {
    $name = $names[$kind]
    $path = Join-Path $releaseDir $name
    $actual = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($sums[$name] -ne $actual) { throw "$name 的 SHA-256 与 SHA256SUMS.txt 不一致，停止发布。" }
    [ordered]@{
        kind   = $kind
        name   = $name
        url    = "${BaseUrl}files/$Version/$name"
        size   = (Get-Item $path).Length
        sha256 = $actual
    }
})
$localPaths = @($names.Values | ForEach-Object { Join-Path $releaseDir $_ })

# 更新说明：优先用面向用户的 release/notes/<Version>.md（"# 标题" + "- 条目"）；
# 没有时退回 CHANGELOG 中 "### <Version> 标题" 小节的一级条目，每条保留首句。
$notesPath = Join-Path $root "release/notes/$Version.md"
$source = if (Test-Path $notesPath) { Get-Content $notesPath } else { Get-Content (Join-Path $root "CHANGELOG.md") }
$headingPattern = if (Test-Path $notesPath) { '^#\s+(.+)$' } else { "^###\s+$([regex]::Escape($Version))\s+(.+)$" }
$start = $source | Select-String -Pattern $headingPattern | Select-Object -First 1
if (-not $start) { throw "找不到 $Version 的更新说明：请写 release/notes/$Version.md，或在 CHANGELOG.md 中加 '### $Version <标题>'。" }
$title = $start.Matches[0].Groups[1].Value.Trim()
$notes = @()
foreach ($line in $source[$start.LineNumber..($source.Count - 1)]) {
    if ($line -match '^#{2,3}\s') { break }
    if ($line -notmatch '^- (.+)$') { continue }
    $text = ($Matches[1] -replace '\*\*|`', '').Trim()
    $first = ($text -split '(?<=。)', 2)[0]
    if ($first.Length -gt 140) { $first = $first.Substring(0, 140) + '…' }
    $notes += $first
}

$releasedAt = [DateTimeOffset]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
$entry = [ordered]@{ version = $Version; releasedAt = $releasedAt; title = $title; notes = @($notes); files = $files }
$latest = [ordered]@{ schema = 1; product = "ClipboardManager"; pageUrl = $BaseUrl }
foreach ($key in $entry.Keys) { $latest[$key] = $entry[$key] }

# releases.json 以服务器上的现有内容为准：替换同版本条目并放到最前。
# 用 System.Text.Json 读写，避免 ConvertFrom-Json 把日期字符串转成 DateTime。
$history = [System.Text.Json.Nodes.JsonArray]::new()
$response = Invoke-WebRequest "${BaseUrl}releases.json" -SkipHttpErrorCheck -Headers @{ 'Cache-Control' = 'no-cache' }
if ($response.StatusCode -eq 200) {
    $history = [System.Text.Json.Nodes.JsonNode]::Parse([string]$response.Content).AsArray()
} elseif ($response.StatusCode -ne 404) {
    throw "读取现有 releases.json 失败：HTTP $($response.StatusCode)"
}
for ($i = $history.Count - 1; $i -ge 0; $i--) {
    if ([string]$history[$i]['version'] -eq $Version) { $history.RemoveAt($i) }
}
$history.Insert(0, [System.Text.Json.Nodes.JsonNode]::Parse(($entry | ConvertTo-Json -Depth 6)))

$jsonOptions = [System.Text.Json.JsonSerializerOptions]::new()
$jsonOptions.WriteIndented = $true
$jsonOptions.Encoder = [System.Text.Encodings.Web.JavaScriptEncoder]::UnsafeRelaxedJsonEscaping

$stage = Join-Path $root "artifacts/download-site"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
Copy-Item (Join-Path $root "site/download") $stage -Recurse
$utf8 = [System.Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText((Join-Path $stage "latest.json"),
    [System.Text.Json.Nodes.JsonNode]::Parse(($latest | ConvertTo-Json -Depth 6)).ToJsonString($jsonOptions), $utf8)
[IO.File]::WriteAllText((Join-Path $stage "releases.json"), $history.ToJsonString($jsonOptions), $utf8)
Write-Output "已生成：$stage（latest.json / releases.json / 页面）"

if ($DryRun) { Write-Output "DryRun：未上传。"; return }

$remoteFiles = "$RemoteRoot/files/$Version"
ssh @sshArgs $SshTarget "mkdir -p '$remoteFiles' '$RemoteRoot/assets'"
# 顺序：安装包 → 页面 → releases.json → latest.json（最后切换，客户端不会读到指向未上传文件的清单）
scp @sshArgs @localPaths "${SshTarget}:$remoteFiles/"
scp @sshArgs -r (Join-Path $stage "index.html") (Join-Path $stage "assets") "${SshTarget}:$RemoteRoot/"
scp @sshArgs (Join-Path $stage "releases.json") "${SshTarget}:$RemoteRoot/releases.json.tmp"
scp @sshArgs (Join-Path $stage "latest.json") "${SshTarget}:$RemoteRoot/latest.json.tmp"
ssh @sshArgs $SshTarget "mv '$RemoteRoot/releases.json.tmp' '$RemoteRoot/releases.json' && mv '$RemoteRoot/latest.json.tmp' '$RemoteRoot/latest.json'"

$check = Invoke-RestMethod "${BaseUrl}latest.json" -Headers @{ 'Cache-Control' = 'no-cache' }
if ($check.version -ne $Version) { throw "上传后 latest.json 仍为 $($check.version)，请检查 nginx 配置与 $RemoteRoot。" }
Write-Output "已发布 $Version → $BaseUrl"
