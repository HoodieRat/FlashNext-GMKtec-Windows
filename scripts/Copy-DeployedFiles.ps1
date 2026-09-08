[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ResultPath,
    [Parameter(Mandatory=$true)][string]$ParentLogPath,
    [Parameter(Mandatory=$true)][string]$SourceRoot,
    [Parameter(Mandatory=$true)][string]$ApplicationCurrent,
    [string]$ManagerPublishRoot
)

. (Join-Path $PSScriptRoot 'Common.ps1')
Initialize-FlashNextLogging -Root (Get-FlashNextMachineRoot) -Name 'copy-deployed' -MirrorPath $ParentLogPath

try {
    if (-not (Test-FlashNextAdministrator)) { throw 'This copy helper must run with administrator rights.' }
    $SourceRoot = [System.IO.Path]::GetFullPath($SourceRoot)
    $ApplicationCurrent = [System.IO.Path]::GetFullPath($ApplicationCurrent)
    $pairs = @(
        @{ Source = Join-Path $SourceRoot 'manifests\runtime.lock.json'; Destination = Join-Path $ApplicationCurrent 'manifests\runtime.lock.json' },
        @{ Source = Join-Path $SourceRoot 'manifests\model.lock.json'; Destination = Join-Path $ApplicationCurrent 'manifests\model.lock.json' },
        @{ Source = Join-Path $SourceRoot 'scripts\model_download.py'; Destination = Join-Path $ApplicationCurrent 'scripts\model_download.py' }
    )
    if (-not [string]::IsNullOrWhiteSpace($ManagerPublishRoot)) {
        $ManagerPublishRoot = [System.IO.Path]::GetFullPath($ManagerPublishRoot)
        Get-ChildItem -LiteralPath $ManagerPublishRoot -Force | ForEach-Object {
            $pairs += @{ Source = $_.FullName; Destination = Join-Path $ApplicationCurrent $_.Name }
        }
    }
    foreach ($pair in $pairs) {
        if (-not (Test-Path -LiteralPath $pair.Source)) { throw "Source missing: $($pair.Source)" }
        $destDir = Split-Path -Parent $pair.Destination
        if (-not (Test-Path -LiteralPath $destDir -PathType Container)) { throw "Destination directory missing: $destDir" }
        Copy-Item -LiteralPath $pair.Source -Destination $pair.Destination -Recurse -Force
        if (-not (Test-Path -LiteralPath $pair.Destination)) { throw "Copy did not produce $($pair.Destination)" }
        Write-FlashNextLog ("Copied '{0}' to '{1}'." -f $pair.Source, $pair.Destination)
    }
    Write-FlashNextOperationResult -Path $ResultPath -Status 'succeeded' -Stage 'copy-deployed-files' -Message 'Updated runtime/model locks, downloader, and application files were copied into the installed application directory.' -LogPath $script:FlashNextLogPath
}
catch {
    Write-FlashNextOperationResult -Path $ResultPath -Status 'failed' -Stage 'copy-deployed-files' -Message $_.Exception.Message -LogPath $script:FlashNextLogPath -ExitCode 1
    throw
}
