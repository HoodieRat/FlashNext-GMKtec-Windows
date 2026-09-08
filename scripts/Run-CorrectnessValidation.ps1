[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$RuntimeDirectory,
    [Parameter(Mandatory=$true)][string]$ModelDirectory,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [int]$MtpNMax = 6,
    [int]$BatchSize = 2048,
    [int]$UbatchSize = 2048,
    [int]$Port = 18083,
    [string]$PythonExecutable = ''
)

$ErrorActionPreference = 'Stop'
$RuntimeDirectory = [IO.Path]::GetFullPath($RuntimeDirectory)
$ModelDirectory = [IO.Path]::GetFullPath($ModelDirectory)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

if ($MtpNMax -lt 1 -or $MtpNMax -gt 16) { throw 'MtpNMax must be 1 through 16.' }
if ($BatchSize -notin @(512,1024,2048,4096)) { throw 'BatchSize is outside the validated set.' }
if ($UbatchSize -notin @(256,512,1024,2048)) { throw 'UbatchSize is outside the validated set.' }

$server = Join-Path $RuntimeDirectory 'llama-server.exe'
$buildPath = Join-Path $RuntimeDirectory 'runtime.build.json'
$modelLockPath = Join-Path $ModelDirectory '.flashnext-model.lock.json'
$mainModel = Join-Path $ModelDirectory 'UD-Q4_K_XL\Qwen3.8-Flash-Next-UD-Q4_K_XL-00001-of-00004.gguf'
$draftModel = Join-Path $ModelDirectory 'MTP\mtp-Qwen3.8-Flash-Next-shared-Q8_0.gguf'
$projector = Join-Path $ModelDirectory 'mmproj-F16.gguf'
$chatTemplate = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\config\flashnext-chat.jinja'))
$clientScript = Join-Path $PSScriptRoot 'inference_validation_client.py'
foreach ($path in @($server,$buildPath,$modelLockPath,$mainModel,$draftModel,$projector,$chatTemplate,$clientScript)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required validation input is missing: $path" }
}
if ([string]::IsNullOrWhiteSpace($PythonExecutable)) {
    $managedPython = Join-Path $env:LOCALAPPDATA 'FlashNextManager\python-env\Scripts\python.exe'
    $PythonExecutable = if (Test-Path -LiteralPath $managedPython -PathType Leaf) { $managedPython } else { (Get-Command python -ErrorAction Stop).Source }
}

$build = Get-Content -LiteralPath $buildPath -Raw | ConvertFrom-Json
$shortCommit = ([string]$build.commit).Substring(0,12)
$stdout = Join-Path $OutputDirectory ("server-$shortCommit-correctness.stdout.log")
$stderr = Join-Path $OutputDirectory ("server-$shortCommit-correctness.stderr.log")
$keyFile = Join-Path $OutputDirectory '.correctness-api-key.txt'
$keyBytes = New-Object byte[] 24
$keyRng = [Security.Cryptography.RandomNumberGenerator]::Create()
try { $keyRng.GetBytes($keyBytes) } finally { $keyRng.Dispose() }
$key = 'correctness_' + ([BitConverter]::ToString($keyBytes).Replace('-', '').ToLowerInvariant())
[IO.File]::WriteAllText($keyFile, $key + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
$headers = @{ Authorization = 'Bearer ' + $key }
$arguments = @(
    '-m',('"' + $mainModel + '"'),'--device','Vulkan0','--fit','off','-ngl','99','-fa','on',
    '-ctk','q8_0','-ctv','q8_0','-b',[string]$BatchSize,'-ub',[string]$UbatchSize,'-t','16','-tb','16',
    '--load-mode','mmap','--ngram-on-disk','--ngram-io-threads','64','--ngram-cache','256','--ngram-direct-io',
    '--no-context-shift','--mmproj',('"' + $projector + '"'),'--image-min-tokens','1024','--image-max-tokens','4096',
    '-md',('"' + $draftModel + '"'),'--n-gpu-layers-draft','99','--spec-draft-type-k','q8_0','--spec-draft-type-v','q8_0',
    '--spec-type','draft-mtp','--spec-draft-p-min','0.75','--spec-draft-n-max',[string]$MtpNMax,
    '-c','131072','--host','127.0.0.1','--port',[string]$Port,'--api-key-file',('"' + $keyFile + '"'),
    '--metrics','--jinja','--no-agent','--no-ui','--cache-prompt','--parallel','1','--alias','Qwen3.8-Flash-Next',
    '--chat-template-file',('"' + $chatTemplate + '"')
)

$process = $null
try {
    $process = Start-Process -FilePath $server -WorkingDirectory $RuntimeDirectory -ArgumentList $arguments -RedirectStandardOutput $stdout -RedirectStandardError $stderr -WindowStyle Hidden -PassThru
    $deadline = [DateTimeOffset]::UtcNow.AddMinutes(20)
    $healthy = $false
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if ($process.HasExited) { throw "Server exited with code $($process.ExitCode). See $stderr" }
        try {
            $health = Invoke-WebRequest -UseBasicParsing -Uri ("http://127.0.0.1:{0}/health" -f $Port) -Headers $headers -TimeoutSec 5
            if ($health.StatusCode -eq 200) { $healthy = $true; break }
        } catch { Start-Sleep -Seconds 2 }
    }
    if (-not $healthy) { throw "Server did not become healthy. See $stderr" }

    & $PythonExecutable $clientScript `
        --base-url ("http://127.0.0.1:{0}" -f $Port) `
        --api-key-file $keyFile `
        --runtime-directory $RuntimeDirectory `
        --model-directory $ModelDirectory `
        --server-pid $process.Id `
        --output-directory $OutputDirectory `
        --mtp-n-max $MtpNMax `
        --batch-size $BatchSize `
        --ubatch-size $UbatchSize
    if ($LASTEXITCODE -ne 0) { throw "Correctness client failed with exit code $LASTEXITCODE. Partial report: $(Join-Path $OutputDirectory 'correctness-report.json')" }
}
finally {
    if ($null -ne $process) {
        try { Invoke-RestMethod -Method Post -Uri ("http://127.0.0.1:{0}/shutdown" -f $Port) -Headers $headers -TimeoutSec 5 | Out-Null } catch { }
        if (-not $process.WaitForExit(30000)) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
        $process.Dispose()
    }
    Remove-Item -LiteralPath $keyFile -Force -ErrorAction SilentlyContinue
}

Write-Host "Correctness report: $(Join-Path $OutputDirectory 'correctness-report.json')"
Write-Host "Runtime-tagged server logs: $stdout ; $stderr"
