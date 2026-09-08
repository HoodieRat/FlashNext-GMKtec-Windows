[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
$sourceRoot = Get-FlashNextRepositoryRoot -ScriptDirectory $PSScriptRoot
Initialize-FlashNextLogging -Root (Get-FlashNextUserRoot) -Name 'copy-lock'
$result = Join-Path (Get-FlashNextUserRoot) 'state\copy-deployed-result.json'
$appCurrent = Join-Path $env:ProgramFiles 'FlashNextManager\app\current'
Invoke-FlashNextElevated -ScriptPath (Join-Path $PSScriptRoot 'Copy-DeployedFiles.ps1') -Parameters @{
    ResultPath = $result
    ParentLogPath = $script:FlashNextLogPath
    SourceRoot = $sourceRoot
    ApplicationCurrent = $appCurrent
} -ResultPath $result -OperationName 'FlashNext model-lock update'
Write-Host 'Updated model lock copied into Program Files.'
exit 0
