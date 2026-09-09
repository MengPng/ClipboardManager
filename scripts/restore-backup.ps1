param(
    [Parameter(Mandatory = $true)][string]$Backup,
    [switch]$ConfirmRestore
)
$ErrorActionPreference = "Stop"
if (-not $ConfirmRestore) { throw "恢复会替换本地数据库、设置和托管附件。确认后请加 -ConfirmRestore。" }
if (Get-Process ClipboardManager -ErrorAction SilentlyContinue) { throw "ClipboardManager 仍在运行。请先从托盘退出，再恢复备份。" }
$root = Split-Path -Parent $PSScriptRoot
$tool = Join-Path $root "tools/ClipboardManager.Maintenance/ClipboardManager.Maintenance.csproj"
if (-not (Test-Path $tool)) { throw "维护工具尚未生成。" }
dotnet run --project $tool -- restore (Resolve-Path $Backup)
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
