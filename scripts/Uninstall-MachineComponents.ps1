[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$ApplicationRoot)
. (Join-Path $PSScriptRoot 'Common.ps1')
if (-not (Test-FlashNextAdministrator)) { throw 'This helper requires elevation.' }
$ApplicationRoot = [System.IO.Path]::GetFullPath($ApplicationRoot)
$expected = [System.IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'FlashNextManager'))
if ($ApplicationRoot -ne $expected) { throw 'Application root does not match the owned FlashNext path.' }
Get-NetFirewallRule -DisplayName 'FlashNextManager-API' -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
Get-ScheduledTask -TaskName 'FlashNext*' -ErrorAction SilentlyContinue | Unregister-ScheduledTask -Confirm:$false -ErrorAction SilentlyContinue
if (Test-Path -LiteralPath $ApplicationRoot) { Remove-Item -LiteralPath $ApplicationRoot -Recurse -Force }
$machineRoot = Get-FlashNextMachineRoot
if (Test-Path -LiteralPath $machineRoot) { Remove-Item -LiteralPath $machineRoot -Recurse -Force }
