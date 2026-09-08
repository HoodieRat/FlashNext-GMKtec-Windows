[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ApplicationRoot,
    [Parameter(Mandatory=$true)][string]$StagingRoot,
    [Parameter(Mandatory=$true)][string]$BuildResultPath,
    [Parameter(Mandatory=$true)][string]$ResultPath,
    [string]$ParentLogPath
)

. (Join-Path $PSScriptRoot 'Common.ps1')
Initialize-FlashNextLogging -Root (Get-FlashNextMachineRoot) -Name 'machine-deploy' -MirrorPath $ParentLogPath
$script:MachineInstallStage = 'startup'

function Set-MachineInstallStage {
    param([Parameter(Mandatory=$true)][string]$Name)
    $script:MachineInstallStage = $Name
    Write-FlashNextLog ("Machine deployment stage: {0}" -f $Name)
}

function Test-FlashNextPathContainedBy {
    param(
        [Parameter(Mandatory=$true)][string]$Candidate,
        [Parameter(Mandatory=$true)][string]$Root
    )
    $candidateFull = [System.IO.Path]::GetFullPath($Candidate)
    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    return $candidateFull.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-FlashNextNoReparsePoints {
    param([Parameter(Mandatory=$true)][string]$Root)
    $rootItem = Get-Item -LiteralPath $Root -Force
    if (($rootItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The deployment staging root is a reparse point and was rejected: $Root"
    }
    foreach ($item in Get-ChildItem -LiteralPath $Root -Force -Recurse) {
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "The deployment staging tree contains a reparse point and was rejected: $($item.FullName)"
        }
    }
}

function Assert-FlashNextInventory {
    param(
        [Parameter(Mandatory=$true)][string]$Root,
        [Parameter(Mandatory=$true)]$Inventory,
        [Parameter(Mandatory=$true)][string]$Label
    )
    $rootFull = [System.IO.Path]::GetFullPath($Root)
    if (-not (Test-Path -LiteralPath $rootFull -PathType Container)) { throw "$Label directory is missing: $rootFull" }
    $records = @($Inventory)
    if ($records.Count -eq 0) { throw "$Label inventory is empty." }
    $expected = @{}
    foreach ($record in $records) {
        $relative = [string]$record.path
        if ([string]::IsNullOrWhiteSpace($relative) -or [System.IO.Path]::IsPathRooted($relative)) { throw "$Label inventory contains an invalid relative path." }
        $relativeWindows = $relative.Replace('/', '\')
        $key = $relativeWindows.ToUpperInvariant()
        if ($expected.ContainsKey($key)) { throw "$Label inventory contains a duplicate path '$relative'." }
        $expected[$key] = $record
        $full = [System.IO.Path]::GetFullPath((Join-Path $rootFull $relativeWindows))
        if (-not (Test-FlashNextPathContainedBy -Candidate $full -Root $rootFull)) { throw "$Label inventory path escapes its root: $relative" }
        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { throw "$Label inventory file is missing: $relative" }
        $item = Get-Item -LiteralPath $full -Force
        if ([int64]$item.Length -ne [int64]$record.bytes) { throw "$Label inventory byte count differs for '$relative'." }
        $actualHash = Get-FlashNextFileSha256 -Path $full
        if (-not $actualHash.Equals([string]$record.sha256, [StringComparison]::OrdinalIgnoreCase)) {
            throw "$Label inventory SHA-256 differs for '$relative'."
        }
    }
    $actualFiles = @(Get-ChildItem -LiteralPath $rootFull -File -Force -Recurse)
    foreach ($file in $actualFiles) {
        $relative = $file.FullName.Substring($rootFull.TrimEnd('\').Length).TrimStart('\').Replace('/', '\')
        if (-not $expected.ContainsKey($relative.ToUpperInvariant())) { throw "$Label contains an unlisted file '$relative'." }
    }
    if ($actualFiles.Count -ne $expected.Count) {
        throw "$Label inventory expected $($expected.Count) files, but the directory contains $($actualFiles.Count)."
    }
}

function Remove-FlashNextDirectoryIfPresent {
    param([AllowNull()][string]$Path)
    if (-not [string]::IsNullOrWhiteSpace($Path) -and (Test-Path -LiteralPath $Path)) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}

$resultOutputPath = $ResultPath
$appIncoming = $null
$runtimeIncoming = $null
$appCurrent = $null
$appPrevious = $null
$runtimeCurrent = $null
$runtimePrevious = $null
$appOldMoved = $false
$runtimeOldMoved = $false
$appNewActivated = $false
$runtimeNewActivated = $false

try {
    Set-MachineInstallStage -Name 'administrator-check'
    if (-not (Test-FlashNextAdministrator)) { throw 'This deployment helper must run with administrator rights.' }

    $ApplicationRoot = [System.IO.Path]::GetFullPath($ApplicationRoot)
    $StagingRoot = [System.IO.Path]::GetFullPath($StagingRoot)
    $BuildResultPath = [System.IO.Path]::GetFullPath($BuildResultPath)
    $ResultPath = [System.IO.Path]::GetFullPath($ResultPath)
    $resultOutputPath = $ResultPath

    Set-MachineInstallStage -Name 'build-result-validation'
    if (-not (Test-Path -LiteralPath $BuildResultPath -PathType Leaf)) { throw "Build result is missing: $BuildResultPath" }
    $buildResult = Read-FlashNextJson -Path $BuildResultPath
    if (-not $buildResult -or [string]$buildResult.status -ne 'succeeded') { throw 'The non-elevated build stage did not complete successfully.' }
    if ([string]$buildResult.projectRevision -ne '1.0.5') { throw "The staged build revision '$($buildResult.projectRevision)' is not compatible with this deployment helper." }
    if (-not ([System.IO.Path]::GetFullPath([string]$buildResult.stagingRoot).Equals($StagingRoot, [StringComparison]::OrdinalIgnoreCase))) {
        throw 'The build result does not identify the supplied staging directory.'
    }

    $appStaging = Join-Path $StagingRoot 'app'
    $runtimeStaging = Join-Path $StagingRoot 'runtime\current'
    if (-not (Test-Path -LiteralPath (Join-Path $appStaging 'FlashNext.Manager.exe') -PathType Leaf)) { throw 'The staged manager executable is missing.' }
    if (-not (Test-Path -LiteralPath (Join-Path $appStaging 'FlashNext.VulkanProbe.exe') -PathType Leaf)) { throw 'The staged Vulkan probe executable is missing.' }
    foreach ($runtimeFile in @('llama-server.exe','llama-cli.exe','llama-bench.exe','runtime.build.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $runtimeStaging $runtimeFile) -PathType Leaf)) { throw "The staged runtime is missing '$runtimeFile'." }
    }

    Set-MachineInstallStage -Name 'staging-integrity-validation'
    Assert-FlashNextNoReparsePoints -Root $StagingRoot
    Assert-FlashNextInventory -Root $appStaging -Inventory $buildResult.inventories.app -Label 'Manager staging'
    Assert-FlashNextInventory -Root $runtimeStaging -Inventory $buildResult.inventories.runtime -Label 'Runtime staging'

    Set-MachineInstallStage -Name 'running-process-check'
    foreach ($processName in @('FlashNext.Manager.exe','FlashNext.Dashboard.exe','llama-server.exe')) {
        foreach ($processInfo in @(Get-CimInstance Win32_Process -Filter ("Name='" + $processName + "'") -ErrorAction SilentlyContinue)) {
            if ($processInfo.ExecutablePath -and (Test-FlashNextPathContainedBy -Candidate ([string]$processInfo.ExecutablePath) -Root $ApplicationRoot)) {
                throw "Close the running '$processName' process and rerun install.cmd. Process ID: $($processInfo.ProcessId)."
            }
        }
    }

    Set-MachineInstallStage -Name 'long-path-support'
    $fileSystemKey = 'HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem'
    New-Item -Path $fileSystemKey -Force | Out-Null
    New-ItemProperty -Path $fileSystemKey -Name 'LongPathsEnabled' -PropertyType DWord -Value 1 -Force | Out-Null

    Set-MachineInstallStage -Name 'copy-verified-release'
    $appContainer = Join-Path $ApplicationRoot 'app'
    $runtimeContainer = Join-Path $ApplicationRoot 'runtime'
    New-Item -ItemType Directory -Path $appContainer -Force | Out-Null
    New-Item -ItemType Directory -Path $runtimeContainer -Force | Out-Null
    $deploymentId = [Guid]::NewGuid().ToString('N')
    $appIncoming = Join-Path $appContainer ('incoming-' + $deploymentId)
    $runtimeIncoming = Join-Path $runtimeContainer ('incoming-' + $deploymentId)
    New-Item -ItemType Directory -Path $appIncoming -Force | Out-Null
    New-Item -ItemType Directory -Path $runtimeIncoming -Force | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $appStaging -Force) { Copy-Item -LiteralPath $item.FullName -Destination $appIncoming -Recurse -Force }
    foreach ($item in Get-ChildItem -LiteralPath $runtimeStaging -Force) { Copy-Item -LiteralPath $item.FullName -Destination $runtimeIncoming -Recurse -Force }
    Assert-FlashNextNoReparsePoints -Root $appIncoming
    Assert-FlashNextNoReparsePoints -Root $runtimeIncoming
    Assert-FlashNextInventory -Root $appIncoming -Inventory $buildResult.inventories.app -Label 'Manager deployment copy'
    Assert-FlashNextInventory -Root $runtimeIncoming -Inventory $buildResult.inventories.runtime -Label 'Runtime deployment copy'

    Set-MachineInstallStage -Name 'atomic-activation'
    $appCurrent = Join-Path $appContainer 'current'
    $appPrevious = Join-Path $appContainer 'previous'
    $runtimeCurrent = Join-Path $runtimeContainer 'current'
    $runtimePrevious = Join-Path $runtimeContainer 'previous'
    Remove-FlashNextDirectoryIfPresent -Path $appPrevious
    Remove-FlashNextDirectoryIfPresent -Path $runtimePrevious

    if (Test-Path -LiteralPath $appCurrent) {
        [System.IO.Directory]::Move($appCurrent, $appPrevious)
        $appOldMoved = $true
    }
    if (Test-Path -LiteralPath $runtimeCurrent) {
        [System.IO.Directory]::Move($runtimeCurrent, $runtimePrevious)
        $runtimeOldMoved = $true
    }
    [System.IO.Directory]::Move($appIncoming, $appCurrent)
    $appNewActivated = $true
    $appIncoming = $null
    [System.IO.Directory]::Move($runtimeIncoming, $runtimeCurrent)
    $runtimeNewActivated = $true
    $runtimeIncoming = $null

    Set-MachineInstallStage -Name 'installed-integrity-validation'
    Assert-FlashNextInventory -Root $appCurrent -Inventory $buildResult.inventories.app -Label 'Installed manager'
    Assert-FlashNextInventory -Root $runtimeCurrent -Inventory $buildResult.inventories.runtime -Label 'Installed runtime'

    Set-MachineInstallStage -Name 'machine-report'
    $machineReportPath = Join-Path (Get-FlashNextMachineRoot) 'machine-install-report.json'
    $report = [pscustomobject]@{
        schemaVersion = 2
        status = 'succeeded'
        completedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        projectRevision = [string]$buildResult.projectRevision
        applicationRoot = $ApplicationRoot
        buildResultPath = $BuildResultPath
        appCurrent = $appCurrent
        appPreviousPreserved = $appOldMoved
        runtimeCurrent = $runtimeCurrent
        runtimePreviousPreserved = $runtimeOldMoved
        buildLogPath = [string]$buildResult.logPath
        deploymentLogPath = $script:FlashNextLogPath
    }
    Write-FlashNextJsonAtomic -Path $machineReportPath -Value $report
    Set-MachineInstallStage -Name 'complete'
    Write-FlashNextOperationResult -Path $ResultPath -Status 'succeeded' -Stage $script:MachineInstallStage -Message 'Verified manager and Vulkan runtime artifacts were deployed atomically to Program Files.' -ExitCode 0 -LogPath $script:FlashNextLogPath -Additional @{ reportPath = $machineReportPath; buildResultPath = $BuildResultPath }
    Write-FlashNextLog 'Elevated deployment completed. No package acquisition, source build, or test ran as administrator.'
    exit 0
}
catch {
    $failure = $_
    $message = $failure.Exception.Message
    $failedStage = $script:MachineInstallStage
    try {
        Set-MachineInstallStage -Name 'rollback'
        if ($runtimeNewActivated -and $runtimeCurrent -and (Test-Path -LiteralPath $runtimeCurrent)) { Remove-Item -LiteralPath $runtimeCurrent -Recurse -Force }
        if ($runtimeOldMoved -and $runtimePrevious -and (Test-Path -LiteralPath $runtimePrevious) -and -not (Test-Path -LiteralPath $runtimeCurrent)) { [System.IO.Directory]::Move($runtimePrevious, $runtimeCurrent) }
        if ($appNewActivated -and $appCurrent -and (Test-Path -LiteralPath $appCurrent)) { Remove-Item -LiteralPath $appCurrent -Recurse -Force }
        if ($appOldMoved -and $appPrevious -and (Test-Path -LiteralPath $appPrevious) -and -not (Test-Path -LiteralPath $appCurrent)) { [System.IO.Directory]::Move($appPrevious, $appCurrent) }
        Remove-FlashNextDirectoryIfPresent -Path $appIncoming
        Remove-FlashNextDirectoryIfPresent -Path $runtimeIncoming
    }
    catch {
        try { Write-FlashNextLog -Message ("Rollback encountered an additional error: {0}" -f $_.Exception.Message) -Level 'ERROR' } catch { }
    }
    try { Write-FlashNextLog -Message ("Deployment stage '{0}' failed: {1}" -f $failedStage, $message) -Level 'ERROR' } catch { }
    try {
        Write-FlashNextOperationResult -Path $resultOutputPath -Status 'failed' -Stage $failedStage -Message $message -ExitCode 1 -LogPath $script:FlashNextLogPath -Additional @{
            exceptionType = $failure.Exception.GetType().FullName
            scriptStackTrace = $failure.ScriptStackTrace
            rollbackAttempted = $true
        }
    } catch { }
    Write-Host ''
    Write-Host ("FlashNext deployment failed: {0}" -f $message) -ForegroundColor Red
    Write-Host ("Log: {0}" -f $script:FlashNextLogPath) -ForegroundColor DarkGray
    exit 1
}
