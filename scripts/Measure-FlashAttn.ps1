# Compares llama-server --flash-attn on vs auto. Skips (exit 0) if inference is not already healthy.
# Does not start the model. Requires FlashNext Dashboard control API on 127.0.0.1:18081 to persist extras.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'

function Get-FlashNextApiKey {
    if (-not [string]::IsNullOrWhiteSpace($env:FLASHNEXT_API_KEY)) { return $env:FLASHNEXT_API_KEY.Trim() }
    $path = Join-Path $env:LOCALAPPDATA 'FlashNextManager\api-key.txt'
    if (-not (Test-Path -LiteralPath $path)) { throw "API key file missing: $path" }
    $value = (Get-Content -LiteralPath $path -Raw).Trim()
    if ($value.Length -lt 32) { throw 'FlashNext API key is invalid.' }
    return $value
}

function Invoke-FlashNextJson {
    param([string]$Method, [string]$Url, [string]$Key, [object]$Body, [int]$TimeoutSec = 30)
    $headers = @{ Authorization = "Bearer $Key" }
    $params = @{ Method = $Method; Uri = $Url; Headers = $headers; TimeoutSec = $TimeoutSec }
    if ($null -ne $Body) {
        $params.ContentType = 'application/json; charset=utf-8'
        $params.Body = ($Body | ConvertTo-Json -Compress -Depth 20)
    }
    return Invoke-RestMethod @params
}

function Test-FlashNextHealthy {
    param([string]$Key)
    try {
        $response = Invoke-WebRequest -UseBasicParsing -Method GET -Uri 'http://127.0.0.1:8080/health' -Headers @{ Authorization = "Bearer $Key" } -TimeoutSec 5
        return [int]$response.StatusCode -ge 200 -and [int]$response.StatusCode -lt 300
    } catch {
        return $false
    }
}

function Invoke-FlashNextProbe {
    param([string]$Key)
    $payload = @{
        model = 'Qwen3.8-Flash-Next'
        messages = @(@{ role = 'user'; content = 'Reply with the single word ok.' })
        max_tokens = 64
        temperature = 0
        stream = $false
        chat_template_kwargs = @{ enable_thinking = $false }
    }
    $result = Invoke-FlashNextJson -Method POST -Url 'http://127.0.0.1:8080/v1/chat/completions' -Key $Key -Body $payload -TimeoutSec 180
    $tps = $null
    if ($result.timings) {
        if ($result.timings.predicted_per_second) { $tps = [double]$result.timings.predicted_per_second }
        elseif ($result.timings.tokens_per_second) { $tps = [double]$result.timings.tokens_per_second }
    }
    return [pscustomobject]@{ TokensPerSecond = $tps; Content = [string]$result.choices[0].message.content }
}

$key = Get-FlashNextApiKey
if (-not (Test-FlashNextHealthy -Key $key)) {
    Write-Output 'SKIP: inference /health is not up. Not starting the model. Re-run Measure-FlashAttn.ps1 when the header says Running.'
    exit 0
}

try { $null = Invoke-FlashNextJson -Method GET -Url 'http://127.0.0.1:18081/flashnext/status' -Key $key -TimeoutSec 5 }
catch {
    Write-Output 'SKIP: control API on 127.0.0.1:18081 is not up (start FlashNext Dashboard). Not changing extras.'
    exit 0
}

Write-Output 'Measuring baseline (flash-attn auto)...'
$before = Invoke-FlashNextProbe -Key $key
Write-Output ("baseline tok/s=" + $before.TokensPerSecond)

$settings = Invoke-FlashNextJson -Method GET -Url 'http://127.0.0.1:18081/flashnext/settings' -Key $key
$extras = @()
if ($settings.server.extraArguments) { $extras = @($settings.server.extraArguments) }
$hadFlash = $false
for ($i = 0; $i -lt $extras.Count; $i++) {
    if ($extras[$i] -eq '--flash-attn') { $hadFlash = $true; break }
}
if ($hadFlash) {
    Write-Output 'SKIP: --flash-attn is already in extraArguments. Not restarting to re-measure.'
    exit 0
}

$withFlash = @($extras + @('--flash-attn', 'on'))
Write-Output 'PATCH extraArguments --flash-attn on and restart (model reload)...'
$null = Invoke-FlashNextJson -Method PATCH -Url 'http://127.0.0.1:18081/flashnext/settings' -Key $key -Body @{ server = @{ extraArguments = $withFlash } }
$null = Invoke-FlashNextJson -Method POST -Url 'http://127.0.0.1:18081/flashnext/server/restart' -Key $key -TimeoutSec 900

$deadline = (Get-Date).AddSeconds(900)
while ((Get-Date) -lt $deadline) {
    if (Test-FlashNextHealthy -Key $key) { break }
    Start-Sleep -Seconds 2
}
if (-not (Test-FlashNextHealthy -Key $key)) {
    Write-Output 'FAIL: server did not become healthy after enabling --flash-attn on. Reverting extras.'
    $null = Invoke-FlashNextJson -Method PATCH -Url 'http://127.0.0.1:18081/flashnext/settings' -Key $key -Body @{ server = @{ extraArguments = $extras } }
    $null = Invoke-FlashNextJson -Method POST -Url 'http://127.0.0.1:18081/flashnext/server/restart' -Key $key -TimeoutSec 900
    exit 1
}

Write-Output 'Measuring --flash-attn on...'
try { $after = Invoke-FlashNextProbe -Key $key }
catch {
    Write-Output ("FAIL: probe after flash-attn on failed: " + $_.Exception.Message + " Reverting extras.")
    $null = Invoke-FlashNextJson -Method PATCH -Url 'http://127.0.0.1:18081/flashnext/settings' -Key $key -Body @{ server = @{ extraArguments = $extras } }
    $null = Invoke-FlashNextJson -Method POST -Url 'http://127.0.0.1:18081/flashnext/server/restart' -Key $key -TimeoutSec 900
    exit 1
}
Write-Output ("flash-attn on tok/s=" + $after.TokensPerSecond)

$keep = $false
if ($null -ne $before.TokensPerSecond -and $null -ne $after.TokensPerSecond -and $after.TokensPerSecond -ge ($before.TokensPerSecond * 1.05)) { $keep = $true }
if ($keep) {
    Write-Output 'KEEP: --flash-attn on improved tok/s by at least 5%. extraArguments left as-is.'
    exit 0
}

Write-Output 'REVERT: no 5% tok/s gain (or missing timings). Restoring previous extraArguments and restarting.'
$null = Invoke-FlashNextJson -Method PATCH -Url 'http://127.0.0.1:18081/flashnext/settings' -Key $key -Body @{ server = @{ extraArguments = $extras } }
$null = Invoke-FlashNextJson -Method POST -Url 'http://127.0.0.1:18081/flashnext/server/restart' -Key $key -TimeoutSec 900
exit 0
