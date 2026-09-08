[CmdletBinding()]
param([switch]$KeepUserData, [switch]$DeleteModels, [switch]$Force)
. (Join-Path $PSScriptRoot 'Common.ps1')
$userRoot = Get-FlashNextUserRoot
Initialize-FlashNextLogging -Root $userRoot -Name 'uninstall'
$applicationRoot = Join-Path $env:ProgramFiles 'FlashNextManager'
$settingsPath = Join-Path $userRoot 'config\settings.json'
$modelDirectory = $null
$settings = Read-FlashNextJson -Path $settingsPath
if ($settings -and $settings.paths -and $settings.paths.modelDirectory) { $modelDirectory = [string]$settings.paths.modelDirectory }

try {
    if (-not $Force) {
        $typed = Read-Host 'Type UNINSTALL FLASHNEXT to remove the manager, runtime, firewall rule, and project-owned machine files'
        if ($typed -cne 'UNINSTALL FLASHNEXT') { Write-Host 'Uninstall cancelled.'; exit 0 }
    }

    $statePath = Join-Path $userRoot 'state\server-state.json'
    $serverState = Read-FlashNextJson -Path $statePath
    if ($serverState -and $serverState.isOwnedProcess -and $serverState.processId) {
        $pidValue = [int]$serverState.processId
        $processInfo = Get-CimInstance Win32_Process -Filter ("ProcessId=" + $pidValue) -ErrorAction SilentlyContinue
        if ($processInfo -and $processInfo.ExecutablePath -and [string]$processInfo.ExecutablePath -like ($applicationRoot + '*')) {
            Stop-Process -Id $pidValue -Force -ErrorAction SilentlyContinue
        }
    }
    foreach ($processInfo in Get-CimInstance Win32_Process -Filter "Name='FlashNext.Manager.exe' OR Name='FlashNext.Dashboard.exe'" -ErrorAction SilentlyContinue) {
        if ($processInfo.ExecutablePath -and [string]$processInfo.ExecutablePath -like ($applicationRoot + '*')) { Stop-Process -Id ([int]$processInfo.ProcessId) -Force -ErrorAction SilentlyContinue }
    }

    $helper = Join-Path $PSScriptRoot 'Uninstall-MachineComponents.ps1'
    Invoke-FlashNextElevated -ScriptPath $helper -Parameters @{ ApplicationRoot = $applicationRoot } -OperationName 'FlashNext machine removal'

    $shortcutFolder = Join-Path ([Environment]::GetFolderPath('Programs')) 'FlashNext'
    if (Test-Path -LiteralPath $shortcutFolder) { Remove-Item -LiteralPath $shortcutFolder -Recurse -Force }

    if ($DeleteModels -and $modelDirectory) {
        $modelDirectory = [System.IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($modelDirectory))
        $installedLock = Join-Path $modelDirectory '.flashnext-model.lock.json'
        if (-not (Test-Path -LiteralPath $installedLock)) { throw 'Model deletion refused because the installed immutable model lock is absent.' }
        $lock = Read-FlashNextJson -Path $installedLock
        if ($lock.repository -ne 'unsloth/Qwen3.8-Flash-Next-GGUF' -or $lock.revision -ne '38bb39ee97821de2c9009abb7e93950eec396e66') { throw 'Model deletion refused because the directory does not match the factory immutable model identity.' }
        $typedModel = Read-Host ("Type DELETE MODELS to permanently delete '" + $modelDirectory + "'")
        if ($typedModel -ceq 'DELETE MODELS') { Remove-Item -LiteralPath $modelDirectory -Recurse -Force; Write-Host 'Verified factory model files were deleted.' }
        else { Write-Host 'Model files were preserved.' }
    } elseif ($modelDirectory) {
        Write-Host ("Model files were preserved at '" + $modelDirectory + "'.")
    }

    if (-not $KeepUserData -and (Test-Path -LiteralPath $userRoot)) {
        $removeUser = $Force
        if (-not $Force) { $removeUser = ((Read-Host 'Delete FlashNext settings, logs, conversations, metrics, Python environment, build cache, and download cache? [y/N]') -match '^(?i)y(es)?$') }
        if ($removeUser) { Remove-Item -LiteralPath $userRoot -Recurse -Force }
    }
    Write-Host 'FlashNext uninstall completed.' -ForegroundColor Green
    exit 0
}
catch {
    Write-FlashNextLog -Message $_.Exception.Message -Level 'ERROR'
    Write-Host ('Uninstall failed: ' + $_.Exception.Message) -ForegroundColor Red
    exit 1
}
