[CmdletBinding()]
param([switch]$Hardware, [switch]$Model)
. (Join-Path $PSScriptRoot 'Common.ps1')
$root = Get-FlashNextRepositoryRoot -ScriptDirectory $PSScriptRoot
Initialize-FlashNextLogging -Root (Get-FlashNextUserRoot) -Name 'repository-verification'
$errors = @()
foreach ($script in Get-ChildItem -LiteralPath (Join-Path $root 'scripts') -Filter '*.ps1' -File) {
    $tokens = $null
    $parseErrors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -gt 0) { foreach ($parseError in $parseErrors) { $errors += ($script.Name + ': ' + $parseError.Message) } }
}
if ($errors.Count -gt 0) { throw ('PowerShell parse failures: ' + ($errors -join '; ')) }
Write-Host 'PowerShell parse checks passed.' -ForegroundColor Green

$python = Find-FlashNextExecutable -Names @('python.exe','py.exe')
if (-not $python) { throw 'Python is required for repository verification.' }
$arguments = @()
if ([System.IO.Path]::GetFileName($python) -ieq 'py.exe') { $arguments += '-3.13' }
$arguments += (Join-Path $root 'scripts\verify_repository.py')
Invoke-FlashNextNative -FilePath $python -Arguments $arguments -WorkingDirectory $root | Out-Null

$dotnet = Find-FlashNextExecutable -Names @('dotnet.exe','dotnet')
if (-not $dotnet) { throw '.NET SDK 10 is required for compiled verification.' }
Invoke-FlashNextNative -FilePath $dotnet -Arguments @('restore',(Join-Path $root 'FlashNext.sln'),'--use-lock-file') -WorkingDirectory $root | Out-Null
Invoke-FlashNextNative -FilePath $dotnet -Arguments @('test',(Join-Path $root 'FlashNext.sln'),'-c','Release','--no-restore','--logger','console;verbosity=detailed') -WorkingDirectory $root | Out-Null
Write-Host '.NET build and tests passed.' -ForegroundColor Green

if ($Hardware -or $Model) {
    $manager = Join-Path $env:ProgramFiles 'FlashNextManager\app\current\FlashNext.Manager.exe'
    if (-not (Test-Path -LiteralPath $manager)) { throw 'Installed manager is required for hardware/model verification.' }
    if ($Hardware) {
        $report = Join-Path (Get-FlashNextUserRoot) 'hardware-verification.json'
        Invoke-FlashNextNative -FilePath $manager -Arguments @('--hardware-report',$report) -WorkingDirectory (Split-Path -Parent $manager) | Out-Null
        Write-Host ("Hardware report: " + $report)
    }
    if ($Model) { Invoke-FlashNextNative -FilePath $manager -Arguments @('--verify-model') -WorkingDirectory (Split-Path -Parent $manager) | Out-Null }
}
