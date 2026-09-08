[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$SourceRoot,
    [Parameter(Mandatory=$true)][string]$WinGetPath,
    [Parameter(Mandatory=$true)][string]$ReportPath,
    [string]$ParentLogPath,
    [switch]$ForceDependencyRepair
)

. (Join-Path $PSScriptRoot 'Common.ps1')
. (Join-Path $PSScriptRoot 'Dependency-Probes.ps1')
Initialize-FlashNextLogging -Root (Get-FlashNextUserRoot) -Name 'dependencies' -MirrorPath $ParentLogPath
$script:DependencyStage = 'startup'

function Set-DependencyStage {
    param([Parameter(Mandatory=$true)][string]$Name)
    $script:DependencyStage = $Name
    Write-FlashNextLog ("Dependency stage: {0}" -f $Name)
}

function Get-FlashNextWinGetDiagnosticTail {
    param([Parameter(Mandatory=$true)][string]$WinGet, [Parameter(Mandatory=$true)][string]$PackageId)
    try {
        $result = Invoke-FlashNextNative -FilePath $WinGet -Arguments @('list','--id',$PackageId,'--exact','--source','winget','--accept-source-agreements','--disable-interactivity') -PassThru -IgnoreExitCode
        return (Get-FlashNextTextTail -Text (($result.StandardOutput + [Environment]::NewLine + $result.StandardError).Trim()) -MaximumLines 20 -MaximumCharacters 4000)
    }
    catch {
        return ('WinGet diagnostic query failed: {0}' -f $_.Exception.Message)
    }
}

function Invoke-FlashNextPackageInstall {
    param(
        [Parameter(Mandatory=$true)][string]$WinGet,
        [Parameter(Mandatory=$true)]$Package,
        [Parameter(Mandatory=$true)][bool]$WasPresent,
        [switch]$Force
    )
    $verb = if ($WasPresent -and -not $Force -and -not ($Package.PSObject.Properties.Name -contains 'override' -and $Package.override)) { 'upgrade' } else { 'install' }
    $arguments = @($verb,'--id',[string]$Package.id,'--exact','--version',[string]$Package.version,'--source','winget','--accept-package-agreements','--accept-source-agreements','--disable-interactivity','--silent')
    if ($Force -or ($Package.PSObject.Properties.Name -contains 'override' -and $Package.override)) { $arguments += '--force' }
    if ($Package.PSObject.Properties.Name -contains 'override' -and $Package.override) { $arguments += @('--override',[string]$Package.override) }
    return (Invoke-FlashNextNative -FilePath $WinGet -Arguments $arguments -PassThru -IgnoreExitCode)
}

function Test-FlashNextRebootExitCode {
    param([int]$ExitCode)
    return ($ExitCode -eq 1641 -or $ExitCode -eq 3010)
}

try {
    $SourceRoot = [System.IO.Path]::GetFullPath($SourceRoot)
    $ReportPath = [System.IO.Path]::GetFullPath($ReportPath)

    Set-DependencyStage -Name 'probe-self-test'
    if (-not (Test-FlashNextDependencyProbeHelpers)) { throw 'Dependency probe helper self-test did not complete.' }

    Set-DependencyStage -Name 'manifest-validation'
    $dependencyLockPath = Join-Path $SourceRoot 'manifests\dependencies.lock.json'
    if (-not (Test-Path -LiteralPath $dependencyLockPath -PathType Leaf)) { throw "Dependency lock is missing: $dependencyLockPath" }
    $dependencyLock = Read-FlashNextJson -Path $dependencyLockPath
    if (-not $dependencyLock -or -not $dependencyLock.packages) { throw 'Dependency lock could not be read or contains no packages.' }

    Set-DependencyStage -Name 'winget-validation'
    $winget = Resolve-FlashNextWinGetPath -PreferredPath $WinGetPath
    if (-not $winget) { throw "WinGet could not be started from '$WinGetPath'." }
    $wingetReady = Initialize-FlashNextWinGetSource -WinGetPath $winget
    $packageRecords = @()

    foreach ($package in $dependencyLock.packages) {
        $id = [string]$package.id
        $version = [string]$package.version
        $minimumVersion = Get-FlashNextPackageMinimumVersion -Package $package
        Set-DependencyStage -Name ("package:{0}" -f $id)
        Refresh-FlashNextPath
        $before = Get-FlashNextDependencyProbe -Package $package
        $installAttempts = @()
        $action = 'already-compatible'

        if ($before.compatible -and -not $ForceDependencyRepair) {
            Write-FlashNextLog ("{0} is already compatible. Direct probe: version={1}; path={2}" -f $id, $before.installedVersion, $before.executablePath)
            $after = $before
        }
        else {
            $reason = if ($before.present) { "detected version '$($before.installedVersion)' or incomplete required components" } else { 'not detected by the direct capability probe' }
            Write-Host ("Installing {0} {1}; {2}. Windows may request administrator approval for the signed package." -f $package.name, $version, $reason) -ForegroundColor Cyan
            $firstForce = [bool]$ForceDependencyRepair -or [bool]($package.PSObject.Properties.Name -contains 'override' -and $package.override)
            $firstInstall = Invoke-FlashNextPackageInstall -WinGet $winget -Package $package -WasPresent ([bool]$before.present) -Force:$firstForce
            $firstAttemptKind = if ($firstForce) { 'force-install' } elseif ($before.present) { 'upgrade' } else { 'install' }
            $installAttempts += [pscustomobject]@{
                kind = $firstAttemptKind
                exitCode = [int]$firstInstall.ExitCode
                outputTail = Get-FlashNextTextTail -Text (($firstInstall.StandardOutput + [Environment]::NewLine + $firstInstall.StandardError).Trim()) -MaximumLines 30 -MaximumCharacters 6000
            }
            if (Test-FlashNextRebootExitCode -ExitCode ([int]$firstInstall.ExitCode)) {
                $rebootMessage = "$id requested a reboot. Restart Windows, then rerun install.cmd; directly verified dependencies will be skipped."
                $rebootReport = [pscustomobject]@{
                    schemaVersion = 2
                    status = 'reboot-required'
                    projectRevision = '1.0.5'
                    sourceRoot = $SourceRoot
                    stage = $script:DependencyStage
                    message = $rebootMessage
                    package = $id
                    version = $version
                    attempts = $installAttempts
                    logPath = $script:FlashNextLogPath
                    timestampUtc = [DateTimeOffset]::UtcNow.ToString('o')
                }
                Write-FlashNextJsonAtomic -Path $ReportPath -Value $rebootReport
                Write-FlashNextLog $rebootMessage 'WARN'
                return
            }

            $after = Wait-FlashNextDependencyProbe -Package $package -TimeoutSeconds 30 -IntervalSeconds 2
            $action = if ($before.present) { 'updated' } else { 'installed' }

            if (-not $after.compatible -and -not $firstForce) {
                Write-FlashNextLog ("The normal WinGet operation did not produce a compatible direct probe for {0}. Performing one bounded force-repair attempt." -f $id) 'WARN'
                $repair = Invoke-FlashNextPackageInstall -WinGet $winget -Package $package -WasPresent ([bool]$after.present) -Force
                $installAttempts += [pscustomobject]@{
                    kind = 'force-repair'
                    exitCode = [int]$repair.ExitCode
                    outputTail = Get-FlashNextTextTail -Text (($repair.StandardOutput + [Environment]::NewLine + $repair.StandardError).Trim()) -MaximumLines 30 -MaximumCharacters 6000
                }
                if (Test-FlashNextRebootExitCode -ExitCode ([int]$repair.ExitCode)) {
                    $rebootMessage = "$id requested a reboot during repair. Restart Windows, then rerun install.cmd."
                    $rebootReport = [pscustomobject]@{
                        schemaVersion = 2
                        status = 'reboot-required'
                        projectRevision = '1.0.5'
                        sourceRoot = $SourceRoot
                        stage = $script:DependencyStage
                        message = $rebootMessage
                        package = $id
                        version = $version
                        attempts = $installAttempts
                        logPath = $script:FlashNextLogPath
                        timestampUtc = [DateTimeOffset]::UtcNow.ToString('o')
                    }
                    Write-FlashNextJsonAtomic -Path $ReportPath -Value $rebootReport
                    Write-FlashNextLog $rebootMessage 'WARN'
                    return
                }
                $after = Wait-FlashNextDependencyProbe -Package $package -TimeoutSeconds 30 -IntervalSeconds 2
                $action = 'repaired'
            }

            if (-not $after.compatible) {
                $wingetTail = Get-FlashNextWinGetDiagnosticTail -WinGet $winget -PackageId $id
                $attemptTail = (@($installAttempts | ForEach-Object { "[$($_.kind)] exit=$($_.exitCode)`n$($_.outputTail)" }) -join [Environment]::NewLine)
                throw ("{0} did not pass its direct capability check after WinGet completed. Required minimum: {1}. Probe evidence: {2}. WinGet diagnostic output: {3}. Install attempts: {4}" -f $id, $minimumVersion, $after.evidence, $wingetTail, $attemptTail)
            }

            foreach ($attempt in $installAttempts) {
                if ([int]$attempt.exitCode -ne 0) {
                    Write-FlashNextLog ("WinGet returned exit code {0} during {1} for {2}, but the direct capability probe passed; continuing with the verified tool." -f $attempt.exitCode, $attempt.kind, $id) 'WARN'
                }
            }
        }

        if ([string]$id -eq 'KhronosGroup.VulkanSDK' -and $after.rootPath) {
            $env:VULKAN_SDK = [string]$after.rootPath
            $vulkanBin = Join-Path ([string]$after.rootPath) 'Bin'
            if ((Test-Path -LiteralPath $vulkanBin -PathType Container) -and ($env:Path -notlike ($vulkanBin + '*'))) { $env:Path = $vulkanBin + ';' + $env:Path }
        }

        $signatureStatus = $null
        $signerSubject = $null
        if ($after.executablePath -and (Test-Path -LiteralPath ([string]$after.executablePath) -PathType Leaf)) {
            try {
                $signature = Get-AuthenticodeSignature -LiteralPath ([string]$after.executablePath)
                $signatureStatus = [string]$signature.Status
                if ($signature.SignerCertificate) { $signerSubject = [string]$signature.SignerCertificate.Subject }
            }
            catch {
                $signatureStatus = 'Unavailable'
            }
        }

        Write-FlashNextLog ("Verified {0}: version={1}; path={2}; evidence={3}" -f $id, $after.installedVersion, $after.executablePath, $after.evidence)
        $packageRecords += [pscustomobject]@{
            id = $id
            requestedVersion = $version
            minimumCompatibleVersion = $minimumVersion
            installedVersion = [string]$after.installedVersion
            executablePath = [string]$after.executablePath
            rootPath = [string]$after.rootPath
            commandPrefix = @($after.commandPrefix)
            action = $action
            capabilityVerified = [bool]$after.compatible
            probeEvidence = [string]$after.evidence
            signatureStatus = $signatureStatus
            signerSubject = $signerSubject
            manifestCommit = [string]$package.manifestCommit
            installAttempts = @($installAttempts)
        }
    }

    Set-DependencyStage -Name 'complete'
    $report = [pscustomobject]@{
        schemaVersion = 2
        status = 'succeeded'
        projectRevision = '1.0.5'
        sourceRoot = $SourceRoot
        completedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        verificationMethod = 'direct executable, version, architecture, workload, SDK, header, library, and compiler capability probes; WinGet table output is diagnostic only'
        wingetPath = $winget
        wingetVersion = $wingetReady.Version
        packages = $packageRecords
        logPath = $script:FlashNextLogPath
    }
    Write-FlashNextJsonAtomic -Path $ReportPath -Value $report
    Write-FlashNextLog 'Dependency installation completed. Every dependency passed a direct capability probe.'
}
catch {
    $failure = $_
    $message = $failure.Exception.Message
    Write-FlashNextLog -Message ("Dependency stage '{0}' failed: {1}" -f $script:DependencyStage, $message) -Level 'ERROR'
    $existingReport = Read-FlashNextJson -Path $ReportPath
    if (-not $existingReport -or [string]$existingReport.status -ne 'reboot-required') {
        $report = [pscustomobject]@{
            schemaVersion = 2
            status = 'failed'
            projectRevision = '1.0.5'
            sourceRoot = $SourceRoot
            stage = $script:DependencyStage
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
