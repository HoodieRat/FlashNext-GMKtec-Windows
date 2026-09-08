[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$RuntimeDirectory,
    [Parameter(Mandatory=$true)][string]$ModelDirectory,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [int]$Port = 18082,
    [int]$MeasuredRuns = 3,
    [int]$OutputTokens = 768
)

$ErrorActionPreference = 'Stop'
$RuntimeDirectory = [IO.Path]::GetFullPath($RuntimeDirectory)
$ModelDirectory = [IO.Path]::GetFullPath($ModelDirectory)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$server = Join-Path $RuntimeDirectory 'llama-server.exe'
$buildPath = Join-Path $RuntimeDirectory 'runtime.build.json'
$modelLockPath = Join-Path $ModelDirectory '.flashnext-model.lock.json'
$mainModel = Join-Path $ModelDirectory 'UD-Q4_K_XL\Qwen3.8-Flash-Next-UD-Q4_K_XL-00001-of-00004.gguf'
$draftModel = Join-Path $ModelDirectory 'MTP\mtp-Qwen3.8-Flash-Next-shared-Q8_0.gguf'
$projector = Join-Path $ModelDirectory 'mmproj-F16.gguf'
$chatTemplate = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\config\flashnext-chat.jinja'))
foreach ($path in @($server,$buildPath,$modelLockPath,$mainModel,$draftModel,$projector,$chatTemplate)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required validation input is missing: $path" }
}
if ($MeasuredRuns -lt 1 -or $MeasuredRuns -gt 10) { throw 'MeasuredRuns must be 1 through 10.' }
if ($OutputTokens -lt 128 -or $OutputTokens -gt 4096) { throw 'OutputTokens must be 128 through 4096.' }

function Get-Sha256Text([string]$Value) {
    $bytes = [Text.Encoding]::UTF8.GetBytes($Value)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $digest = $sha.ComputeHash($bytes) } finally { $sha.Dispose() }
    return [BitConverter]::ToString($digest).Replace('-', '').ToLowerInvariant()
}

function Get-Median($Values) {
    $items = @($Values | Where-Object { $null -ne $_ } | Sort-Object)
    if ($items.Count -eq 0) { return $null }
    $middle = [int][Math]::Floor($items.Count / 2)
    if (($items.Count % 2) -eq 1) { return [double]$items[$middle] }
    return ([double]$items[$middle - 1] + [double]$items[$middle]) / 2.0
}

function Save-Report($Value, [string]$Path) {
    $json = $Value | ConvertTo-Json -Depth 20
    [IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}

$build = Get-Content -LiteralPath $buildPath -Raw | ConvertFrom-Json
$modelLock = Get-Content -LiteralPath $modelLockPath -Raw | ConvertFrom-Json
$reportPath = Join-Path $OutputDirectory 'benchmark-report.json'
$keyFile = Join-Path $OutputDirectory '.benchmark-api-key.txt'
$keyBytes = New-Object byte[] 24
$keyRng = [Security.Cryptography.RandomNumberGenerator]::Create()
try { $keyRng.GetBytes($keyBytes) } finally { $keyRng.Dispose() }
$key = 'benchmark_' + ([BitConverter]::ToString($keyBytes).Replace('-', '').ToLowerInvariant())
[IO.File]::WriteAllText($keyFile, $key + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
$headers = @{ Authorization = 'Bearer ' + $key }
$runtimeManifestHash = (Get-FileHash -LiteralPath $buildPath -Algorithm SHA256).Hash.ToLowerInvariant()
$report = [ordered]@{
    timestampUtc = [DateTimeOffset]::UtcNow.ToString('o')
    runtime = [ordered]@{
        repository = [string]$build.repository
        commit = [string]$build.commit
        buildManifestSha256 = $runtimeManifestHash
        executable = $server
        patches = @($build.patches)
    }
    model = [ordered]@{
        repository = [string]$modelLock.repository
        revision = [string]$modelLock.revision
        firstShard = $mainModel
        draftModel = $draftModel
        projector = $projector
    }
    commonConfiguration = [ordered]@{
        contextSize = 131072
        batchSizesTested = @(2048,4096)
        ubatchSizesTested = @(512,2048)
        targetKv = 'q8_0/q8_0'
        draftKv = 'q8_0/q8_0'
        flashAttention = 'on'
        ple = 'on-demand NVMe DirectIO; 64 I/O threads; 256 MiB row cache'
        parallel = 1
        fit = 'off'
        contextShift = 'off'
        seed = 12345
        outputTokens = $OutputTokens
    }
    candidates = @()
    outputIdentity = $null
}
Save-Report $report $reportPath
$benchmarkCorpus = ((1..384 | ForEach-Object {
    "Record $_ describes a committed transaction, its write-ahead-log sequence number, the pages it dirtied, the checksum persisted before acknowledgement, and the recovery decision after a simulated power loss."
}) -join ' ')

try {
    foreach ($candidate in @(
        [ordered]@{ name = 'mtp-off'; specType = 'none'; nMax = 0; batch = 2048; ubatch = 2048 },
        [ordered]@{ name = 'mtp-4'; specType = 'draft-mtp'; nMax = 4; batch = 2048; ubatch = 2048 },
        [ordered]@{ name = 'mtp-6'; specType = 'draft-mtp'; nMax = 6; batch = 2048; ubatch = 2048 },
        [ordered]@{ name = 'mtp-6-b4096'; specType = 'draft-mtp'; nMax = 6; batch = 4096; ubatch = 2048 },
        [ordered]@{ name = 'mtp-6-ub512'; specType = 'draft-mtp'; nMax = 6; batch = 2048; ubatch = 512 }
    )) {
        $stdout = Join-Path $OutputDirectory ("server-{0}-{1}.stdout.log" -f $build.commit.Substring(0,12),$candidate.name)
        $stderr = Join-Path $OutputDirectory ("server-{0}-{1}.stderr.log" -f $build.commit.Substring(0,12),$candidate.name)
        $arguments = @(
            '-m',('"' + $mainModel + '"'),'--device','Vulkan0','--fit','off','-ngl','99','-fa','on',
            '-ctk','q8_0','-ctv','q8_0','-b',[string]$candidate.batch,'-ub',[string]$candidate.ubatch,'-t','16','-tb','16',
            '--load-mode','mmap','--ngram-on-disk','--ngram-io-threads','64','--ngram-cache','256','--ngram-direct-io',
            '--no-context-shift','--mmproj',('"' + $projector + '"'),'--image-min-tokens','1024','--image-max-tokens','4096'
        )
        if ($candidate.specType -eq 'draft-mtp') {
            $arguments += @('-md',('"' + $draftModel + '"'),'--n-gpu-layers-draft','99','--spec-draft-type-k','q8_0','--spec-draft-type-v','q8_0')
        }
        $arguments += @('--spec-type',$candidate.specType,'--spec-draft-p-min','0.75')
        if ($candidate.nMax -gt 0) { $arguments += @('--spec-draft-n-max',[string]$candidate.nMax) }
        $arguments += @('-c','131072','--host','127.0.0.1','--port',[string]$Port,'--api-key-file',('"' + $keyFile + '"'),'--metrics','--jinja','--no-agent','--no-ui','--cache-prompt','--parallel','1','--alias','Qwen3.8-Flash-Next','--chat-template-file',('"' + $chatTemplate + '"'))

        $process = Start-Process -FilePath $server -WorkingDirectory $RuntimeDirectory -ArgumentList $arguments -RedirectStandardOutput $stdout -RedirectStandardError $stderr -WindowStyle Hidden -PassThru
        $candidateResult = [ordered]@{
            name = $candidate.name
            runtimeCommit = [string]$build.commit
            runtimeExecutable = $server
            specType = $candidate.specType
            mtpNMax = $candidate.nMax
            batchSize = $candidate.batch
            ubatchSize = $candidate.ubatch
            launchArguments = @($arguments | ForEach-Object { $_.Trim('"') })
            stdoutLog = $stdout
            stderrLog = $stderr
            runs = @()
            medianPromptTokensPerSecond = $null
            medianGenerationTokensPerSecond = $null
            medianAcceptancePercent = $null
            stable = $false
        }
        $report.candidates += $candidateResult
        Save-Report $report $reportPath
        try {
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

            for ($run = 0; $run -le $MeasuredRuns; $run++) {
                $warmup = $run -eq 0
                $marker = if ($warmup) { 'warmup' } else { "measured-$run" }
                # Put the per-run marker before the corpus so each measured request has a cold,
                # non-cacheable bulk prefill while remaining byte-identical across candidates.
                $userPrompt = "Validation series $marker. Analyze the following deterministic recovery ledger. $benchmarkCorpus Produce a long, technically precise explanation of how a transactional key-value store preserves atomicity under crashes. Continue with concrete failure cases and recovery invariants until the token limit; do not conclude early."
                $body = [ordered]@{
                    model = 'Qwen3.8-Flash-Next'
                    messages = @(
                        [ordered]@{ role = 'system'; content = 'You are a deterministic benchmark generator. Follow the requested subject and continue until the token limit.' },
                        [ordered]@{ role = 'user'; content = $userPrompt }
                    )
                    stream = $false
                    temperature = 0.0
                    top_p = 1.0
                    top_k = 1
                    min_p = 0.0
                    presence_penalty = 0.0
                    repeat_penalty = 1.0
                    seed = 12345
                    max_tokens = $(if ($warmup) { 128 } else { $OutputTokens })
                    cache_prompt = $true
                    chat_template_kwargs = @{ enable_thinking = $false }
                    reasoning_effort = 'none'
                }
                $started = [Diagnostics.Stopwatch]::StartNew()
                $response = Invoke-RestMethod -Method Post -Uri ("http://127.0.0.1:{0}/v1/chat/completions" -f $Port) -Headers $headers -ContentType 'application/json' -Body ($body | ConvertTo-Json -Depth 8 -Compress) -TimeoutSec 3600
                $started.Stop()
                $content = [string]$response.choices[0].message.content
                $timings = $response.timings
                $drafted = if ($null -ne $timings.draft_n) { [long]$timings.draft_n } else { 0L }
                $accepted = if ($null -ne $timings.draft_n_accepted) { [long]$timings.draft_n_accepted } else { 0L }
                $candidateResult.runs += [ordered]@{
                    runNumber = $run
                    warmup = $warmup
                    runtimeCommit = [string]$build.commit
                    runtimeBuildManifestSha256 = $runtimeManifestHash
                    runtimeExecutable = $server
                    batchSize = $candidate.batch
                    ubatchSize = $candidate.ubatch
                    promptSha256 = Get-Sha256Text $userPrompt
                    outputSha256 = Get-Sha256Text $content
                    finishReason = [string]$response.choices[0].finish_reason
                    promptTokens = [int]$response.usage.prompt_tokens
                    evaluatedPromptTokens = $(if ($null -ne $timings.prompt_n) { [int]$timings.prompt_n } else { $null })
                    generatedTokens = [int]$response.usage.completion_tokens
                    cachedTokens = $(if ($null -ne $timings.cache_n) { [int]$timings.cache_n } else { 0 })
                    # llama-server's prompt_n is the evaluated (uncached) token count;
                    # cache_n is reported separately.
                    coldPromptTokens = $(if ($null -ne $timings.prompt_n) { [int]$timings.prompt_n } else { $null })
                    promptTokensPerSecond = $(if ($null -ne $timings.prompt_per_second) { [double]$timings.prompt_per_second } else { $null })
                    generationTokensPerSecond = $(if ($null -ne $timings.predicted_per_second) { [double]$timings.predicted_per_second } else { $null })
                    draftedTokens = $drafted
                    acceptedTokens = $accepted
                    acceptancePercent = $(if ($drafted -gt 0) { $accepted * 100.0 / $drafted } else { $null })
                    elapsedMilliseconds = [Math]::Round($started.Elapsed.TotalMilliseconds,3)
                }
                Save-Report $report $reportPath
            }
            $measured = @($candidateResult.runs | Where-Object { -not $_.warmup })
            $candidateResult.medianPromptTokensPerSecond = Get-Median @($measured | ForEach-Object { $_.promptTokensPerSecond })
            $candidateResult.medianGenerationTokensPerSecond = Get-Median @($measured | ForEach-Object { $_.generationTokensPerSecond })
            $candidateResult.medianAcceptancePercent = Get-Median @($measured | ForEach-Object { $_.acceptancePercent })
            $candidateResult.stable = ($measured.Count -eq $MeasuredRuns -and @($measured | Where-Object { $_.generatedTokens -lt [Math]::Min(128,$OutputTokens) -or $null -eq $_.generationTokensPerSecond }).Count -eq 0)
        }
        finally {
            try { Invoke-RestMethod -Method Post -Uri ("http://127.0.0.1:{0}/shutdown" -f $Port) -Headers $headers -TimeoutSec 5 | Out-Null } catch { }
            if (-not $process.WaitForExit(30000)) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
            $process.Dispose()
            Save-Report $report $reportPath
        }
    }

    $identity = @()
    foreach ($run in 1..$MeasuredRuns) {
        $rows = @($report.candidates | ForEach-Object { $_.runs | Where-Object { -not $_.warmup -and $_.runNumber -eq $run } })
        $identity += [ordered]@{ runNumber = $run; hashes = @($rows.outputSha256); identicalAcrossCandidates = (@($rows.outputSha256 | Select-Object -Unique).Count -eq 1) }
    }
    $report.outputIdentity = $identity
    Save-Report $report $reportPath
    Write-Host "Validation report: $reportPath"
}
finally {
    Remove-Item -LiteralPath $keyFile -Force -ErrorAction SilentlyContinue
}
