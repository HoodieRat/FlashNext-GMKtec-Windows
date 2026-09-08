[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$PublishRoot,
    [switch]$Apply,
    [string]$InventoryHash,
    [string]$ResultPath
)

$ErrorActionPreference = 'Stop'
$PublishRoot = [IO.Path]::GetFullPath($PublishRoot).TrimEnd('\')
$inventoryPath = $PublishRoot + '.inventory.json'

function Get-Sha256([string]$Path) {
    $stream = [IO.File]::OpenRead([IO.Path]::GetFullPath($Path))
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $algorithm.Dispose(); $stream.Dispose() }
}

function Assert-NoLinks([string]$Root) {
    $ancestor = Get-Item -LiteralPath $Root -Force
    while ($null -ne $ancestor) {
        if ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point rejected: $($ancestor.FullName)" }
        $ancestor = $ancestor.Parent
    }
    foreach ($item in Get-ChildItem -LiteralPath $Root -Recurse -Force) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point rejected: $($item.FullName)" }
    }
}

function Get-Inventory([string]$Root) {
    @(Get-ChildItem -LiteralPath $Root -File -Recurse -Force | Sort-Object FullName | ForEach-Object {
        [pscustomobject]@{
            path = $_.FullName.Substring($Root.Length + 1)
            bytes = $_.Length
            sha256 = Get-Sha256 $_.FullName
        }
    })
}

function Assert-Inventory([string]$Root, $Expected) {
    Assert-NoLinks $Root
    $actual = @(Get-Inventory $Root)
    if ($actual.Count -ne $Expected.Count) { throw "File count differs in $Root" }
    for ($i = 0; $i -lt $actual.Count; $i++) {
        if ($actual[$i].path -cne $Expected[$i].path -or $actual[$i].bytes -ne $Expected[$i].bytes -or $actual[$i].sha256 -ne $Expected[$i].sha256) {
            throw "File verification failed in $Root at $($actual[$i].path)"
        }
    }
}

Assert-NoLinks $PublishRoot
foreach ($file in @('FlashNext.Dashboard.exe','FlashNext.Dashboard.dll','FlashNext.Manager.exe','FlashNext.Manager.dll','FlashNext.Core.dll','FlashNext.Infrastructure.Windows.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $PublishRoot $file) -PathType Leaf)) { throw "Missing staged application file: $file" }
}
if (-not $Apply) {
    [IO.File]::WriteAllText($inventoryPath, (ConvertTo-Json -InputObject @(Get-Inventory $PublishRoot) -Depth 4), [Text.UTF8Encoding]::new($false))
    [pscustomobject]@{ Path = $inventoryPath; Hash = Get-Sha256 $inventoryPath }
    exit 0
}

$appContainer = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'FlashNextManager\app'))
$current = Join-Path $appContainer 'current'
$updateId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N')
$incoming = Join-Path $appContainer ('incoming-app-update-' + $updateId)
$backup = Join-Path $appContainer ('before-app-update-' + $updateId)
$failedCopy = Join-Path $appContainer ('failed-app-update-' + $updateId)
$oldMoved = $false
$newActive = $false
$result = @{ status = 'failed'; stage = 'app-update'; logPath = $ResultPath; current = $current; backup = $backup }
$exitCode = 1
try {
    if ([string]::IsNullOrWhiteSpace($ResultPath)) { throw 'A result path is required.' }
    if ((Get-Sha256 $inventoryPath) -ne $InventoryHash) { throw 'The prepared inventory has changed.' }
    $expected = Get-Content -LiteralPath $inventoryPath -Raw | ConvertFrom-Json
    Assert-Inventory $PublishRoot $expected
    New-Item -ItemType Directory -Path $appContainer -Force | Out-Null
    Assert-NoLinks $appContainer
    # All directory moves are constrained to this explicitly named installation's app folder.
    foreach ($target in @($current, $incoming, $backup, $failedCopy)) {
        if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($target)) -ne $appContainer) { throw "Unsafe update target: $target" }
    }
    foreach ($process in Get-Process -Name 'FlashNext.Dashboard','FlashNext.Manager' -ErrorAction SilentlyContinue) {
        if ($process.Path -and $process.Path.StartsWith($appContainer + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Close the installed $($process.ProcessName) before applying the update."
        }
    }
    New-Item -ItemType Directory -Path $incoming | Out-Null
    Get-ChildItem -LiteralPath $PublishRoot -Force | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $incoming -Recurse -Force }
    Assert-Inventory $incoming $expected
    if (Test-Path -LiteralPath $current -PathType Container) {
        [IO.Directory]::Move($current, $backup)
        $oldMoved = $true
    }
    [IO.Directory]::Move($incoming, $current)
    $newActive = $true
    Assert-Inventory $current $expected
    $result.status = 'succeeded'
    $result.message = 'Current-user application updated and verified. Runtime, model, and settings were not modified.'
    $exitCode = 0
}
catch {
    $result.message = $_.Exception.Message
    try {
        if ($newActive) { [IO.Directory]::Move($current, $failedCopy) }
        if ($oldMoved) { [IO.Directory]::Move($backup, $current) }
    }
    catch { $result.rollbackError = $_.Exception.Message }
}
finally {
    if ($ResultPath) {
        [IO.File]::WriteAllText([IO.Path]::GetFullPath($ResultPath), ($result | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
    }
}
exit $exitCode
