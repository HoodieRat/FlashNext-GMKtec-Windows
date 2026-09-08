[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidateSet('Activate','Rollback')][string]$Action,
    [Parameter(Mandatory=$true)][string]$RuntimeRoot,
    [string]$StagingSource
)

. (Join-Path $PSScriptRoot 'Common.ps1')
Initialize-FlashNextLogging -Root (Get-FlashNextMachineRoot) -Name 'runtime-activation'

if (-not (Test-FlashNextAdministrator)) { throw 'Runtime slot activation requires administrator approval.' }
$RuntimeRoot = [System.IO.Path]::GetFullPath($RuntimeRoot)
$expectedRoot = [System.IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'FlashNextManager\runtime'))
if (-not $RuntimeRoot.Equals($expectedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'The runtime root is outside the project-owned installation directory.' }

$current = Join-Path $RuntimeRoot 'current'
$previous = Join-Path $RuntimeRoot 'previous'
$staging = Join-Path $RuntimeRoot 'staging'

if ($Action -eq 'Activate') {
    if (-not [string]::IsNullOrWhiteSpace($StagingSource)) {
        $StagingSource = [System.IO.Path]::GetFullPath($StagingSource)
        if (-not (Test-Path -LiteralPath $StagingSource -PathType Container)) { throw "Runtime staging source is missing: $StagingSource" }
        if ((Get-Item -LiteralPath $StagingSource -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Runtime staging source cannot be a reparse point.' }

        $recordPath = Join-Path $StagingSource 'runtime.build.json'
        if (-not (Test-Path -LiteralPath $recordPath -PathType Leaf)) { throw 'Runtime staging source has no runtime.build.json.' }
        $record = Read-FlashNextJson -Path $recordPath
        $files = @($record.files)
        if ($files.Count -eq 0) { throw 'Runtime staging source has an empty file inventory.' }

        foreach ($entry in $files) {
            $name = [string]$entry.name
            if ([string]::IsNullOrWhiteSpace($name) -or [System.IO.Path]::IsPathRooted($name) -or $name.Contains('\') -or $name.Contains('/')) { throw "Unsafe runtime inventory file name: $name" }
            $sourceFile = Join-Path $StagingSource $name
            if (-not (Test-Path -LiteralPath $sourceFile -PathType Leaf)) { throw "Runtime staging source is missing inventoried file: $name" }
            if ((Get-Item -LiteralPath $sourceFile).Length -ne [int64]$entry.bytes) { throw "Runtime staging source byte count mismatch: $name" }
            if ((Get-FlashNextFileSha256 -Path $sourceFile) -ne ([string]$entry.sha256).ToLowerInvariant()) { throw "Runtime staging source hash mismatch: $name" }
        }

        if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
        New-Item -ItemType Directory -Path $staging -Force | Out-Null
        foreach ($entry in $files) {
            Copy-Item -LiteralPath (Join-Path $StagingSource ([string]$entry.name)) -Destination (Join-Path $staging ([string]$entry.name)) -Force
        }
        Copy-Item -LiteralPath $recordPath -Destination (Join-Path $staging 'runtime.build.json') -Force

        foreach ($entry in $files) {
            $copiedFile = Join-Path $staging ([string]$entry.name)
            if ((Get-FlashNextFileSha256 -Path $copiedFile) -ne ([string]$entry.sha256).ToLowerInvariant()) { throw "Copied runtime hash mismatch: $([string]$entry.name)" }
        }
        Write-FlashNextLog "Copied and verified staged runtime from '$StagingSource'."
    }
    if (-not (Test-Path -LiteralPath (Join-Path $staging 'llama-server.exe'))) { throw 'The staged runtime is incomplete.' }
    if (Test-Path -LiteralPath $previous) { Remove-Item -LiteralPath $previous -Recurse -Force }
    if (Test-Path -LiteralPath $current) { [System.IO.Directory]::Move($current, $previous) }
    try { [System.IO.Directory]::Move($staging, $current) }
    catch {
        if (-not (Test-Path -LiteralPath $current) -and (Test-Path -LiteralPath $previous)) { [System.IO.Directory]::Move($previous, $current) }
        throw
    }
    Write-FlashNextLog "Activated staged runtime at $current."
    exit 0
}

if (-not (Test-Path -LiteralPath (Join-Path $previous 'llama-server.exe'))) { throw 'The previous runtime slot is unavailable or incomplete.' }
$failed = Join-Path $RuntimeRoot ('failed-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
if (Test-Path -LiteralPath $failed) { Remove-Item -LiteralPath $failed -Recurse -Force }
if (Test-Path -LiteralPath $current) { [System.IO.Directory]::Move($current, $failed) }
try { [System.IO.Directory]::Move($previous, $current) }
catch {
    if (-not (Test-Path -LiteralPath $current) -and (Test-Path -LiteralPath $failed)) { [System.IO.Directory]::Move($failed, $current) }
    throw
}
Write-FlashNextLog "Rolled back runtime at $current."
exit 0
