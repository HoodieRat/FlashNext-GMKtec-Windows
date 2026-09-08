[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$SourceRoot,
    [Parameter(Mandatory=$true)][string]$StagingRoot,
    [Parameter(Mandatory=$true)][string]$PythonEnvironmentRoot,
    [Parameter(Mandatory=$true)][string]$DependencyReportPath,
    [Parameter(Mandatory=$true)][string]$BuildResultPath,
    [Parameter(Mandatory=$true)][string]$PythonPath,
    [string[]]$PythonPrefix = @(),
    [Parameter(Mandatory=$true)][string]$DotNetPath,
    [Parameter(Mandatory=$true)][string]$GitPath,
    [Parameter(Mandatory=$true)][string]$CMakePath,
    [Parameter(Mandatory=$true)][string]$VulkanSdkRoot,
    [string]$ParentLogPath
)

. (Join-Path $PSScriptRoot 'Common.ps1')
Initialize-FlashNextLogging -Root (Get-FlashNextUserRoot) -Name 'build-install' -MirrorPath $ParentLogPath
$script:BuildInstallStage = 'startup'

function Set-BuildInstallStage {
    param([Parameter(Mandatory=$true)][string]$Name)
    $script:BuildInstallStage = $Name
    Write-FlashNextLog ("Build stage: {0}" -f $Name)
}


function Get-FlashNextBuildDependencyRecord {
    param([Parameter(Mandatory=$true)]$Report, [Parameter(Mandatory=$true)][string]$PackageId)
    $records = @($Report.packages | Where-Object { [string]$_.id -eq $PackageId } | Select-Object -First 1)
    if ($records.Count -eq 0) { return $null }
    return $records[0]
}

function Get-FlashNextBuildInventory {
    param([Parameter(Mandatory=$true)][string]$Root)
    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\')
    $records = @()
    foreach ($file in Get-ChildItem -LiteralPath $rootFull -File -Force -Recurse | Sort-Object FullName) {
        $relative = $file.FullName.Substring($rootFull.Length).TrimStart('\').Replace('\','/')
        $records += [pscustomobject]@{
            path = $relative
            bytes = [int64]$file.Length
            sha256 = Get-FlashNextFileSha256 -Path $file.FullName
        }
    }
    if ($records.Count -eq 0) { throw "Cannot create an inventory for an empty directory: $rootFull" }
    return @($records)
}

try {
    $SourceRoot = [System.IO.Path]::GetFullPath($SourceRoot)
    $StagingRoot = [System.IO.Path]::GetFullPath($StagingRoot)
    $PythonEnvironmentRoot = [System.IO.Path]::GetFullPath($PythonEnvironmentRoot)
    $DependencyReportPath = [System.IO.Path]::GetFullPath($DependencyReportPath)
    $BuildResultPath = [System.IO.Path]::GetFullPath($BuildResultPath)
    $python = [System.IO.Path]::GetFullPath($PythonPath)
    $dotnet = [System.IO.Path]::GetFullPath($DotNetPath)
    $git = [System.IO.Path]::GetFullPath($GitPath)
    $cmake = [System.IO.Path]::GetFullPath($CMakePath)
    $vulkanSdk = [System.IO.Path]::GetFullPath($VulkanSdkRoot)

    Set-BuildInstallStage -Name 'input-validation'
    foreach ($requiredPath in @($python,$dotnet,$git,$cmake)) {
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) { throw "A validated build command is no longer available: $requiredPath" }
    }
    if (-not (Test-Path -LiteralPath $vulkanSdk -PathType Container)) { throw "The validated Vulkan SDK root is no longer available: $vulkanSdk" }
    if (-not (Test-Path -LiteralPath $DependencyReportPath -PathType Leaf)) { throw "Dependency report is missing: $DependencyReportPath" }
    $dependencyReport = Read-FlashNextJson -Path $DependencyReportPath
    if (-not $dependencyReport -or [string]$dependencyReport.status -ne 'succeeded') { throw 'The dependency report is not successful.' }

    Set-BuildInstallStage -Name 'toolchain-validation'
    $pythonProbe = @($PythonPrefix) + @('-c','import sys; assert sys.version_info[:2] == (3,13), sys.version')
    Invoke-FlashNextNative -FilePath $python -Arguments $pythonProbe | Out-Null
    $dotnetVersion = Invoke-FlashNextNative -FilePath $dotnet -Arguments @('--version') -PassThru
    if ($dotnetVersion.StandardOutput.Trim() -notmatch '^10\.0\.') { throw "Expected .NET SDK 10; found '$($dotnetVersion.StandardOutput.Trim())'." }
    Invoke-FlashNextNative -FilePath $git -Arguments @('--version') | Out-Null
    Invoke-FlashNextNative -FilePath $cmake -Arguments @('--version') | Out-Null

    $visualStudioRecord = Get-FlashNextBuildDependencyRecord -Report $dependencyReport -PackageId 'Microsoft.VisualStudio.2022.BuildTools'
    if (-not $visualStudioRecord -or -not [bool]$visualStudioRecord.capabilityVerified) { throw 'The dependency report does not contain a verified Visual Studio Build Tools record.' }
    $vswhere = [string]$visualStudioRecord.executablePath
    if (-not $vswhere -or -not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    }
    if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) { throw 'vswhere.exe is missing after Visual Studio Build Tools installation.' }
    $vsResult = Invoke-FlashNextNative -FilePath $vswhere -Arguments @('-latest','-products','*','-requires','Microsoft.VisualStudio.Component.VC.Tools.x86.x64','-property','installationPath') -PassThru
    $vsPath = $vsResult.StandardOutput.Trim()
    $vsVersionResult = Invoke-FlashNextNative -FilePath $vswhere -Arguments @('-latest','-products','*','-requires','Microsoft.VisualStudio.Component.VC.Tools.x86.x64','-property','installationVersion') -PassThru
    $vsInstallationVersion = $vsVersionResult.StandardOutput.Trim()
    if (-not $vsPath -or -not (Test-Path -LiteralPath $vsPath -PathType Container)) { throw 'Visual Studio Build Tools with the x64/x86 C++ compiler component is not installed.' }
    $vcvars = Join-Path $vsPath 'VC\Auxiliary\Build\vcvars64.bat'
    if (-not (Test-Path -LiteralPath $vcvars -PathType Leaf)) { throw 'Visual Studio Build Tools is missing vcvars64.bat.' }
    $msvc = $null
    $clPath = $null
    $msvcRoot = Join-Path $vsPath 'VC\Tools\MSVC'
    foreach ($candidate in @(Get-ChildItem -LiteralPath $msvcRoot -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending)) {
        $candidateCl = Join-Path $candidate.FullName 'bin\Hostx64\x64\cl.exe'
        if (Test-Path -LiteralPath $candidateCl -PathType Leaf) { $msvc = $candidate; $clPath = $candidateCl; break }
    }
    if (-not $msvc -or -not $clPath) { throw 'MSVC x64 compiler tools are missing from Visual Studio Build Tools.' }

    $windowsKitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10'
    $sdkVersion = $null
    foreach ($candidate in @(Get-ChildItem -LiteralPath (Join-Path $windowsKitsRoot 'Include') -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending)) {
        $windowsHeader = Join-Path $candidate.FullName 'um\Windows.h'
        $kernelLibrary = Join-Path (Join-Path $windowsKitsRoot ('Lib\' + $candidate.Name)) 'um\x64\kernel32.lib'
        $resourceCompiler = Join-Path (Join-Path $windowsKitsRoot ('bin\' + $candidate.Name)) 'x64\rc.exe'
        if ((Test-Path -LiteralPath $windowsHeader -PathType Leaf) -and (Test-Path -LiteralPath $kernelLibrary -PathType Leaf) -and (Test-Path -LiteralPath $resourceCompiler -PathType Leaf)) {
            $sdkVersion = $candidate.Name
            break
        }
    }
    if (-not $sdkVersion) { throw 'A complete Windows SDK with Windows.h, kernel32.lib, and rc.exe is missing from the Build Tools installation.' }

    Set-BuildInstallStage -Name 'repository-static-verification'
    $verificationArguments = @($PythonPrefix) + @((Join-Path $SourceRoot 'scripts\verify_repository.py'))
    Invoke-FlashNextNative -FilePath $python -Arguments $verificationArguments -WorkingDirectory $SourceRoot | Out-Null

    Set-BuildInstallStage -Name 'python-environment'
    if (Test-Path -LiteralPath $PythonEnvironmentRoot) {
        $environmentPython = Join-Path $PythonEnvironmentRoot 'Scripts\python.exe'
        $environmentProbe = $null
        if (Test-Path -LiteralPath $environmentPython -PathType Leaf) {
            $environmentProbe = Invoke-FlashNextNative -FilePath $environmentPython -Arguments @('-c','import sys; assert sys.version_info[:2] == (3,13)') -PassThru -IgnoreExitCode
        }
        if (-not $environmentProbe -or $environmentProbe.ExitCode -ne 0) { Remove-Item -LiteralPath $PythonEnvironmentRoot -Recurse -Force }
    }
    if (-not (Test-Path -LiteralPath $PythonEnvironmentRoot)) {
        $venvArguments = @($PythonPrefix) + @('-m','venv',$PythonEnvironmentRoot)
        Invoke-FlashNextNative -FilePath $python -Arguments $venvArguments | Out-Null
    }
    $environmentPython = Join-Path $PythonEnvironmentRoot 'Scripts\python.exe'
    if (-not (Test-Path -LiteralPath $environmentPython -PathType Leaf)) { throw "Python environment was not created at '$PythonEnvironmentRoot'." }
    $wheelhouse = Join-Path (Get-FlashNextUserRoot) 'wheelhouse'
    if (Test-Path -LiteralPath $wheelhouse) { Remove-Item -LiteralPath $wheelhouse -Recurse -Force }
    New-Item -ItemType Directory -Path $wheelhouse -Force | Out-Null
    Invoke-FlashNextNative -FilePath $environmentPython -Arguments @('-m','pip','download','--disable-pip-version-check','--only-binary=:all:','--dest',$wheelhouse,'huggingface_hub[hf_xet]==1.28.0','hf_xet==1.6.0') | Out-Null
    $wheelReport = Join-Path (Get-FlashNextUserRoot) 'python-wheel-lock.json'
    Invoke-FlashNextNative -FilePath $environmentPython -Arguments @((Join-Path $SourceRoot 'scripts\verify_wheelhouse.py'),$wheelhouse,'--report',$wheelReport) | Out-Null
    Invoke-FlashNextNative -FilePath $environmentPython -Arguments @('-m','pip','install','--disable-pip-version-check','--no-index','--find-links',$wheelhouse,'huggingface_hub[hf_xet]==1.28.0','hf_xet==1.6.0') | Out-Null
    Invoke-FlashNextNative -FilePath $environmentPython -Arguments @('-m','pip','check') | Out-Null
    $freeze = Invoke-FlashNextNative -FilePath $environmentPython -Arguments @('-m','pip','freeze','--all') -PassThru
    [System.IO.File]::WriteAllText((Join-Path (Get-FlashNextUserRoot) 'python-environment.lock.txt'), $freeze.StandardOutput, (New-Object System.Text.UTF8Encoding($false)))

    Set-BuildInstallStage -Name 'dotnet-restore'
    Invoke-FlashNextNative -FilePath $dotnet -Arguments @('restore',(Join-Path $SourceRoot 'FlashNext.sln'),'--use-lock-file','--force-evaluate') -WorkingDirectory $SourceRoot | Out-Null

    Set-BuildInstallStage -Name 'dotnet-tests'
    Invoke-FlashNextNative -FilePath $dotnet -Arguments @('test',(Join-Path $SourceRoot 'FlashNext.sln'),'-c','Release','--no-restore','--logger','console;verbosity=normal') -WorkingDirectory $SourceRoot | Out-Null

    Set-BuildInstallStage -Name 'manager-publish'
    if (Test-Path -LiteralPath $StagingRoot) { Remove-Item -LiteralPath $StagingRoot -Recurse -Force }
    $appStaging = Join-Path $StagingRoot 'app'
    $runtimeStaging = Join-Path $StagingRoot 'runtime'
    New-Item -ItemType Directory -Path $appStaging -Force | Out-Null
    New-Item -ItemType Directory -Path $runtimeStaging -Force | Out-Null
    Invoke-FlashNextNative -FilePath $dotnet -Arguments @('publish',(Join-Path $SourceRoot 'src\FlashNext.Manager\FlashNext.Manager.csproj'),'-c','Release','-r','win-x64','--self-contained','true','--no-restore','-o',$appStaging) -WorkingDirectory $SourceRoot -HeartbeatSeconds 20 -Stage $script:BuildInstallStage | Out-Null
    Set-BuildInstallStage -Name 'vulkan-probe-publish'
    $probePublish = Join-Path $StagingRoot 'probe-publish'
    Invoke-FlashNextNative -FilePath $dotnet -Arguments @('publish',(Join-Path $SourceRoot 'src\FlashNext.VulkanProbe\FlashNext.VulkanProbe.csproj'),'-c','Release','-r','win-x64','--self-contained','true','-o',$probePublish) -WorkingDirectory $SourceRoot -HeartbeatSeconds 20 -Stage $script:BuildInstallStage | Out-Null
    $publishedProbe = Join-Path $probePublish 'FlashNext.VulkanProbe.exe'
    if (-not (Test-Path -LiteralPath $publishedProbe -PathType Leaf)) { throw 'Native Vulkan probe publish did not produce FlashNext.VulkanProbe.exe.' }
    Copy-Item -LiteralPath $publishedProbe -Destination (Join-Path $appStaging 'FlashNext.VulkanProbe.exe') -Force
    if (-not (Test-Path -LiteralPath (Join-Path $appStaging 'FlashNext.VulkanProbe.exe') -PathType Leaf)) { throw 'The rebuilt Vulkan probe was not copied into manager staging.' }
    foreach ($directory in @('config','manifests','scripts','integrations','licenses','docs')) {
        Copy-Item -LiteralPath (Join-Path $SourceRoot $directory) -Destination (Join-Path $appStaging $directory) -Recurse -Force
    }
    foreach ($file in @('README.md','THIRD_PARTY_NOTICES.md','LICENSE')) {
        $source = Join-Path $SourceRoot $file
        if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination (Join-Path $appStaging $file) -Force }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $appStaging 'FlashNext.Manager.exe') -PathType Leaf)) { throw 'Self-contained manager publish did not produce FlashNext.Manager.exe.' }
    Set-BuildInstallStage -Name 'dashboard-publish'
    Invoke-FlashNextNative -FilePath $dotnet -Arguments @('publish',(Join-Path $SourceRoot 'src\FlashNext.Dashboard\FlashNext.Dashboard.csproj'),'-c','Release','-r','win-x64','--self-contained','true','--no-restore','-o',$appStaging) -WorkingDirectory $SourceRoot -HeartbeatSeconds 20 -Stage $script:BuildInstallStage | Out-Null
    if (-not (Test-Path -LiteralPath (Join-Path $appStaging 'FlashNext.Dashboard.exe') -PathType Leaf)) { throw 'Self-contained dashboard publish did not produce FlashNext.Dashboard.exe.' }

    Set-BuildInstallStage -Name 'vulkan-runtime-build'
    & (Join-Path $SourceRoot 'scripts\Build-Runtime.ps1') -ApplicationRoot $appStaging -RuntimeRoot $runtimeStaging -ActivateInitial -MirrorLogPath $ParentLogPath -GitPath $git -CMakePath $cmake -VulkanSdkRoot $vulkanSdk -VisualStudioPath $vsPath -VisualStudioVersion $vsInstallationVersion -WorkRoot (Join-Path (Get-FlashNextUserRoot) 'build-cache')
    $runtimeCurrent = Join-Path $runtimeStaging 'current'
    foreach ($binary in @('llama-server.exe','llama-cli.exe','llama-bench.exe','runtime.build.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $runtimeCurrent $binary) -PathType Leaf)) { throw "The staged runtime is missing '$binary'." }
    }

    Set-BuildInstallStage -Name 'artifact-inventory'
    $appInventory = @(Get-FlashNextBuildInventory -Root $appStaging)
    $runtimeInventory = @(Get-FlashNextBuildInventory -Root $runtimeCurrent)

    Set-BuildInstallStage -Name 'build-report'
    $report = [pscustomobject]@{
        schemaVersion = 2
        status = 'succeeded'
        projectRevision = '1.0.5'
        completedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        sourceRoot = $SourceRoot
        stagingRoot = $StagingRoot
        appStaging = $appStaging
        runtimeStaging = $runtimeCurrent
        pythonEnvironment = $PythonEnvironmentRoot
        wheelLock = $wheelReport
        dotnetSdk = $dotnetVersion.StandardOutput.Trim()
        visualStudio = $vsPath
        visualStudioVersion = $vsInstallationVersion
        msvc = $msvc.Name
        windowsSdk = $sdkVersion
        vulkanSdk = $vulkanSdk
        dependencyReport = $DependencyReportPath
        inventories = [pscustomobject]@{
            app = $appInventory
            runtime = $runtimeInventory
        }
        logPath = $script:FlashNextLogPath
    }
    Write-FlashNextJsonAtomic -Path $BuildResultPath -Value $report
    Set-BuildInstallStage -Name 'complete'
    Write-FlashNextLog 'The manager and Vulkan runtime were built and tested without an elevated PowerShell process.'
}
catch {
    $failure = $_
    $message = $failure.Exception.Message
    Write-FlashNextLog -Message ("Build stage '{0}' failed: {1}" -f $script:BuildInstallStage, $message) -Level 'ERROR'
    $report = [pscustomobject]@{
        schemaVersion = 2
        status = 'failed'
        projectRevision = '1.0.5'
        stage = $script:BuildInstallStage
        message = $message
        exceptionType = $failure.Exception.GetType().FullName
        scriptStackTrace = $failure.ScriptStackTrace
        logPath = $script:FlashNextLogPath
        timestampUtc = [DateTimeOffset]::UtcNow.ToString('o')
    }
    Write-FlashNextJsonAtomic -Path $BuildResultPath -Value $report
    throw
}
