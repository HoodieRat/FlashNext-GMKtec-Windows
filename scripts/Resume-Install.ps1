[CmdletBinding()]
param(
    [string]$ModelDirectory,
    [switch]$AcceptLicense,
    [switch]$SkipModel,
    [switch]$ForceDependencyRepair,
    [switch]$NoLaunch
)
& (Join-Path $PSScriptRoot 'Install-FlashNext.ps1') @PSBoundParameters
exit $LASTEXITCODE
