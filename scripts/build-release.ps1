param(
    [string]$Version = "0.8.4",
    [switch]$IncludeMsi = $true
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$contractPath = Join-Path $root "release/release-contract.json"
$contract = Get-Content $contractPath -Raw | ConvertFrom-Json
if ($Version -ne $contract.appVersion) {
    throw "发布版本 $Version 与发布契约版本 $($contract.appVersion) 不一致。请先更新 release-contract.json。"
}
$releaseDir = Join-Path $root "artifacts/release/$Version"
$portableDir = Join-Path $releaseDir "ClipboardManager-$Version-win-x64"
$zipPath = Join-Path $releaseDir "ClipboardManager-$Version-win-x64-portable.zip"
$exePath = Join-Path $releaseDir "ClipboardManager-$Version-win-x64.exe"

if (Test-Path $releaseDir) { Remove-Item $releaseDir -Recurse -Force }
New-Item -ItemType Directory -Path $portableDir -Force | Out-Null

& (Join-Path $PSScriptRoot "publish-single-exe.ps1") -OutputPath "artifacts/release/$Version/ClipboardManager-$Version-win-x64/ClipboardManager.exe" -Version $Version
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& (Join-Path $PSScriptRoot "test-release-artifact.ps1") `
    -ExecutablePath (Join-Path $portableDir "ClipboardManager.exe") `
    -ExpectedVersion $contract.appVersion `
    -ExpectedSchemaVersion $contract.databaseSchemaVersion

$helperTarget = Join-Path $portableDir "ClipboardManager.Maintenance.exe"
if (-not (Test-Path $helperTarget)) { throw "便携包缺少恢复助手。" }

$manifest = [ordered]@{
    product = "ClipboardManager"
    version = $Version
    runtime = "win-x64"
    selfContained = $true
    files = @(
        [ordered]@{ name = "ClipboardManager.exe"; sha256 = (Get-FileHash (Join-Path $portableDir "ClipboardManager.exe") -Algorithm SHA256).Hash }
        [ordered]@{ name = "ClipboardManager.Maintenance.exe"; sha256 = (Get-FileHash $helperTarget -Algorithm SHA256).Hash }
    )
    createdAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $portableDir "release-manifest.json") -Encoding UTF8
Compress-Archive -Path (Join-Path $portableDir "*") -DestinationPath $zipPath -CompressionLevel Optimal
Copy-Item (Join-Path $portableDir "ClipboardManager.exe") $exePath -Force

$checksums = @(
    "$(Get-FileHash $exePath -Algorithm SHA256 | Select-Object -ExpandProperty Hash)  $(Split-Path $exePath -Leaf)"
    "$(Get-FileHash $zipPath -Algorithm SHA256 | Select-Object -ExpandProperty Hash)  $(Split-Path $zipPath -Leaf)"
)
if ($IncludeMsi) {
    & (Join-Path $PSScriptRoot "build-msi.ps1") -PackageVersion $Version
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $builtMsi = Get-ChildItem (Join-Path $root "installer/bin") -Filter "ClipboardManager-$Version-win-x64.msi" -Recurse | Select-Object -First 1
    if (-not $builtMsi) { throw "未找到版本 $Version 的 MSI。" }
    $msiTarget = Join-Path $releaseDir $builtMsi.Name
    Copy-Item $builtMsi.FullName $msiTarget -Force
    & (Join-Path $PSScriptRoot "test-msi-artifact.ps1") `
        -MsiPath $msiTarget `
        -ExpectedVersion $contract.appVersion `
        -ExpectedSchemaVersion $contract.databaseSchemaVersion
    $checksums += "$(Get-FileHash $msiTarget -Algorithm SHA256 | Select-Object -ExpandProperty Hash)  $(Split-Path $msiTarget -Leaf)"
}
$checksums | Set-Content (Join-Path $releaseDir "SHA256SUMS.txt") -Encoding ascii
Write-Output "Release created: $releaseDir"
