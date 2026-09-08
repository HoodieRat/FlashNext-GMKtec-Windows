[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RequestFile)

. (Join-Path $PSScriptRoot 'Common.ps1')
if (-not (Test-FlashNextAdministrator)) { throw 'The firewall helper requires elevation.' }
$ruleName = 'FlashNextManager-API'
$request = $null
$resultPath = $null
try {
    $RequestFile = [System.IO.Path]::GetFullPath($RequestFile)
    $request = Read-FlashNextJson -Path $RequestFile
    if (-not $request) { throw 'LAN request file is invalid.' }
    $resultPath = [System.IO.Path]::GetFullPath([string]$request.resultFile)
    $action = [string]$request.action
    if ($action -notin @('enable','disable')) { throw 'LAN action is invalid.' }
    $program = [System.IO.Path]::GetFullPath([string]$request.program)
    $ownedRoot = [System.IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'FlashNextManager'))
    if (-not $program.StartsWith($ownedRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Firewall program path is outside the FlashNext installation.' }
    if (-not (Test-Path -LiteralPath $program)) { throw 'FlashNext.Manager.exe does not exist at the requested path.' }
    $port = [int]$request.port
    if ($port -lt 1024 -or $port -gt 65535) { throw 'Firewall port is invalid.' }
    $remote = [string]$request.remoteAddress
    if ($remote -ne 'LocalSubnet' -and $remote -notmatch '^[0-9A-Fa-f:.,/\-]+$') { throw 'Remote address must be LocalSubnet, an IP address, or a CIDR range.' }
    Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
    if ($action -eq 'enable') {
        New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow -Protocol TCP -LocalPort $port -Program $program -Profile Private -RemoteAddress $remote -Description 'FlashNext local model API; managed by FlashNext Manager.' | Out-Null
    }
    Write-FlashNextJsonAtomic -Path $resultPath -Value ([pscustomobject]@{ success = $true; action = $action; message = "LAN firewall action '$action' completed."; timestampUtc = [DateTimeOffset]::UtcNow.ToString('o') })
    exit 0
}
catch {
    if ($resultPath) {
        try { Write-FlashNextJsonAtomic -Path $resultPath -Value ([pscustomobject]@{ success = $false; message = $_.Exception.Message; timestampUtc = [DateTimeOffset]::UtcNow.ToString('o') }) } catch { }
    }
    Write-Error $_.Exception.Message
    exit 1
}
