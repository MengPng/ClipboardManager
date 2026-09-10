$ErrorActionPreference = 'Stop'

function Get-TencentHexHash([string]$Value) {
    ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Value)))).ToLowerInvariant()
}

function Get-TencentHmac([byte[]]$Key, [string]$Value) {
    $hmac = [Security.Cryptography.HMACSHA256]::new($Key)
    try { $hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value)) } finally { $hmac.Dispose() }
}

function Invoke-TencentCloudJsonApi {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$Service,
        [Parameter(Mandatory)] [string]$Action,
        [Parameter(Mandatory)] [string]$Version,
        [string]$Region = '',
        [hashtable]$Body = @{}
    )

    if (-not $env:TENCENTCLOUD_SECRET_ID -or -not $env:TENCENTCLOUD_SECRET_KEY) {
        throw 'Tencent Cloud credential environment is not initialized.'
    }

    $hostName = "$Service.tencentcloudapi.com"
    $payload = $Body | ConvertTo-Json -Compress -Depth 12
    $now = [DateTimeOffset]::UtcNow
    $timestamp = $now.ToUnixTimeSeconds()
    $date = $now.ToString('yyyy-MM-dd')
    $canonical = "POST`n/`n`ncontent-type:application/json; charset=utf-8`nhost:$hostName`n`ncontent-type;host`n$(Get-TencentHexHash $payload)"
    $scope = "$date/$Service/tc3_request"
    $stringToSign = "TC3-HMAC-SHA256`n$timestamp`n$scope`n$(Get-TencentHexHash $canonical)"
    $dateKey = Get-TencentHmac ([Text.Encoding]::UTF8.GetBytes("TC3$env:TENCENTCLOUD_SECRET_KEY")) $date
    $serviceKey = Get-TencentHmac $dateKey $Service
    $signingKey = Get-TencentHmac $serviceKey 'tc3_request'
    $signature = ([Convert]::ToHexString((Get-TencentHmac $signingKey $stringToSign))).ToLowerInvariant()
    $authorization = "TC3-HMAC-SHA256 Credential=$env:TENCENTCLOUD_SECRET_ID/$scope, SignedHeaders=content-type;host, Signature=$signature"

    $client = [Net.Http.HttpClient]::new()
    try {
        $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post, "https://$hostName")
        $request.Content = [Net.Http.StringContent]::new($payload, [Text.Encoding]::UTF8, 'application/json')
        [void]$request.Headers.TryAddWithoutValidation('Authorization', $authorization)
        [void]$request.Headers.TryAddWithoutValidation('X-TC-Action', $Action)
        [void]$request.Headers.TryAddWithoutValidation('X-TC-Version', $Version)
        [void]$request.Headers.TryAddWithoutValidation('X-TC-Timestamp', "$timestamp")
        if ($Region) { [void]$request.Headers.TryAddWithoutValidation('X-TC-Region', $Region) }
        $response = $client.Send($request)
        $json = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
        if ($json.Response.Error) {
            throw "TencentApi:$($json.Response.Error.Code):$($json.Response.Error.Message)"
        }
        $json.Response
    }
    finally {
        $client.Dispose()
    }
}

Export-ModuleMember -Function Invoke-TencentCloudJsonApi
