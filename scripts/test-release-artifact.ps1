param(
    [Parameter(Mandatory = $true)][string]$ExecutablePath,
    [Parameter(Mandatory = $true)][string]$ExpectedVersion,
    [Parameter(Mandatory = $true)][int]$ExpectedSchemaVersion
)
$ErrorActionPreference = "Stop"
$executable = (Resolve-Path $ExecutablePath).Path
$declaredVersion = (Get-Item $executable).VersionInfo.FileVersion
$declaredSemanticVersion = ([version]$declaredVersion).ToString(3)
$expectedSemanticVersion = ([version]$ExpectedVersion).ToString(3)
if ($declaredSemanticVersion -ne $expectedSemanticVersion) {
    throw "最终 EXE 文件版本为 $declaredVersion，预期为 $ExpectedVersion；拒绝启动旧产物。"
}
$temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) ("ClipboardManager.ReleaseProbe-" + [Guid]::NewGuid().ToString("N"))
$resultPath = Join-Path $temporaryDirectory "release-info.json"
New-Item -ItemType Directory -Path $temporaryDirectory -Force | Out-Null
try {
    $process = Start-Process -FilePath $executable -ArgumentList "--write-release-info=$resultPath" -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "发布探针退出码为 $($process.ExitCode)：$executable" }
    if (-not (Test-Path $resultPath)) { throw "最终 EXE 未生成发布信息：$executable" }
    $actual = Get-Content $resultPath -Raw | ConvertFrom-Json
    if ($actual.product -ne "ClipboardManager") { throw "最终 EXE 产品标识错误：$($actual.product)" }
    if ($actual.version -ne $ExpectedVersion) { throw "最终 EXE 版本为 $($actual.version)，预期为 $ExpectedVersion。" }
    if ([int]$actual.databaseSchemaVersion -ne $ExpectedSchemaVersion) {
        throw "最终 EXE 支持 schema $($actual.databaseSchemaVersion)，预期为 $ExpectedSchemaVersion。"
    }
    Write-Output "Release artifact verified: version=$($actual.version), schema=$($actual.databaseSchemaVersion), file=$executable"
}
finally {
    if (Test-Path $temporaryDirectory) { Remove-Item $temporaryDirectory -Recurse -Force }
}
