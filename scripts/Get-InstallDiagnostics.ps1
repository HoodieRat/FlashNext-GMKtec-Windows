[CmdletBinding()]
param([string]$OutputPath)

. (Join-Path $PSScriptRoot 'Common.ps1')
$userRoot = Get-FlashNextUserRoot
$machineRoot = Get-FlashNextMachineRoot
if (-not $OutputPath) {
    $directory = Join-Path $userRoot 'diagnostics'
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $OutputPath = Join-Path $directory ('install-diagnostic-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.txt')
}
$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$lines = New-Object System.Collections.Generic.List[string]

function Add-DiagnosticLine {
    param([AllowEmptyString()][string]$Text = '')
    [void]$lines.Add($Text)
}

function Add-FileTail {
    param([Parameter(Mandatory=$true)][string]$Label, [AllowNull()][string]$Path, [int]$LineCount = 160)
    Add-DiagnosticLine
    Add-DiagnosticLine ('===== {0} =====' -f $Label)
    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Add-DiagnosticLine 'Not available.'
        return
    }
    Add-DiagnosticLine ('Path: {0}' -f $Path)
    try {
        foreach ($line in @(Get-Content -LiteralPath $Path -Tail $LineCount -Encoding UTF8 -ErrorAction Stop)) { Add-DiagnosticLine $line }
    } catch { Add-DiagnosticLine ('Could not read file: {0}' -f $_.Exception.Message) }
}

try {
    Add-DiagnosticLine 'FlashNext installation diagnostic'
    Add-DiagnosticLine ('Collected UTC: {0:o}' -f [DateTimeOffset]::UtcNow)
    Add-DiagnosticLine ('Windows user: {0}' -f [Security.Principal.WindowsIdentity]::GetCurrent().Name)
    Add-DiagnosticLine ('Administrator token: {0}' -f (Test-FlashNextAdministrator))
    Add-DiagnosticLine ('PowerShell: {0}' -f $PSVersionTable.PSVersion)
    try {
        $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
        Add-DiagnosticLine ('Windows: {0}; build {1}; architecture {2}' -f $os.Caption, $os.BuildNumber, $os.OSArchitecture)
    } catch { Add-DiagnosticLine ('Windows details unavailable: {0}' -f $_.Exception.Message) }

    try {
        $package = Get-AppxPackage -Name 'Microsoft.DesktopAppInstaller' -ErrorAction Stop | Sort-Object Version -Descending | Select-Object -First 1
        Add-DiagnosticLine ('Desktop App Installer: version {0}; location {1}' -f $package.Version, $package.InstallLocation)
    } catch { Add-DiagnosticLine ('Desktop App Installer lookup failed: {0}' -f $_.Exception.Message) }

    $winget = Resolve-FlashNextWinGetPath
    if ($winget) {
        Add-DiagnosticLine ('WinGet executable: {0}' -f $winget)
        try {
            $version = Invoke-FlashNextNative -FilePath $winget -Arguments @('--version') -PassThru -IgnoreExitCode
            Add-DiagnosticLine ('WinGet version exit code: {0}; output: {1}' -f $version.ExitCode, (($version.StandardOutput + $version.StandardError).Trim()))
            $sources = Invoke-FlashNextNative -FilePath $winget -Arguments @('source','list','--disable-interactivity') -PassThru -IgnoreExitCode
            Add-DiagnosticLine ('WinGet source-list exit code: {0}' -f $sources.ExitCode)
            foreach ($line in @(($sources.StandardOutput + [Environment]::NewLine + $sources.StandardError).Trim() -split "`r?`n")) { Add-DiagnosticLine $line }
        } catch { Add-DiagnosticLine ('WinGet probe failed: {0}' -f $_.Exception.Message) }
    } else {
        Add-DiagnosticLine 'WinGet executable could not be resolved.'
    }

    Add-FileTail -Label 'Structured dependency-stage result' -Path (Join-Path $userRoot 'state\dependency-install-result.json') -LineCount 160
    Add-FileTail -Label 'Structured build-stage result' -Path (Join-Path $userRoot 'state\build-install-result.json') -LineCount 160
    Add-FileTail -Label 'Structured machine-stage result' -Path (Join-Path $userRoot 'state\machine-install-result.json') -LineCount 160
    Add-FileTail -Label 'Install state' -Path (Join-Path $userRoot 'install-state.json') -LineCount 160

    $userLog = Get-ChildItem -LiteralPath (Join-Path $userRoot 'logs') -Filter 'install-*.log' -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    Add-FileTail -Label 'Normal-user installer log' -Path $(if ($userLog) { $userLog.FullName } else { $null })

    $dependencyLog = Get-ChildItem -LiteralPath (Join-Path $userRoot 'logs') -Filter 'dependencies-*.log' -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    Add-FileTail -Label 'Dependency installer log' -Path $(if ($dependencyLog) { $dependencyLog.FullName } else { $null })

    $buildLog = Get-ChildItem -LiteralPath (Join-Path $userRoot 'logs') -Filter 'build-install-*.log' -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    Add-FileTail -Label 'Manager build and test log' -Path $(if ($buildLog) { $buildLog.FullName } else { $null })

    $runtimeUserLog = Get-ChildItem -LiteralPath (Join-Path $userRoot 'logs') -Filter 'runtime-build-*.log' -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    Add-FileTail -Label 'Normal-user Vulkan runtime build log' -Path $(if ($runtimeUserLog) { $runtimeUserLog.FullName } else { $null })

    $machineLog = Get-ChildItem -LiteralPath (Join-Path $machineRoot 'logs') -Filter 'machine-deploy-*.log' -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    Add-FileTail -Label 'Administrator deployment log' -Path $(if ($machineLog) { $machineLog.FullName } else { $null })

    $legacyMachineLog = Get-ChildItem -LiteralPath (Join-Path $machineRoot 'logs') -Filter 'machine-install-*.log' -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    Add-FileTail -Label 'Earlier installer administrator log, when present' -Path $(if ($legacyMachineLog) { $legacyMachineLog.FullName } else { $null })

    $outputDirectory = Split-Path -Parent $OutputPath
    if ($outputDirectory) { New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null }
    [System.IO.File]::WriteAllLines($OutputPath, $lines, (New-Object System.Text.UTF8Encoding($false)))
    foreach ($line in $lines) { Write-Host $line }
    Write-Host ''
    Write-Host ('Saved diagnostic: {0}' -f $OutputPath) -ForegroundColor Green
    exit 0
}
catch {
    Write-Host ('Diagnostic collection failed: {0}' -f $_.Exception.Message) -ForegroundColor Red
    exit 1
}
