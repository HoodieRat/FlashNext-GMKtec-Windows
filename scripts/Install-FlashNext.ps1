[CmdletBinding()]
param(
    [string]$ModelDirectory,
    [switch]$AcceptLicense,
    [switch]$SkipModel,
    [switch]$ForceDependencyRepair,
    [switch]$RestartDashboard,
    [switch]$NoLaunch
)

. (Join-Path $PSScriptRoot 'Common.ps1')
$sourceRoot = Get-FlashNextRepositoryRoot -ScriptDirectory $PSScriptRoot
$userRoot = Get-FlashNextUserRoot
$statePath = Join-Path $userRoot 'install-state.json'
$applicationRoot = Join-Path $env:ProgramFiles 'FlashNextManager'
$appCurrent = Join-Path $applicationRoot 'app\current'
$runtimeCurrent = Join-Path $applicationRoot 'runtime\current'
$managerExe = Join-Path $appCurrent 'FlashNext.Manager.exe'
$pythonEnvironmentRoot = Join-Path $userRoot 'python-env'
$pythonExe = Join-Path $pythonEnvironmentRoot 'Scripts\python.exe'
$stagingRoot = Join-Path $userRoot 'install-staging'
$machineResultPath = Join-Path $userRoot 'state\machine-install-result.json'
$dependencyReportPath = Join-Path $userRoot 'state\dependency-install-result.json'
$buildResultPath = Join-Path $userRoot 'state\build-install-result.json'
$bootstrapHardwareReportPath = Join-Path $userRoot 'state\bootstrap-hardware-result.json'
Initialize-FlashNextLogging -Root $userRoot -Name 'install'
$projectRevision = '1.0.5'
$basicPreflightVersion = 2
$vulkanProbeVersion = 1

function Get-StageRank {
    param([string]$Stage)
    $stages = @('new','preflight-complete','dependencies-complete','build-complete','machine-complete','hardware-complete','model-complete','complete')
    $index = [Array]::IndexOf($stages, $Stage)
    if ($index -lt 0) { return 0 }
    return $index
}

function Test-SuccessfulFlashNextResult {
    param(
        [Parameter(Mandatory=$true)][string]$Path,
        [string]$RequiredRevision,
        [string]$RequiredSourceRoot,
        [string]$RequiredStagingRoot,
        [string[]]$RequiredFiles
    )
    $value = Read-FlashNextJson -Path $Path
    if (-not $value -or [string]$value.status -ne 'succeeded') { return $false }
    if ($RequiredRevision -and [string]$value.projectRevision -ne $RequiredRevision) { return $false }
    if ($RequiredSourceRoot) {
        if (-not ($value.PSObject.Properties.Name -contains 'sourceRoot') -or -not $value.sourceRoot) { return $false }
        try {
            $recordedSource = [System.IO.Path]::GetFullPath([string]$value.sourceRoot)
            $expectedSource = [System.IO.Path]::GetFullPath($RequiredSourceRoot)
            if (-not $recordedSource.Equals($expectedSource, [StringComparison]::OrdinalIgnoreCase)) { return $false }
        } catch { return $false }
    }
    if ($RequiredStagingRoot) {
        if (-not ($value.PSObject.Properties.Name -contains 'stagingRoot') -or -not $value.stagingRoot) { return $false }
        try {
            $recordedStaging = [System.IO.Path]::GetFullPath([string]$value.stagingRoot)
            $expectedStaging = [System.IO.Path]::GetFullPath($RequiredStagingRoot)
            if (-not $recordedStaging.Equals($expectedStaging, [StringComparison]::OrdinalIgnoreCase)) { return $false }
        } catch { return $false }
        foreach ($relative in @($RequiredFiles)) {
            if (-not (Test-Path -LiteralPath (Join-Path $expectedStaging $relative) -PathType Leaf)) { return $false }
        }
    }
    return $true
}


function Get-FlashNextDependencyRecord {
    param([Parameter(Mandatory=$true)]$Report, [Parameter(Mandatory=$true)][string]$PackageId)
    if (-not $Report -or -not $Report.packages) { return $null }
    return @($Report.packages | Where-Object { [string]$_.id -eq $PackageId } | Select-Object -First 1)[0]
}

function Resolve-FlashNextReportedExecutable {
    param([AllowNull()]$Record, [string[]]$Names, [string[]]$AdditionalPaths)
    $candidates = @()
    if ($Record -and $Record.executablePath) { $candidates += [string]$Record.executablePath }
    $candidates += @($AdditionalPaths)
    return (Find-FlashNextExecutable -Names $Names -AdditionalPaths $candidates)
}

function Get-FlashNextResultMessage {
    param([Parameter(Mandatory=$true)][string]$Path, [Parameter(Mandatory=$true)][string]$Label)
    $value = Read-FlashNextJson -Path $Path
    if (-not $value) { return "$Label did not create a result file at '$Path'." }
    $hasStage = ($value.PSObject.Properties.Name -contains 'stage')
    $hasMessage = ($value.PSObject.Properties.Name -contains 'message')
    $hasLog = ($value.PSObject.Properties.Name -contains 'logPath')
    $hasStatus = ($value.PSObject.Properties.Name -contains 'status')
    $stage = if ($hasStage -and $value.stage) { [string]$value.stage } else { 'unknown' }
    $message = if ($hasMessage -and $value.message) { [string]$value.message } elseif ($hasStatus) { "status '$($value.status)'" } else { 'no status or message was recorded' }
    $log = if ($hasLog -and $value.logPath) { " Log: $($value.logPath)" } else { '' }
    return "$Label failed during '$stage': $message$log"
}

try {
    Write-Host ''
    Write-Host 'FlashNext-GMKtec-Windows installer' -ForegroundColor Cyan
    Write-Host 'Pinned Windows Vulkan runtime and Qwen3.8-Flash-Next setup' -ForegroundColor DarkGray
    Write-Host ''
    New-Item -ItemType Directory -Path $userRoot -Force | Out-Null
    $bootstrapLock = Join-Path $sourceRoot 'manifests\bootstrap.lock.json'
    $bootstrapProbe = Join-Path $sourceRoot 'bootstrap\FlashNext.VulkanProbe.exe'
    if (-not (Test-Path -LiteralPath $bootstrapLock -PathType Leaf)) { throw "Source package is missing manifests\bootstrap.lock.json." }
    if (-not (Test-Path -LiteralPath $bootstrapProbe -PathType Leaf)) { throw "Source package is missing bootstrap\FlashNext.VulkanProbe.exe. Restore the 1.0.5 package and rerun install.cmd." }
    $lock = Read-FlashNextJson -Path $bootstrapLock
    $probeInfo = Get-Item -LiteralPath $bootstrapProbe -Force
    if ([int64]$probeInfo.Length -ne [int64]$lock.bytes) { throw ("Bootstrap Vulkan probe size mismatch ({0} vs {1} bytes). Restore the original 1.0.5 source package." -f $probeInfo.Length, $lock.bytes) }
    $probeHash = Get-FlashNextFileSha256 -Path $bootstrapProbe
    if (-not $probeHash.Equals([string]$lock.sha256, [StringComparison]::OrdinalIgnoreCase)) { throw ("Bootstrap Vulkan probe SHA-256 mismatch. Restore the original 1.0.5 source package.") }
    Write-FlashNextLog 'Source package bootstrap Vulkan probe hash verified.'
    $state = Read-FlashNextJson -Path $statePath
    if (-not $state) {
        $state = [pscustomobject]@{ stage = 'new'; sourceRoot = $sourceRoot; projectRevision = $projectRevision; createdAtUtc = [DateTimeOffset]::UtcNow.ToString('o') }
        Write-FlashNextJsonAtomic -Path $statePath -Value $state
    }

    $migration = Convert-FlashNextInstallState -State $state -ProjectRevision $projectRevision -SourceRoot $sourceRoot -StatePath $statePath
    $state = $migration.State
    if ($migration.Changed) {
        Write-FlashNextLog ("Install state migrated to revision {0}. Obsolete Vulkan full-report results are invalidated; verified dependencies and model files are reused when still valid." -f $projectRevision) 'WARN'
        Write-FlashNextJsonAtomic -Path $statePath -Value $state
        if (Test-Path -LiteralPath $bootstrapHardwareReportPath -PathType Leaf) {
            $oldProbe = Read-FlashNextJson -Path $bootstrapHardwareReportPath
            $obsolete = $true
            if ($oldProbe -and [string]$oldProbe.probeKind -eq 'direct-vulkan-memory-probe' -and [string]$oldProbe.projectRevision -eq $projectRevision) { $obsolete = $false }
            if ($obsolete) {
                Remove-Item -LiteralPath $bootstrapHardwareReportPath -Force
                Write-FlashNextLog 'Removed the obsolete vulkaninfo full-report hardware result. The direct Vulkan memory probe will run next.'
            }
        }
        $state = Read-FlashNextJson -Path $statePath
    }

    if ((Get-StageRank -Stage ([string]$state.stage)) -ge 4 -and -not (Test-Path -LiteralPath $managerExe -PathType Leaf)) {
        $recoveryStage = if (Test-SuccessfulFlashNextResult -Path $buildResultPath -RequiredRevision $projectRevision) { 'build-complete' } else { 'preflight-complete' }
        Write-FlashNextLog ("Installed manager is absent. Resuming from stage '{0}'." -f $recoveryStage) 'WARN'
        Set-FlashNextInstallStage -StatePath $statePath -Stage $recoveryStage -Additional @{ projectRevision = $projectRevision }
        $state = Read-FlashNextJson -Path $statePath
    }

    # A completed install is not evidence that newly edited application sources are deployed.
    # Rebuild the app on reinstall when the installed native runtime/model pins are unchanged.
    $samePins = $true
    foreach ($pin in @('runtime.lock.json','model.lock.json')) {
        $installedPin = Join-Path $appCurrent ('manifests\' + $pin)
        if (-not (Test-Path -LiteralPath $installedPin -PathType Leaf)) { $samePins = $false; break }
        if ((Get-FlashNextFileSha256 (Join-Path $sourceRoot ('manifests\' + $pin))) -ne (Get-FlashNextFileSha256 $installedPin)) { $samePins = $false; break }
    }
    if ([string]$state.stage -eq 'complete' -and $samePins -and -not $ForceDependencyRepair -and -not $ModelDirectory -and -not $SkipModel -and
        (Test-Path -LiteralPath $managerExe -PathType Leaf) -and (Test-Path -LiteralPath (Join-Path $runtimeCurrent 'llama-server.exe') -PathType Leaf)) {
        Write-FlashNextLog 'Completed installation detected: publishing and deploying the current application source instead of reusing the old binaries.'
        & (Join-Path $sourceRoot 'scripts\Publish-AppUpdate.ps1') -SourceRoot $sourceRoot -NoLaunch:$NoLaunch -RestartDashboard:$RestartDashboard
        if ($LASTEXITCODE -ne 0) { throw 'Application update failed; the previous installation has been preserved.' }
        exit 0
    }
    if ([string]$state.stage -eq 'complete' -and -not $samePins) {
        Set-FlashNextInstallStage -StatePath $statePath -Stage 'dependencies-complete'
        $state = Read-FlashNextJson -Path $statePath
    }

    $recordedPreflightVersion = 0
    if ($state.PSObject.Properties.Name -contains 'basicPreflightVersion') { $recordedPreflightVersion = [int]$state.basicPreflightVersion }
    if ((Get-StageRank -Stage ([string]$state.stage)) -lt 1 -or $recordedPreflightVersion -lt $basicPreflightVersion) {
        Write-FlashNextLog 'Running Windows and GMKtec hardware preflight.'
        if (-not (Test-FlashNextWindows11X64)) { throw 'Windows 11 x64 build 22000 or newer is required.' }
        $systemDrive = Get-PSDrive -Name ($env:SystemDrive.TrimEnd(':')) -ErrorAction SilentlyContinue
        if ($systemDrive -and $systemDrive.Free -lt 40GB) {
            throw ('The system drive has {0:N1} GiB free; at least 40 GiB is required for restore, tests, and Vulkan compilation.' -f ($systemDrive.Free / 1GB))
        }
        $cpu = Get-FlashNextCpuName
        $gpus = Get-FlashNextGpuNames
        $memory = Get-FlashNextMemorySnapshot
        Write-Host ('CPU: {0}' -f $cpu)
        Write-Host ('GPU: {0}' -f ($gpus -join '; '))
        Write-Host ('Installed physical memory: {0:N1} GiB ({1})' -f $memory.InstalledGiB, $memory.Source)
        if ($memory.OsVisibleBytes -gt 0) { Write-Host ('OS-visible memory after firmware/UMA reservations: {0:N1} GiB' -f $memory.OsVisibleGiB) }
        if ($memory.ReliableInstalledCapacity -and $memory.InstalledGiB -ge 120 -and $memory.OsVisibleGiB -ge 48 -and $memory.OsVisibleGiB -le 80) {
            Write-Host 'Warning: this 128-GiB/approximately-64-GiB-visible pattern is consistent with a 64 GB BIOS UMA allocation.' -ForegroundColor Yellow
            Write-Host 'The factory FlashNext model requires 96 GB UMA. Set the EVO-X2 BIOS UMA frame buffer to 96 GB before the Vulkan check; the installer will verify the actual heap and never changes BIOS settings.' -ForegroundColor Yellow
        }
        if (-not (Test-FlashNextCpuTarget -Name $cpu)) { throw "This factory project targets AMD Ryzen AI Max+ 395; detected '$cpu'." }
        if (-not (Test-FlashNextGpuTarget -Names $gpus)) { throw "This factory project targets the Radeon 8060S. Windows reported '$($gpus -join '; ')'." }
        if (-not $memory.ReliableInstalledCapacity -and $memory.InstalledGiB -lt 120) {
            throw ('Windows exposed {0:N1} GiB to the operating system, but the physically installed capacity could not be read from SMBIOS. That usable-memory number can exclude a large UMA reservation. Update the EVO-X2 BIOS if needed and rerun the installer; do not use this value alone to judge installed RAM.' -f $memory.OsVisibleGiB)
        }
        if ($memory.InstalledGiB -lt 120) { throw ('At least 120 GiB physically installed memory is required; detected {0:N1} GiB from {1}.' -f $memory.InstalledGiB, $memory.Source) }
        Set-FlashNextInstallStage -StatePath $statePath -Stage 'preflight-complete' -Additional @{
            projectRevision = $projectRevision
            basicPreflightVersion = $basicPreflightVersion
            sourceRoot = $sourceRoot
            cpu = $cpu
            gpu = ($gpus -join '; ')
            installedPhysicalMemoryGiB = [double]$memory.InstalledGiB
            osVisibleMemoryGiB = [double]$memory.OsVisibleGiB
            physicalMemorySource = [string]$memory.Source
        }
        $state = Read-FlashNextJson -Path $statePath
    }

    $recordedProbeVersion = 0
    if ($state.PSObject.Properties.Name -contains 'vulkanProbeVersion') { $recordedProbeVersion = [int]$state.vulkanProbeVersion }
    $probeReady = $false
    if (Test-Path -LiteralPath $bootstrapHardwareReportPath -PathType Leaf) {
        $existingProbe = Read-FlashNextJson -Path $bootstrapHardwareReportPath
        if ($existingProbe -and [string]$existingProbe.status -eq 'succeeded' -and [string]$existingProbe.projectRevision -eq $projectRevision -and [string]$existingProbe.probeKind -eq 'direct-vulkan-memory-probe' -and [bool]$existingProbe.passesFactoryUmaRequirement) {
            $probeReady = $true
        }
    }
    if (-not $probeReady -or $recordedProbeVersion -lt $vulkanProbeVersion) {
        Write-Host ''
        Write-Host 'Measuring the Radeon 8060S Vulkan device-local heap before any dependency installation.' -ForegroundColor Cyan
        try {
            & (Join-Path $sourceRoot 'scripts\Test-BootstrapHardware.ps1') -SourceRoot $sourceRoot -ReportPath $bootstrapHardwareReportPath -ParentLogPath $script:FlashNextLogPath -TimeoutSeconds 15
        }
        catch {
            $bootstrapReport = Read-FlashNextJson -Path $bootstrapHardwareReportPath
            $bootstrapMessage = if ($bootstrapReport -and $bootstrapReport.message) { [string]$bootstrapReport.message } else { $_.Exception.Message }
            throw ("Vulkan/UMA preflight failed before dependency installation: {0} Report: {1}" -f $bootstrapMessage, $bootstrapHardwareReportPath)
        }
        $bootstrapReport = Read-FlashNextJson -Path $bootstrapHardwareReportPath
        if (-not $bootstrapReport -or [string]$bootstrapReport.status -ne 'succeeded') { throw (Get-FlashNextResultMessage -Path $bootstrapHardwareReportPath -Label 'Direct Vulkan memory probe') }
        Write-Host ('Radeon 8060S largest DEVICE_LOCAL heap: {0:N1} GiB' -f [double]$bootstrapReport.largestDeviceLocalHeapGiB)
        Set-FlashNextInstallStage -StatePath $statePath -Stage ([string]$state.stage) -Additional @{
            projectRevision = $projectRevision
            vulkanProbeVersion = $vulkanProbeVersion
            vulkanProbeKind = 'direct-vulkan-memory-probe'
            vulkanHardwareResultInvalidated = $false
            largestDeviceLocalHeapBytes = $bootstrapReport.largestDeviceLocalHeapBytes
            vulkanDeviceName = [string]$bootstrapReport.targetDeviceName
        }
        $state = Read-FlashNextJson -Path $statePath
    }

    $dependencyReady = Test-SuccessfulFlashNextResult -Path $dependencyReportPath -RequiredSourceRoot $sourceRoot
    if ($dependencyReady) {
        $depReport = Read-FlashNextJson -Path $dependencyReportPath
        $depRevision = if ($depReport.PSObject.Properties.Name -contains 'projectRevision') { [string]$depReport.projectRevision } else { '' }
        if (@('1.0.4','1.0.5') -notcontains $depRevision) { $dependencyReady = $false }
        elseif ([string]$depReport.status -ne 'succeeded') { $dependencyReady = $false }
    }
    if ((Get-StageRank -Stage ([string]$state.stage)) -lt 2 -or $ForceDependencyRepair -or -not $dependencyReady) {
        Write-FlashNextLog 'Validating Windows Package Manager in the normal user context.'
        $wingetPath = Resolve-FlashNextWinGetPath -AttemptRegistration
        if (-not $wingetPath) {
            throw 'Windows Package Manager could not be started. Install or update Microsoft App Installer, open it once, and rerun install.cmd.'
        }
        Write-FlashNextLog ("WinGet is ready at '{0}'." -f $wingetPath)
        Write-Host 'Installing locked dependencies from the normal user session. Individual signed installers may request administrator approval.' -ForegroundColor Yellow
        $dependencyArguments = @{
            SourceRoot = $sourceRoot
            WinGetPath = $wingetPath
            ReportPath = $dependencyReportPath
            ParentLogPath = $script:FlashNextLogPath
        }
        if ($ForceDependencyRepair) { $dependencyArguments['ForceDependencyRepair'] = $true }
        try { & (Join-Path $sourceRoot 'scripts\Install-Dependencies.ps1') @dependencyArguments }
        catch { throw (Get-FlashNextResultMessage -Path $dependencyReportPath -Label 'Dependency installation') }
        $dependencyReport = Read-FlashNextJson -Path $dependencyReportPath
        if ($dependencyReport -and [string]$dependencyReport.status -eq 'reboot-required') {
            throw ([string]$dependencyReport.message)
        }
        if (-not $dependencyReport -or [string]$dependencyReport.status -ne 'succeeded') { throw (Get-FlashNextResultMessage -Path $dependencyReportPath -Label 'Dependency installation') }
        Set-FlashNextInstallStage -StatePath $statePath -Stage 'dependencies-complete' -Additional @{ dependencyReport = $dependencyReportPath; wingetPath = [string]$dependencyReport.wingetPath; wingetVersion = [string]$dependencyReport.wingetVersion }
        $state = Read-FlashNextJson -Path $statePath
    } else {
        $dependencyReport = Read-FlashNextJson -Path $dependencyReportPath
    }

    Refresh-FlashNextPath
    $gitRecord = Get-FlashNextDependencyRecord -Report $dependencyReport -PackageId 'Git.Git'
    $cmakeRecord = Get-FlashNextDependencyRecord -Report $dependencyReport -PackageId 'Kitware.CMake'
    $pythonRecord = Get-FlashNextDependencyRecord -Report $dependencyReport -PackageId 'Python.Python.3.13'
    $dotnetRecord = Get-FlashNextDependencyRecord -Report $dependencyReport -PackageId 'Microsoft.DotNet.SDK.10'
    $vulkanRecord = Get-FlashNextDependencyRecord -Report $dependencyReport -PackageId 'KhronosGroup.VulkanSDK'

    $pythonCommand = $null
    if ($pythonRecord -and [bool]$pythonRecord.capabilityVerified -and $pythonRecord.executablePath -and (Test-Path -LiteralPath ([string]$pythonRecord.executablePath) -PathType Leaf)) {
        $pythonCommand = [pscustomobject]@{ Path = [string]$pythonRecord.executablePath; Prefix = @($pythonRecord.commandPrefix) }
    }
    if (-not $pythonCommand) { $pythonCommand = Resolve-FlashNextPython313Command }
    if (-not $pythonCommand) { throw 'Python 3.13 passed dependency installation but could not be started. Rerun install.cmd with -ForceDependencyRepair.' }
    $dotnetPath = Resolve-FlashNextReportedExecutable -Record $dotnetRecord -Names @('dotnet.exe','dotnet') -AdditionalPaths @((Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'))
    $gitPath = Resolve-FlashNextReportedExecutable -Record $gitRecord -Names @('git.exe','git') -AdditionalPaths @((Join-Path $env:ProgramFiles 'Git\cmd\git.exe'),(Join-Path $env:LOCALAPPDATA 'Programs\Git\cmd\git.exe'))
    $cmakePath = Resolve-FlashNextReportedExecutable -Record $cmakeRecord -Names @('cmake.exe','cmake') -AdditionalPaths @((Join-Path $env:ProgramFiles 'CMake\bin\cmake.exe'))
    $vulkanSdkRoot = if ($vulkanRecord -and $vulkanRecord.rootPath) { [string]$vulkanRecord.rootPath } else { [Environment]::GetEnvironmentVariable('VULKAN_SDK','Machine') }
    if (-not $dotnetPath) { throw '.NET SDK 10 passed dependency installation but dotnet.exe could not be located.' }
    if (-not $gitPath) { throw 'Git passed dependency installation but git.exe could not be located.' }
    if (-not $cmakePath) { throw 'CMake passed dependency installation but cmake.exe could not be located.' }
    if (-not $vulkanSdkRoot -or -not (Test-Path -LiteralPath $vulkanSdkRoot -PathType Container)) { throw 'The Vulkan SDK passed dependency installation but its verified root directory could not be located.' }
    $env:VULKAN_SDK = $vulkanSdkRoot

    $buildReady = Test-SuccessfulFlashNextResult -Path $buildResultPath -RequiredRevision $projectRevision -RequiredSourceRoot $sourceRoot -RequiredStagingRoot $stagingRoot -RequiredFiles @('app\FlashNext.Manager.exe','app\FlashNext.VulkanProbe.exe','runtime\current\llama-server.exe','runtime\current\runtime.build.json')
    if ((Get-StageRank -Stage ([string]$state.stage)) -lt 3 -or -not $buildReady) {
        Write-Host ''
        Write-Host 'Building and testing FlashNext in the normal user session.' -ForegroundColor Cyan
        Write-Host 'This stage restores .NET packages, runs tests, publishes the manager, and compiles the pinned Vulkan runtime.' -ForegroundColor DarkGray
        $buildArguments = @{
            SourceRoot = $sourceRoot
            StagingRoot = $stagingRoot
            PythonEnvironmentRoot = $pythonEnvironmentRoot
            DependencyReportPath = $dependencyReportPath
            BuildResultPath = $buildResultPath
            PythonPath = [string]$pythonCommand.Path
            PythonPrefix = @($pythonCommand.Prefix)
            DotNetPath = $dotnetPath
            GitPath = $gitPath
            CMakePath = $cmakePath
            VulkanSdkRoot = $vulkanSdkRoot
            ParentLogPath = $script:FlashNextLogPath
        }
        try { & (Join-Path $sourceRoot 'scripts\Build-FlashNext.ps1') @buildArguments }
        catch { throw (Get-FlashNextResultMessage -Path $buildResultPath -Label 'Build and test stage') }
        if (-not (Test-SuccessfulFlashNextResult -Path $buildResultPath -RequiredRevision $projectRevision -RequiredSourceRoot $sourceRoot -RequiredStagingRoot $stagingRoot -RequiredFiles @('app\FlashNext.Manager.exe','app\FlashNext.VulkanProbe.exe','runtime\current\llama-server.exe','runtime\current\runtime.build.json'))) { throw (Get-FlashNextResultMessage -Path $buildResultPath -Label 'Build and test stage') }
        Set-FlashNextInstallStage -StatePath $statePath -Stage 'build-complete' -Additional @{ buildResult = $buildResultPath; stagingRoot = $stagingRoot; pythonEnvironment = $pythonEnvironmentRoot }
        $state = Read-FlashNextJson -Path $statePath
    }

    $runtimeServer = Join-Path $runtimeCurrent 'llama-server.exe'
    if ((Get-StageRank -Stage ([string]$state.stage)) -lt 4 -or -not (Test-Path -LiteralPath $managerExe -PathType Leaf) -or -not (Test-Path -LiteralPath $runtimeServer -PathType Leaf)) {
        Write-Host ''
        Write-Host 'Windows will request one administrator approval for a short, verified Program Files deployment.' -ForegroundColor Yellow
        Write-Host 'No package download, source compilation, .NET restore, or test runs inside the administrator process.' -ForegroundColor DarkGray
        $helperParameters = @{
            ApplicationRoot = $applicationRoot
            StagingRoot = $stagingRoot
            BuildResultPath = $buildResultPath
            ResultPath = $machineResultPath
            ParentLogPath = $script:FlashNextLogPath
        }
        Invoke-FlashNextElevated -ScriptPath (Join-Path $sourceRoot 'scripts\Install-MachineComponents.ps1') -Parameters $helperParameters -ResultPath $machineResultPath -OperationName 'FlashNext Program Files deployment'
        if (-not (Test-Path -LiteralPath $managerExe -PathType Leaf)) { throw 'Deployment reported success but FlashNext.Manager.exe is absent.' }
        if (-not (Test-Path -LiteralPath (Join-Path $appCurrent 'FlashNext.VulkanProbe.exe') -PathType Leaf)) { throw 'Deployment reported success but FlashNext.VulkanProbe.exe is absent.' }
        if (-not (Test-Path -LiteralPath $runtimeServer -PathType Leaf)) { throw 'Deployment reported success but llama-server.exe is absent.' }
        if (-not (Test-Path -LiteralPath $pythonExe -PathType Leaf)) { throw 'The normal-user Python environment is absent after deployment.' }
        Set-FlashNextInstallStage -StatePath $statePath -Stage 'machine-complete' -Additional @{ applicationRoot = $applicationRoot; machineResult = $machineResultPath; buildResult = $buildResultPath; dependencyReport = $dependencyReportPath }
        $state = Read-FlashNextJson -Path $statePath
    }

    Invoke-FlashNextNative -FilePath $managerExe -Arguments @('--initialize') -WorkingDirectory $appCurrent | Out-Null

    if ((Get-StageRank -Stage ([string]$state.stage)) -lt 5) {
        $hardwareReport = Join-Path $userRoot 'hardware-preflight.json'
        $probe = Invoke-FlashNextNative -FilePath $managerExe -Arguments @('--hardware-report',$hardwareReport) -WorkingDirectory $appCurrent -PassThru -IgnoreExitCode
        if ($probe.StandardOutput) { Write-Host $probe.StandardOutput }
        if ($probe.ExitCode -ne 0) {
            Write-Host ''
            $hardware = Read-FlashNextJson -Path $hardwareReport
            if ($hardware -and $hardware.warnings) { foreach ($warning in @($hardware.warnings)) { Write-Host ("Warning: {0}" -f $warning) -ForegroundColor Yellow } }
            if ($hardware -and $hardware.errors) { foreach ($hardwareError in @($hardware.errors)) { Write-Host ("Error: {0}" -f $hardwareError) -ForegroundColor Red } }
            $heapWasMeasured = ($hardware -and $null -ne $hardware.largestDeviceLocalHeapBytes)
            if ($heapWasMeasured -and -not ([bool]$hardware.meetsGpuHeapRequirement)) {
                Write-Host 'The factory model requires a Vulkan device-local heap of at least 90 GiB.' -ForegroundColor Red
                Write-Host 'Set the GMKtec EVO-X2 BIOS UMA frame buffer to 96 GB, save the BIOS setting, reboot Windows, and rerun install.cmd.' -ForegroundColor Yellow
                Write-Host 'The installer never changes BIOS settings.' -ForegroundColor Yellow
            } else {
                Write-Host 'Correct the hardware or driver errors above, then rerun install.cmd. If Vulkan later reports less than 90 GiB device-local memory, set BIOS UMA to 96 GB.' -ForegroundColor Yellow
            }
            throw "Hardware preflight failed. Full report: $hardwareReport"
        }
        Set-FlashNextInstallStage -StatePath $statePath -Stage 'hardware-complete' -Additional @{ hardwareReport = $hardwareReport }
        $state = Read-FlashNextJson -Path $statePath
    }

    if (-not $SkipModel -and (Get-StageRank -Stage ([string]$state.stage)) -lt 6) {
        if (-not $ModelDirectory) {
            $settingsPath = Join-Path $userRoot 'config\settings.json'
            $settings = Read-FlashNextJson -Path $settingsPath
            if ($settings -and $settings.paths.modelDirectory) { $ModelDirectory = [string]$settings.paths.modelDirectory }
        }
        if (-not $ModelDirectory) {
            $drive = Get-PSDrive -PSProvider FileSystem | Where-Object { $_.Free -ge 115GB } | Sort-Object Free -Descending | Select-Object -First 1
            if (-not $drive) { throw 'No fixed drive has the required 115 GiB free. Free disk space or add a suitable model drive, then rerun install.cmd.' }
            $defaultModel = Join-Path $drive.Root 'FlashNextModels\Qwen3.8-Flash-Next-UD-Q4_K_XL'
            Write-Host ('Suggested model directory: {0}' -f $defaultModel) -ForegroundColor Cyan
            $entered = Read-Host 'Model directory (press Enter for the suggested path)'
            if ($entered) { $ModelDirectory = $entered } else { $ModelDirectory = $defaultModel }
        }
        $ModelDirectory = [System.IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($ModelDirectory))
        New-Item -ItemType Directory -Path $ModelDirectory -Force | Out-Null
        $driveInfo = New-Object System.IO.DriveInfo([System.IO.Path]::GetPathRoot($ModelDirectory))
        Write-Host ('Model drive free space: {0:N1} GiB; minimum 115 GiB; recommended 140 GiB.' -f ($driveInfo.AvailableFreeSpace / 1GB))
        if ($driveInfo.AvailableFreeSpace -lt 115GB) { throw 'The selected model drive does not have 115 GiB free.' }
        $sourceLock = Join-Path $sourceRoot 'manifests\model.lock.json'
        $installedLock = Join-Path $appCurrent 'manifests\model.lock.json'
        $sourceDownloader = Join-Path $sourceRoot 'scripts\model_download.py'
        $installedDownloader = Join-Path $appCurrent 'scripts\model_download.py'
        $lockNeedsUpdate = $true
        $downloaderNeedsUpdate = $true
        if (Test-Path -LiteralPath $installedLock -PathType Leaf) {
            $lockNeedsUpdate = -not (Get-FlashNextFileSha256 -Path $sourceLock).Equals((Get-FlashNextFileSha256 -Path $installedLock), [StringComparison]::OrdinalIgnoreCase)
        }
        if (Test-Path -LiteralPath $installedDownloader -PathType Leaf) {
            $downloaderNeedsUpdate = -not (Get-FlashNextFileSha256 -Path $sourceDownloader).Equals((Get-FlashNextFileSha256 -Path $installedDownloader), [StringComparison]::OrdinalIgnoreCase)
        }
        if ($lockNeedsUpdate -or $downloaderNeedsUpdate) {
            $copiedWithoutElevation = $false
            try {
                Copy-Item -LiteralPath $sourceLock -Destination $installedLock -Force
                Copy-Item -LiteralPath $sourceDownloader -Destination $installedDownloader -Force
                $copiedWithoutElevation = $true
                Write-FlashNextLog 'Updated the installed model lock and downloader without elevation.'
            } catch {
                $copiedWithoutElevation = $false
            }
            if (-not $copiedWithoutElevation) {
                if ($lockNeedsUpdate) {
                    Write-Host 'Updating the installed model lock from the 1.0.5 source package. Windows may request administrator approval for this short copy.' -ForegroundColor Yellow
                    $copyResult = Join-Path $userRoot 'state\copy-deployed-result.json'
                    Invoke-FlashNextElevated -ScriptPath (Join-Path $sourceRoot 'scripts\Copy-DeployedFiles.ps1') -Parameters @{
                        ResultPath = $copyResult
                        ParentLogPath = $script:FlashNextLogPath
                        SourceRoot = $sourceRoot
                        ApplicationCurrent = $appCurrent
                    } -ResultPath $copyResult -OperationName 'FlashNext model-lock update'
                } else {
                    Write-FlashNextLog 'Program Files downloader is stale but the pinned lock matches; using the user payload copy for this installer run.' 'WARN'
                }
            }
        }
        Invoke-FlashNextNative -FilePath $managerExe -Arguments @('--set-model-directory',$ModelDirectory) -WorkingDirectory $appCurrent | Out-Null
        $verify = Invoke-FlashNextNative -FilePath $managerExe -Arguments @('--verify-model') -WorkingDirectory $appCurrent -PassThru -IgnoreExitCode
        if ($verify.ExitCode -ne 0) {
            Write-Host ''
            Write-Host 'Model disclosure:' -ForegroundColor Yellow
            Write-Host 'The factory GGUF is Unsloth UD-Q4_K_XL with its shared Q8_0 MTP model and F16 vision projector, pinned to one immutable revision.'
            Write-Host 'It does not include a vision tower and is not guaranteed to match mainstream IQ4 or unquantized quality.'
            if (-not $AcceptLicense) {
                $typed = Read-Host 'Type ACCEPT to accept Qwen Community License 1.0 and download the pinned model'
                if ($typed -cne 'ACCEPT') { throw 'Model license was not accepted. Installation stopped before model download.' }
            }
            $payloadRoot = Join-Path $userRoot 'payload'
            New-Item -ItemType Directory -Path $payloadRoot -Force | Out-Null
            $payloadLock = Join-Path $payloadRoot 'model.lock.json'
            $payloadDownloader = Join-Path $payloadRoot 'model_download.py'
            Copy-Item -LiteralPath $sourceLock -Destination $payloadLock -Force
            Copy-Item -LiteralPath $sourceDownloader -Destination $payloadDownloader -Force
            $cache = Join-Path $userRoot 'download-cache'
            $downloadLog = Join-Path $userRoot ('logs\model-download-' + (Get-Date -Format 'yyyy-MM-dd') + '.log')
            Write-Host 'Downloading and verifying the pinned model files. Large transfers can take a long time; progress is written to the install log every 15 seconds.' -ForegroundColor Cyan
            $previousPythonUnbuffered = $env:PYTHONUNBUFFERED
            $previousDownloadLog = $env:FLASHNEXT_DOWNLOAD_LOG
            $env:PYTHONUNBUFFERED = '1'
            $env:FLASHNEXT_DOWNLOAD_LOG = $downloadLog
            try {
                $download = Invoke-FlashNextNative -FilePath $pythonExe -Arguments @('-u',$payloadDownloader,'--mode','download','--lock',$payloadLock,'--destination',$ModelDirectory,'--cache',$cache,'--workers','2','--accept-license') -WorkingDirectory $payloadRoot -PassThru -HeartbeatSeconds 15 -Stage 'model-download'
            } finally {
                if ($null -eq $previousPythonUnbuffered -or $previousPythonUnbuffered -eq '') { Remove-Item Env:PYTHONUNBUFFERED -ErrorAction SilentlyContinue } else { $env:PYTHONUNBUFFERED = $previousPythonUnbuffered }
                if ($null -eq $previousDownloadLog -or $previousDownloadLog -eq '') { Remove-Item Env:FLASHNEXT_DOWNLOAD_LOG -ErrorAction SilentlyContinue } else { $env:FLASHNEXT_DOWNLOAD_LOG = $previousDownloadLog }
            }
            if ($download.StandardOutput) { Write-Host $download.StandardOutput }
            if ($download.StandardError) { Write-Host $download.StandardError }
        } else {
            Write-FlashNextLog 'Existing model files passed immutable verification.'
        }
        $finalVerify = Invoke-FlashNextNative -FilePath $managerExe -Arguments @('--verify-model') -WorkingDirectory $appCurrent -PassThru -IgnoreExitCode
        if ($finalVerify.ExitCode -ne 0) { throw 'Model verification failed after acquisition or repair.' }
        $smokeReport = Join-Path $userRoot 'factory-smoke-test.json'
        Write-Host 'Loading the pinned model and validating health, API authentication, one completion, MTP counters, and telemetry.' -ForegroundColor Cyan
        $smoke = Invoke-FlashNextNative -FilePath $managerExe -Arguments @('--smoke-test',$smokeReport) -WorkingDirectory $appCurrent -PassThru -IgnoreExitCode
        if ($smoke.ExitCode -ne 0) { throw "Factory model smoke test failed. Report: $smokeReport" }
        Set-FlashNextInstallStage -StatePath $statePath -Stage 'model-complete' -Additional @{ modelDirectory = $ModelDirectory; licenseAccepted = $true; smokeReport = $smokeReport }
        $state = Read-FlashNextJson -Path $statePath
    }

    $shortcut = New-FlashNextShortcut -TargetPath $managerExe
    if ($SkipModel) {
        Set-FlashNextInstallStage -StatePath $statePath -Stage 'hardware-complete' -Additional @{ shortcut = $shortcut; modelSkipped = $true }
        Write-Host 'Manager and runtime installation completed without a model. Use menu option 6 to finish model acquisition.' -ForegroundColor Yellow
    } else {
        Set-FlashNextInstallStage -StatePath $statePath -Stage 'complete' -Additional @{ shortcut = $shortcut; completedAtUtc = [DateTimeOffset]::UtcNow.ToString('o') }
        Write-Host ''
        Write-Host 'FlashNext installation completed successfully.' -ForegroundColor Green
        Write-Host ('Manager: {0}' -f $managerExe)
        Write-Host ('Shortcut: {0}' -f $shortcut)
        Write-Host 'Factory API: http://127.0.0.1:8080/v1 (API key required)'
    }
    if (-not $NoLaunch) {
        $launch = Read-Host 'Launch FlashNext Manager now? [Y/n]'
        if (-not $launch -or $launch -match '^(?i)y(es)?$') { Start-Process -FilePath $managerExe -WorkingDirectory $appCurrent | Out-Null }
    }
    exit 0
}
catch {
    Write-FlashNextLog -Message $_.Exception.Message -Level 'ERROR'
    Write-Host ''
    Write-Host ('Installation stopped: {0}' -f $_.Exception.Message) -ForegroundColor Red
    Write-Host ('Log: {0}' -f $script:FlashNextLogPath) -ForegroundColor DarkGray
    Write-Host 'Run diagnose-install.cmd to collect the stage reports and relevant log tails.' -ForegroundColor DarkGray
    exit 1
}
