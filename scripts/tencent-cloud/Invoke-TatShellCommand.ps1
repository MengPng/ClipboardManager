param(
    [Parameter(Mandatory)] [string]$InstanceId,
    [Parameter(Mandatory)] [string]$Command,
    [string]$Region = 'ap-guangzhou',
    [string]$Username = 'root',
    [int]$TimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\Import-TencentCloudCredential.ps1"
Import-Module "$PSScriptRoot\TencentCloudApi.psm1" -Force

try {
    $content = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Command))
    $run = Invoke-TencentCloudJsonApi -Service tat -Action RunCommand -Version '2020-10-28' -Region $Region -Body @{
        CommandName='clipboardmanager-deploy'
        SaveCommand=$false
        Description='ClipboardManager production deployment operation'
        Content=$content
        CommandType='SHELL'
        Username=$Username
        Timeout=$TimeoutSeconds
        InstanceIds=@($InstanceId)
    }

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds + 30)
    do {
        Start-Sleep -Seconds 2
        $tasks = Invoke-TencentCloudJsonApi -Service tat -Action DescribeInvocationTasks -Version '2020-10-28' -Region $Region -Body @{
            Filters=@(@{ Name='invocation-id'; Values=@([string]$run.InvocationId) })
            HideOutput=$false
            Limit=10
            Offset=0
        }
        $task = @($tasks.InvocationTaskSet)[0]
        if ($task.TaskStatus -in @('SUCCESS','FAILED','TIMEOUT')) { break }
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    if (-not $task) { throw 'TAT command produced no task.' }
    $decodedOutput = if ($task.TaskResult.Output) {
        [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String([string]$task.TaskResult.Output))
    } else { '' }
    [pscustomobject]@{
        InvocationId=[string]$run.InvocationId
        Status=[string]$task.TaskStatus
        ExitCode=$task.TaskResult.ExitCode
        Output=$decodedOutput
        ErrorInfo=[string]$task.ErrorInfo
    } | ConvertTo-Json -Depth 4
}
finally {
    Remove-Item Env:TENCENTCLOUD_SECRET_ID -ErrorAction SilentlyContinue
    Remove-Item Env:TENCENTCLOUD_SECRET_KEY -ErrorAction SilentlyContinue
    Remove-Item Env:TENCENTCLOUD_CAM_USER -ErrorAction SilentlyContinue
}
