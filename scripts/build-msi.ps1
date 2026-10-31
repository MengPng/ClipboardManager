param([string]$PackageVersion = "0.8.7")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $root "artifacts/publish/$PackageVersion/msi-single"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
# Single-file options live in the csproj so this payload matches the portable EXE byte for byte.
dotnet publish (Join-Path $root "src/ClipboardManager.Windows/ClipboardManager.Windows.csproj") --configuration Release -m:1 --runtime win-x64 --self-contained true -p:PublishSingleFile=true -p:NuGetAudit=false -p:UseSharedCompilation=false -p:Version=$PackageVersion --output $publish
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet publish (Join-Path $root "tools/ClipboardManager.Maintenance/ClipboardManager.Maintenance.csproj") --configuration Release -m:1 --runtime win-x64 --self-contained true -p:PublishSingleFile=true -p:NuGetAudit=false -p:UseSharedCompilation=false -p:Version=$PackageVersion --output $publish
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if (-not (Test-Path (Join-Path $publish "ClipboardManager.Maintenance.exe"))) { throw "发布未生成恢复助手。" }
dotnet clean (Join-Path $root "installer/ClipboardManager.Package.wixproj") --configuration Release -m:1 -p:PackageVersion=$PackageVersion | Out-Null
dotnet build (Join-Path $root "installer/ClipboardManager.Package.wixproj") --configuration Release -m:1 -p:UseSharedCompilation=false -p:PackageVersion=$PackageVersion
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$msi = Get-ChildItem (Join-Path $root "installer/bin") -Filter *.msi -Recurse | Select-Object -First 1
if (-not $msi) { throw "WiX 构建未生成 MSI。" }
Write-Output "Built $($msi.FullName)"
