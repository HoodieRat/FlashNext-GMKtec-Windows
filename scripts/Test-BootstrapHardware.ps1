[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ReportPath,
    [string]$SourceRoot,
    [string]$ParentLogPath,
    [string]$ProbePath,
    [int]$TimeoutSeconds = 15
)

. (Join-Path $PSScriptRoot 'Common.ps1')
Initialize-FlashNextLogging -Root (Get-FlashNextUserRoot) -Name 'bootstrap-hardware' -MirrorPath $ParentLogPath
$script:BootstrapHardwareStage = 'startup'

function Set-BootstrapHardwareStage {
    param([Parameter(Mandatory=$true)][string]$Name)
    $script:BootstrapHardwareStage = $Name
    Write-FlashNextLog ("Bootstrap hardware stage: {0}" -f $Name)
}

function Resolve-FlashNextVulkanProbePath {
    param([string]$Preferred, [string]$RepositoryRoot)
    $candidates = New-Object System.Collections.Generic.List[string]
    if (-not [string]::IsNullOrWhiteSpace($Preferred)) { [void]$candidates.Add($Preferred) }
    if (-not [string]::IsNullOrWhiteSpace($RepositoryRoot)) {
        [void]$candidates.Add((Join-Path $RepositoryRoot 'bootstrap\FlashNext.VulkanProbe.exe'))
        [void]$candidates.Add((Join-Path $RepositoryRoot 'src\FlashNext.VulkanProbe\bin\Release\net10.0\win-x64\publish\FlashNext.VulkanProbe.exe'))
    }
    [void]$candidates.Add((Join-Path $env:ProgramFiles 'FlashNextManager\app\current\FlashNext.VulkanProbe.exe'))
    foreach ($candidate in $candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
        try {
            $full = [System.IO.Path]::GetFullPath($candidate)
            if (Test-Path -LiteralPath $full -PathType Leaf) { return $full }
        } catch { }
    }
    return $null
}

function Assert-FlashNextBootstrapProbeHash {
    param([Parameter(Mandatory=$true)][string]$ProbeFile, [Parameter(Mandatory=$true)][string]$LockPath)
    if (-not (Test-Path -LiteralPath $LockPath -PathType Leaf)) { throw "Bootstrap lock manifest is missing: $LockPath" }
    $lock = Read-FlashNextJson -Path $LockPath
    if (-not $lock -or -not $lock.sha256 -or -not $lock.bytes) { throw "bootstrap.lock.json is missing sha256/bytes." }
    $info = Get-Item -LiteralPath $ProbeFile -Force
    if ([int64]$info.Length -ne [int64]$lock.bytes) {
        throw ("The bootstrap Vulkan probe size is {0} bytes; bootstrap.lock.json requires {1} bytes. Restore the original source package and rerun install.cmd." -f $info.Length, $lock.bytes)
    }
    $actual = Get-FlashNextFileSha256 -Path $ProbeFile
    if (-not $actual.Equals([string]$lock.sha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw ("The bootstrap Vulkan probe SHA-256 is {0}; bootstrap.lock.json requires {1}. Restore the original source package and rerun install.cmd." -f $actual, $lock.sha256)
    }
}

try {
    Set-BootstrapHardwareStage -Name 'input-validation'
    $ReportPath = [System.IO.Path]::GetFullPath($ReportPath)
    if ([string]::IsNullOrWhiteSpace($SourceRoot)) { $SourceRoot = Get-FlashNextRepositoryRoot -ScriptDirectory $PSScriptRoot }
    $SourceRoot = [System.IO.Path]::GetFullPath($SourceRoot)
    $lockPath = Join-Path $SourceRoot 'manifests\bootstrap.lock.json'
    $probe = Resolve-FlashNextVulkanProbePath -Preferred $ProbePath -RepositoryRoot $SourceRoot
    if (-not $probe) { throw 'FlashNext.VulkanProbe.exe was not found in bootstrap/. Restore the 1.0.5 source package and rerun install.cmd.' }

    Set-BootstrapHardwareStage -Name 'bootstrap-hash'
    Assert-FlashNextBootstrapProbeHash -ProbeFile $probe -LockPath $lockPath
    Write-FlashNextLog ("Verified bootstrap Vulkan probe '{0}'." -f $probe)

    Set-BootstrapHardwareStage -Name 'direct-vulkan-memory-probe'
    $timeoutMs = [Math]::Max(250, [int]($TimeoutSeconds * 1000))
    $probeResult = Invoke-FlashNextNative -FilePath $probe -Arguments @('--timeout-ms', ([string]$timeoutMs)) -WorkingDirectory (Split-Path -Parent $probe) -PassThru -IgnoreExitCode -TimeoutSeconds $TimeoutSeconds -HeartbeatSeconds 5 -Stage $script:BootstrapHardwareStage
    $jsonText = [string]$probeResult.StandardOutput
    if ([string]::IsNullOrWhiteSpace($jsonText) -or $jsonText -notmatch '\{') {
        throw ("The Vulkan memory probe produced no JSON. stderr tail: {0}" -f (Get-FlashNextTextTail -Text ([string]$probeResult.StandardError) -MaximumLines 40 -MaximumCharacters 8000))
    }
    $start = $jsonText.IndexOf('{')
    $end = $jsonText.LastIndexOf('}')
    if ($start -lt 0 -or $end -le $start) { throw 'The Vulkan memory probe output did not contain a JSON object.' }
    $document = $jsonText.Substring($start, ($end - $start + 1)) | ConvertFrom-Json
    if (-not $document) { throw 'The Vulkan memory probe JSON could not be parsed.' }

    Set-BootstrapHardwareStage -Name 'device-validation'
    $devices = @()
    if ($document.devices) { $devices = @($document.devices) }
    $deviceNames = @($devices | ForEach-Object { [string]$_.name } | Where-Object { $_ })
    $target = $null
    foreach ($device in $devices) {
        $flag = $false
        if ($device.PSObject.Properties.Name -contains 'isTargetRadeon8060S') { $flag = [bool]$device.isTargetRadeon8060S }
        if ($flag -or (Test-FlashNextGpuTarget -Names @([string]$device.name))) { $target = $device; break }
    }
    if (-not $target) {
        $joined = if ($deviceNames.Count -gt 0) { $deviceNames -join '; ' } else { 'none' }
        throw ("Vulkan did not enumerate the Radeon 8060S. Devices: {0}" -f $joined)
    }

    Set-BootstrapHardwareStage -Name 'memory-heap-validation'
    [UInt64]$largestHeap = 0
    if ($target.PSObject.Properties.Name -contains 'largestDeviceLocalHeapBytes' -and $null -ne $target.largestDeviceLocalHeapBytes) {
        $largestHeap = [UInt64]$target.largestDeviceLocalHeapBytes
    }
    if ($largestHeap -eq 0 -and $document.PSObject.Properties.Name -contains 'largestTargetDeviceLocalHeapBytes') {
        $largestHeap = [UInt64]$document.largestTargetDeviceLocalHeapBytes
    }
    if ($largestHeap -eq 0) { throw 'The Vulkan memory probe did not report a DEVICE_LOCAL heap size for the Radeon 8060S.' }
    [UInt64]$requiredHeap = [UInt64](90GB)
    $passes = ($largestHeap -ge $requiredHeap)
    $failureMessage = if ($passes) { $null } else { ("The Radeon 8060S Vulkan DEVICE_LOCAL heap is {0:N1} GiB; the factory configuration requires at least 90 GiB. Set the GMKtec EVO-X2 BIOS UMA frame buffer to 96 GB, save the BIOS setting, reboot Windows, and rerun install.cmd. The installer will not change BIOS settings." -f ([double]$largestHeap / 1GB)) }

    $report = [pscustomobject]@{
        schemaVersion = 2
        status = if ($passes) { 'succeeded' } else { 'failed' }
        message = $failureMessage
        projectRevision = '1.0.5'
        probeKind = 'direct-vulkan-memory-probe'
        completedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        stage = $script:BootstrapHardwareStage
        probePath = $probe
        loaderPath = [string]$document.loaderPath
        probeExitCode = [int]$probeResult.ExitCode
        probeTimedOut = [bool]$probeResult.TimedOut
        probeProcessId = [int]$probeResult.ProcessId
        devices = $deviceNames
        targetDeviceName = [string]$target.name
        targetDeviceFound = $true
        largestDeviceLocalHeapBytes = $largestHeap
        largestDeviceLocalHeapGiB = [Math]::Round(([double]$largestHeap / 1GB), 2)
        requiredDeviceLocalHeapBytes = $requiredHeap
        requiredDeviceLocalHeapGiB = 90
        passesFactoryUmaRequirement = $passes
        outputTail = Get-FlashNextTextTail -Text (($probeResult.StandardOutput + [Environment]::NewLine + $probeResult.StandardError).Trim()) -MaximumLines 80 -MaximumCharacters 16000
        logPath = $script:FlashNextLogPath
    }
    Write-FlashNextJsonAtomic -Path $ReportPath -Value $report
    if (-not $passes) { throw $failureMessage }

    Set-BootstrapHardwareStage -Name 'complete'
    Write-FlashNextLog ("Direct Vulkan memory probe passed. Device: {0}; largest DEVICE_LOCAL heap: {1:N1} GiB." -f [string]$target.name, ([double]$largestHeap / 1GB))
}
catch {
    $failure = $_
    $message = $failure.Exception.Message
    Write-FlashNextLog -Message ("Bootstrap hardware stage '{0}' failed: {1}" -f $script:BootstrapHardwareStage, $message) -Level 'ERROR'
    $existing = Read-FlashNextJson -Path $ReportPath
    if (-not $existing -or [string]$existing.status -ne 'failed') {
        $report = [pscustomobject]@{
            schemaVersion = 2
            status = 'failed'
            projectRevision = '1.0.5'
            probeKind = 'direct-vulkan-memory-probe'
            stage = $script:BootstrapHardwareStage
            message = $message
            exceptionType = $failure.Exception.GetType().FullName
            scriptStackTrace = $failure.ScriptStackTrace
            logPath = $script:FlashNextLogPath
            timestampUtc = [DateTimeOffset]::UtcNow.ToString('o')
        }
        Write-FlashNextJsonAtomic -Path $ReportPath -Value $report
    }
    throw
}
