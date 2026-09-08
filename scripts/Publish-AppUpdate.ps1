[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$SourceRoot,
    [switch]$RestartDashboard,
    [switch]$NoLaunch
)

. (Join-Path $PSScriptRoot 'Common.ps1')
Initialize-FlashNextLogging -Root (Get-FlashNextUserRoot) -Name 'app-update'
$SourceRoot = [IO.Path]::GetFullPath($SourceRoot)
$machineCurrent = Join-Path $env:ProgramFiles 'FlashNextManager\app\current'
$current = Join-Path (Get-FlashNextUserRoot) 'app\current'
$installed = if (Test-Path -LiteralPath $current -PathType Container) { $current } else { $machineCurrent }
$updateRoot = Join-Path (Get-FlashNextUserRoot) ('app-updates\' + [Guid]::NewGuid().ToString('N'))
$publish = Join-Path $updateRoot 'app'
$resultPath = Join-Path $updateRoot 'result.json'
try {
    $dotnet = Find-FlashNextExecutable -Names @('dotnet.exe','dotnet') -AdditionalPaths @((Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'))
    if (-not $dotnet) { throw '.NET SDK 10 is needed to publish the updated application.' }
    foreach ($process in Get-Process -Name 'FlashNext.Dashboard','FlashNext.Manager' -ErrorAction SilentlyContinue) {
        if ($process.Path -and ($process.Path.StartsWith($current + '\', [StringComparison]::OrdinalIgnoreCase) -or $process.Path.StartsWith($machineCurrent + '\', [StringComparison]::OrdinalIgnoreCase)) -and
            (-not $RestartDashboard -or $process.ProcessName -ne 'FlashNext.Dashboard')) {
            throw 'Quit the running FlashNext application and retry, or use install.cmd -RestartDashboard to restart the dashboard during the update. Save unsent text first.'
        }
    }
    Invoke-FlashNextNative -FilePath $dotnet -Arguments @('restore',(Join-Path $SourceRoot 'FlashNext.sln'),'--use-lock-file') -WorkingDirectory $SourceRoot | Out-Null
    foreach ($project in @('FlashNext.UnitTests','FlashNext.DashboardTests','FlashNext.IntegrationTests')) {
        $arguments = @('test',(Join-Path $SourceRoot ('tests\' + $project + '\' + $project + '.csproj')),'-c','Release','--no-restore')
        if ($project -eq 'FlashNext.IntegrationTests') { $arguments += @('--filter','FullyQualifiedName~LlamaApiClientTests|FullyQualifiedName~ControlApiTests|FullyQualifiedName~AtomicLifecycleTests') }
        Invoke-FlashNextNative -FilePath $dotnet -Arguments $arguments -WorkingDirectory $SourceRoot | Out-Null
    }
    # Preserve installed support files; publish both programs against the same shared libraries.
    if ((Get-Item -LiteralPath $installed -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'The installed app directory is a reparse point.' }
    if (@(Get-ChildItem -LiteralPath $installed -Recurse -Force -Attributes ReparsePoint).Count) { throw 'The installed app contains a reparse point.' }
    New-Item -ItemType Directory -Path $publish -Force | Out-Null
    Get-ChildItem -LiteralPath $installed -Force | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $publish -Recurse -Force }
    foreach ($project in @('FlashNext.Manager','FlashNext.Dashboard')) {
        Invoke-FlashNextNative -FilePath $dotnet -Arguments @('publish',(Join-Path $SourceRoot ('src\' + $project + '\' + $project + '.csproj')),'-c','Release','--no-restore','-o',$publish) -WorkingDirectory $SourceRoot | Out-Null
    }
    Get-ChildItem -LiteralPath (Join-Path $SourceRoot 'scripts') -Force | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $publish 'scripts') -Recurse -Force }
    Copy-Item -LiteralPath (Join-Path $SourceRoot 'README.md') -Destination (Join-Path $publish 'README.md') -Force
    $helper = Join-Path $SourceRoot 'scripts\Update-InstalledApp.ps1'
    & $helper -PublishRoot $publish | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not prepare the application inventory.' }
    $inventoryHash = Get-FlashNextFileSha256 ($publish + '.inventory.json')

    if ($RestartDashboard) {
        if (Get-Process -Name 'llama-server' -ErrorAction SilentlyContinue) { throw 'Stop the inference server before restarting the dashboard for an app update.' }
        foreach ($process in Get-Process -Name 'FlashNext.Dashboard' -ErrorAction SilentlyContinue) {
            if ($process.Path -in @((Join-Path $current 'FlashNext.Dashboard.exe'), (Join-Path $machineCurrent 'FlashNext.Dashboard.exe'))) {
                Write-FlashNextLog ('Closing the old dashboard for the requested update. PID: ' + $process.Id)
                Stop-Process -Id $process.Id -ErrorAction Stop
                if (-not $process.WaitForExit(10000)) { throw 'The old dashboard did not exit.' }
            }
        }
    }
    Write-Host 'Installing the verified application for the current user; no administrator approval is needed.' -ForegroundColor Cyan
    & $helper -PublishRoot $publish -Apply -InventoryHash $inventoryHash -ResultPath $resultPath
    if ($LASTEXITCODE -ne 0) {
        $result = Read-FlashNextJson -Path $resultPath
        throw ('Application activation failed: ' + $result.message)
    }
    foreach ($file in @('FlashNext.Dashboard.dll','FlashNext.Manager.dll','FlashNext.Core.dll','FlashNext.Infrastructure.Windows.dll')) {
        if ((Get-FlashNextFileSha256 (Join-Path $current $file)) -ne (Get-FlashNextFileSha256 (Join-Path $publish $file))) { throw "Installed file did not match the publish: $file" }
    }
    New-FlashNextShortcut -TargetPath (Join-Path $current 'FlashNext.Manager.exe') | Out-Null
    Write-FlashNextLog ('Application update verified. run.cmd and the existing shortcut now use: ' + $current)
    if (-not $NoLaunch) { Start-Process -FilePath (Join-Path $current 'FlashNext.Dashboard.exe') -WorkingDirectory $current -WindowStyle Hidden | Out-Null }
    exit 0
}
catch {
    Write-FlashNextLog -Message ('Application update failed: ' + $_.Exception.Message) -Level 'ERROR'
    Write-Host ('Update files and diagnostics: ' + $updateRoot)
    exit 1
}
