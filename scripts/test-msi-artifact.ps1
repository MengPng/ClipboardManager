param(
    [Parameter(Mandatory = $true)][string]$MsiPath,
    [Parameter(Mandatory = $true)][string]$ExpectedVersion,
    [Parameter(Mandatory = $true)][int]$ExpectedSchemaVersion
)
$ErrorActionPreference = "Stop"
$msi = (Resolve-Path $MsiPath).Path
if ((Get-Item $msi).Length -lt 10MB) { throw "MSI 文件异常偏小，可能未包含应用载荷：$msi" }
$temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) ("ClipboardManager.MsiProbe-" + [Guid]::NewGuid().ToString("N"))
$extractDirectory = Join-Path $temporaryDirectory "extracted"
$logPath = Join-Path $temporaryDirectory "msiexec.log"
New-Item -ItemType Directory -Path $extractDirectory -Force | Out-Null
try {
    $arguments = @('/a', $msi, '/qn', "TARGETDIR=$extractDirectory", '/L*v', $logPath)
    $process = Start-Process -FilePath "$env:SystemRoot\System32\msiexec.exe" -ArgumentList $arguments -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "MSI 管理解包失败，退出码 $($process.ExitCode)。日志：$logPath" }
    $executable = Get-ChildItem $extractDirectory -Filter ClipboardManager.exe -Recurse -File | Select-Object -First 1
    if ($null -eq $executable) { throw "MSI 中未找到 ClipboardManager.exe。" }
    & (Join-Path $PSScriptRoot 'test-release-artifact.ps1') -ExecutablePath $executable.FullName -ExpectedVersion $ExpectedVersion -ExpectedSchemaVersion $ExpectedSchemaVersion
    Write-Output "MSI artifact verified: $msi"
}
finally {
    if (Test-Path $temporaryDirectory) { Remove-Item $temporaryDirectory -Recurse -Force }
}
