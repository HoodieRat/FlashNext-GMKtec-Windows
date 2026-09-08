[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ApplicationRoot,
    [Parameter(Mandatory=$true)][string]$RuntimeRoot,
    [switch]$StageOnly,
    [switch]$ActivateInitial,
    [string]$MirrorLogPath,
    [string]$GitPath,
    [string]$CMakePath,
    [string]$VulkanSdkRoot,
    [string]$VisualStudioPath,
    [string]$VisualStudioVersion,
    [string]$WorkRoot
)

. (Join-Path $PSScriptRoot 'Common.ps1')
$logRoot = if (Test-FlashNextAdministrator) { Get-FlashNextMachineRoot } else { Get-FlashNextUserRoot }
Initialize-FlashNextLogging -Root $logRoot -Name 'runtime-build' -MirrorPath $MirrorLogPath
$ApplicationRoot = [System.IO.Path]::GetFullPath($ApplicationRoot)
$RuntimeRoot = [System.IO.Path]::GetFullPath($RuntimeRoot)
$lockPath = Join-Path $ApplicationRoot 'manifests\runtime.lock.json'
if (-not (Test-Path -LiteralPath $lockPath)) { throw "Runtime lock is missing at '$lockPath'." }
$lock = Read-FlashNextJson -Path $lockPath
$repository = [string]$lock.repository
$commit = [string]$lock.commit
if ($commit -notmatch '^[0-9a-f]{40}$') { throw 'Runtime lock commit is not a full SHA.' }

Refresh-FlashNextPath
$git = Find-FlashNextExecutable -Names @('git.exe','git') -AdditionalPaths @($GitPath)
$cmake = Find-FlashNextExecutable -Names @('cmake.exe','cmake') -AdditionalPaths @($CMakePath)
if (-not $git) { throw 'Git was not found after dependency installation.' }
if (-not $cmake) { throw 'CMake was not found after dependency installation.' }

$vulkanSdk = $null
if (-not [string]::IsNullOrWhiteSpace($VulkanSdkRoot)) {
    try { $vulkanSdk = [System.IO.Path]::GetFullPath($VulkanSdkRoot).TrimEnd('\') } catch { throw "The supplied Vulkan SDK root is invalid: $VulkanSdkRoot" }
}
if (-not $vulkanSdk) { $vulkanSdk = [Environment]::GetEnvironmentVariable('VULKAN_SDK','Machine') }
if (-not $vulkanSdk) { $vulkanSdk = $env:VULKAN_SDK }
if (-not $vulkanSdk) {
    $candidate = Get-ChildItem -LiteralPath 'C:\VulkanSDK' -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending | Select-Object -First 1
    if ($candidate) { $vulkanSdk = $candidate.FullName }
}
if (-not $vulkanSdk -or -not (Test-Path -LiteralPath $vulkanSdk -PathType Container)) { throw 'The verified Vulkan SDK root was not found.' }
$glslc = Join-Path $vulkanSdk 'Bin\glslc.exe'
$vulkanHeader = Join-Path $vulkanSdk 'Include\vulkan\vulkan.h'
$vulkanLibrary = Join-Path $vulkanSdk 'Lib\vulkan-1.lib'
foreach ($requiredVulkanFile in @($glslc,$vulkanHeader,$vulkanLibrary)) {
    if (-not (Test-Path -LiteralPath $requiredVulkanFile -PathType Leaf)) { throw "The Vulkan SDK is incomplete; missing '$requiredVulkanFile'." }
}
$env:VULKAN_SDK = $vulkanSdk
$env:Path = (Join-Path $vulkanSdk 'Bin') + ';' + $env:Path

function Resolve-FlashNextVisualStudioGenerator {
    param(
        [Parameter(Mandatory=$true)][string]$CMakePath,
        [Parameter(Mandatory=$true)][string]$InstallationPath,
        [AllowNull()][string]$InstallationVersion
    )

    $major = 0
    if (-not [string]::IsNullOrWhiteSpace($InstallationVersion)) {
        $versionMatch = [regex]::Match($InstallationVersion, '^\s*(\d+)')
        if ($versionMatch.Success) { [void][int]::TryParse($versionMatch.Groups[1].Value, [ref]$major) }
    }
    if ($major -eq 0) {
        $pathMatch = [regex]::Match($InstallationPath, '(?i)\\Microsoft Visual Studio\\(\d+)\\')
        if ($pathMatch.Success) { [void][int]::TryParse($pathMatch.Groups[1].Value, [ref]$major) }
    }

    $help = Invoke-FlashNextNative -FilePath $CMakePath -Arguments @('--help') -PassThru
    $helpText = $help.StandardOutput + [Environment]::NewLine + $help.StandardError
    $preferred = @()
    if ($major -ge 18) { $preferred += 'Visual Studio 18 2026' }
    if ($major -ge 17) { $preferred += 'Visual Studio 17 2022' }
    $preferred += @('Visual Studio 18 2026','Visual Studio 17 2022')

    foreach ($name in @($preferred | Select-Object -Unique)) {
        if ($helpText.IndexOf($name, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            if (($name -match '^Visual Studio 18') -and $major -gt 0 -and $major -lt 18) { continue }
            if (($name -match '^Visual Studio 17') -and $major -ge 18) { continue }
            return $name
        }
    }
    throw ("CMake does not expose a Visual Studio generator compatible with installation '{0}' (version '{1}'). Available-generator output tail: {2}" -f $InstallationPath, $InstallationVersion, (Get-FlashNextTextTail -Text $helpText -MaximumLines 80 -MaximumCharacters 12000))
}

if ([string]::IsNullOrWhiteSpace($VisualStudioPath)) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswhere -PathType Leaf) {
        $pathResult = Invoke-FlashNextNative -FilePath $vswhere -Arguments @('-latest','-products','*','-requires','Microsoft.VisualStudio.Component.VC.Tools.x86.x64','-property','installationPath') -PassThru
        $VisualStudioPath = $pathResult.StandardOutput.Trim()
        if ([string]::IsNullOrWhiteSpace($VisualStudioVersion)) {
            $versionResult = Invoke-FlashNextNative -FilePath $vswhere -Arguments @('-latest','-products','*','-requires','Microsoft.VisualStudio.Component.VC.Tools.x86.x64','-property','installationVersion') -PassThru
            $VisualStudioVersion = $versionResult.StandardOutput.Trim()
        }
    }
}
if ([string]::IsNullOrWhiteSpace($VisualStudioPath) -or -not (Test-Path -LiteralPath $VisualStudioPath -PathType Container)) {
    throw 'A verified Visual Studio installation path was not supplied and could not be resolved.'
}
$vcvars = Join-Path $VisualStudioPath 'VC\Auxiliary\Build\vcvars64.bat'
if (-not (Test-Path -LiteralPath $vcvars -PathType Leaf)) { throw "vcvars64.bat is missing from the verified Visual Studio instance: $vcvars" }
$ninja = Join-Path $VisualStudioPath 'Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe'
$cmakeGenerator = $null
$cmakeArchitecture = $null
if (Test-Path -LiteralPath $ninja -PathType Leaf) {
    $env:Path = (Split-Path -Parent $ninja) + ';' + $env:Path
    $cmakeGenerator = 'Ninja'
    Write-FlashNextLog ("Using CMake generator 'Ninja' with MSVC from '{0}' (version '{1}'). Ninja is required so the Vulkan shader-generator child CMake can find cl.exe." -f $VisualStudioPath, $VisualStudioVersion)
} else {
    $cmakeGenerator = Resolve-FlashNextVisualStudioGenerator -CMakePath $cmake -InstallationPath $VisualStudioPath -InstallationVersion $VisualStudioVersion
    $cmakeArchitecture = 'x64'
    $env:CMAKE_GENERATOR_INSTANCE = $VisualStudioPath
    Write-FlashNextLog ("Using CMake generator '{0}' with Visual Studio instance '{1}' (version '{2}')." -f $cmakeGenerator, $VisualStudioPath, $VisualStudioVersion)
}

function Invoke-FlashNextCMake {
    param(
        [Parameter(Mandatory=$true)][string[]]$CMakeArguments,
        [Parameter(Mandatory=$true)][string]$WorkingDirectory,
        [Parameter(Mandatory=$true)][string]$Stage,
        [int]$TimeoutSeconds = 1800
    )
    $quoted = @($CMakeArguments | ForEach-Object { ConvertTo-FlashNextWindowsArgument -Value $_ })
    $scriptDirectory = Join-Path $WorkRoot 'cmake-helpers'
    New-Item -ItemType Directory -Path $scriptDirectory -Force | Out-Null
    $scriptPath = Join-Path $scriptDirectory ($Stage + '.cmd')
    $lines = @(
        '@echo off',
        'setlocal',
        ('call "{0}"' -f $vcvars),
        'if errorlevel 1 exit /b 1',
        'set CL=/FS'
    )
    if (Test-Path -LiteralPath $ninja -PathType Leaf) {
        $lines += ('set "PATH={0};%PATH%"' -f (Split-Path -Parent $ninja))
    }
    $lines += ('"{0}" {1}' -f $cmake, ($quoted -join ' '))
    [System.IO.File]::WriteAllLines($scriptPath, $lines, (New-Object System.Text.UTF8Encoding($false)))
    Invoke-FlashNextNative -FilePath $scriptPath -Arguments @() -WorkingDirectory $WorkingDirectory -HeartbeatSeconds 15 -Stage $Stage -TimeoutSeconds $TimeoutSeconds | Out-Null
}

if ([string]::IsNullOrWhiteSpace($WorkRoot)) {
    $WorkRoot = Join-Path $logRoot 'build-cache'
}
$WorkRoot = [System.IO.Path]::GetFullPath($WorkRoot)
$sourceRoot = Join-Path $WorkRoot 'runtime-source'
$buildRoot = Join-Path $WorkRoot 'rb'
$staging = Join-Path $RuntimeRoot 'staging'
New-Item -ItemType Directory -Path $WorkRoot -Force | Out-Null

if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot '.git'))) {
    if (Test-Path -LiteralPath $sourceRoot) { Remove-Item -LiteralPath $sourceRoot -Recurse -Force }
    New-Item -ItemType Directory -Path $sourceRoot -Force | Out-Null
    Invoke-FlashNextNative -FilePath $git -Arguments @('init') -WorkingDirectory $sourceRoot | Out-Null
    Invoke-FlashNextNative -FilePath $git -Arguments @('remote','add','origin',$repository) -WorkingDirectory $sourceRoot | Out-Null
}
$remote = Invoke-FlashNextNative -FilePath $git -Arguments @('remote','get-url','origin') -WorkingDirectory $sourceRoot -PassThru
if ($remote.StandardOutput.Trim() -ne $repository) { throw 'Runtime source remote differs from runtime.lock.json.' }
Invoke-FlashNextNative -FilePath $git -Arguments @('fetch','--no-tags','--depth','1','origin',$commit) -WorkingDirectory $sourceRoot | Out-Null
Invoke-FlashNextNative -FilePath $git -Arguments @('checkout','--detach','--force',$commit) -WorkingDirectory $sourceRoot | Out-Null
Invoke-FlashNextNative -FilePath $git -Arguments @('clean','-ffd') -WorkingDirectory $sourceRoot | Out-Null
$head = Invoke-FlashNextNative -FilePath $git -Arguments @('rev-parse','HEAD') -WorkingDirectory $sourceRoot -PassThru
if ($head.StandardOutput.Trim() -ne $commit) { throw 'Detached source checkout does not match runtime.lock.json.' }
$tree = Invoke-FlashNextNative -FilePath $git -Arguments @('show','-s','--format=%T','HEAD') -WorkingDirectory $sourceRoot -PassThru
if ($tree.StandardOutput.Trim() -ne [string]$lock.tree) { throw 'Detached source tree does not match runtime.lock.json.' }
$status = Invoke-FlashNextNative -FilePath $git -Arguments @('status','--porcelain') -WorkingDirectory $sourceRoot -PassThru
if ($status.StandardOutput.Trim().Length -ne 0) { throw 'Runtime source checkout contains unexpected modifications.' }

$appliedPatches = @()
$applicationPrefix = $ApplicationRoot.TrimEnd('\') + '\'
foreach ($patch in @($lock.patches)) {
    $relativePatch = [string]$patch.path
    $expectedPatchHash = ([string]$patch.sha256).ToLowerInvariant()
    if ([string]::IsNullOrWhiteSpace($relativePatch) -or $expectedPatchHash -notmatch '^[0-9a-f]{64}$') {
        throw 'Runtime lock contains an invalid patch entry.'
    }

    $patchPath = [System.IO.Path]::GetFullPath((Join-Path $ApplicationRoot $relativePatch))
    if (-not $patchPath.StartsWith($applicationPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Runtime patch is outside the application root: $relativePatch"
    }
    if (-not (Test-Path -LiteralPath $patchPath -PathType Leaf)) {
        throw "Runtime patch is missing: $relativePatch"
    }

    $actualPatchHash = (Get-FlashNextFileSha256 -Path $patchPath).ToLowerInvariant()
    if ($actualPatchHash -ne $expectedPatchHash) {
        throw "Runtime patch hash mismatch: $relativePatch"
    }

    Invoke-FlashNextNative -FilePath $git -Arguments @('apply','--check',$patchPath) -WorkingDirectory $sourceRoot | Out-Null
    Invoke-FlashNextNative -FilePath $git -Arguments @('apply',$patchPath) -WorkingDirectory $sourceRoot | Out-Null
    $appliedPatches += [pscustomobject]@{ path = $relativePatch; sha256 = $actualPatchHash }
    Write-FlashNextLog "Applied verified runtime patch '$relativePatch'."
}

Invoke-FlashNextNative -FilePath $git -Arguments @('diff','--check') -WorkingDirectory $sourceRoot | Out-Null

$cachePath = Join-Path $buildRoot 'CMakeCache.txt'
if (Test-Path -LiteralPath $cachePath -PathType Leaf) {
    $cacheText = [System.IO.File]::ReadAllText($cachePath)
    $expectedGeneratorLine = "CMAKE_GENERATOR:INTERNAL=$cmakeGenerator"
    $generatorMatches = ($cacheText.IndexOf($expectedGeneratorLine, [StringComparison]::OrdinalIgnoreCase) -ge 0)
    $instanceMatches = $cmakeGenerator -eq 'Ninja'
    $instanceMatch = [regex]::Match($cacheText, '(?im)^CMAKE_GENERATOR_INSTANCE:INTERNAL=(.*)$')
    if (-not $instanceMatches -and $instanceMatch.Success) {
        try {
            $cachedInstance = [System.IO.Path]::GetFullPath($instanceMatch.Groups[1].Value.Trim()).TrimEnd('\')
            $expectedInstance = [System.IO.Path]::GetFullPath($VisualStudioPath).TrimEnd('\')
            $instanceMatches = $cachedInstance.Equals($expectedInstance, [StringComparison]::OrdinalIgnoreCase)
        }
        catch {
            $cachedInstance = $instanceMatch.Groups[1].Value.Trim().Replace('/','\').TrimEnd('\')
            $expectedInstance = $VisualStudioPath.Replace('/','\').TrimEnd('\')
            $instanceMatches = $cachedInstance.Equals($expectedInstance, [StringComparison]::OrdinalIgnoreCase)
        }
    }
    if (-not $generatorMatches -or -not $instanceMatches) {
        Write-FlashNextLog 'Removing an incompatible cached CMake generator or Visual Studio instance before reconfiguration.' 'WARN'
        Remove-Item -LiteralPath $buildRoot -Recurse -Force
    }
}
New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
$configure = @(
    '-S',$sourceRoot,
    '-B',$buildRoot,
    '-G',$cmakeGenerator
)
if ($cmakeArchitecture) {
    $configure += @('-A',$cmakeArchitecture, ('-DCMAKE_GENERATOR_INSTANCE=' + $VisualStudioPath))
}
$configure += @(
    '-DGGML_VULKAN=ON',
    '-DGGML_NATIVE=ON',
    '-DLLAMA_CURL=OFF',
    '-DLLAMA_BUILD_UI=OFF',
    '-DLLAMA_BUILD_TESTS=OFF',
    '-DLLAMA_BUILD_EXAMPLES=OFF',
    '-DLLAMA_BUILD_HTML=OFF',
    '-DBUILD_SHARED_LIBS=ON',
    '-DCMAKE_BUILD_TYPE=Release'
)
Invoke-FlashNextCMake -CMakeArguments $configure -WorkingDirectory $sourceRoot -Stage 'cmake-configure' -TimeoutSeconds 1800
Invoke-FlashNextCMake -CMakeArguments @('--build',$buildRoot,'--config','Release','--target','llama-server','llama-cli','llama-bench') -WorkingDirectory $sourceRoot -Stage 'cmake-build' -TimeoutSeconds 14400

if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging -Force | Out-Null
$serverSource = Get-ChildItem -LiteralPath $buildRoot -Filter 'llama-server.exe' -File -Recurse | Where-Object { $_.FullName -match '\\Release\\|\\bin\\' } | Select-Object -First 1
if (-not $serverSource) { throw 'The build did not produce llama-server.exe.' }
Copy-Item -Path (Join-Path $serverSource.Directory.FullName '*') -Destination $staging -Recurse -Force
foreach ($binary in @('llama-server.exe','llama-cli.exe','llama-bench.exe')) {
    $target = Join-Path $staging $binary
    if (-not (Test-Path -LiteralPath $target)) {
        $source = Get-ChildItem -LiteralPath $buildRoot -Filter $binary -File -Recurse | Select-Object -First 1
        if (-not $source) { throw "The build did not produce $binary." }
        Copy-Item -LiteralPath $source.FullName -Destination $target -Force
    }
}

$version = Invoke-FlashNextNative -FilePath (Join-Path $staging 'llama-cli.exe') -Arguments @('--version') -WorkingDirectory $staging -PassThru
$devices = Invoke-FlashNextNative -FilePath (Join-Path $staging 'llama-cli.exe') -Arguments @('--list-devices') -WorkingDirectory $staging -PassThru
if (($devices.StandardOutput + $devices.StandardError) -notmatch '(?i)Vulkan|8060S') { throw 'The built runtime did not enumerate a Vulkan device.' }
$help = Invoke-FlashNextNative -FilePath (Join-Path $staging 'llama-server.exe') -Arguments @('--help') -WorkingDirectory $staging -PassThru
$helpText = $help.StandardOutput + $help.StandardError
foreach ($option in @($lock.requiredHelpOptions)) {
    if ($helpText.IndexOf($option, [StringComparison]::Ordinal) -lt 0) { throw "Built server is missing required option '$option'." }
}

$inventory = @()
foreach ($file in Get-ChildItem -LiteralPath $staging -File) {
    $inventory += [pscustomobject]@{ name = $file.Name; bytes = $file.Length; sha256 = (Get-FlashNextFileSha256 -Path $file.FullName) }
}
$buildRecord = [pscustomobject]@{
    manifestVersion = '1.0.0'
    repository = $repository
    commit = $commit
    tree = $tree.StandardOutput.Trim()
    patches = $appliedPatches
    builtAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    backend = 'Vulkan'
    cmake = $cmake
    vulkanSdk = $vulkanSdk
    cmakeGenerator = $cmakeGenerator
    visualStudioPath = $VisualStudioPath
    visualStudioVersion = $VisualStudioVersion
    versionOutput = $version.StandardOutput.Trim()
    deviceOutput = ($devices.StandardOutput + $devices.StandardError).Trim()
    launchEnvironment = $lock.launchEnvironment
    files = $inventory
}
Write-FlashNextJsonAtomic -Path (Join-Path $staging 'runtime.build.json') -Value $buildRecord

if ($ActivateInitial) {
    $current = Join-Path $RuntimeRoot 'current'
    if (Test-Path -LiteralPath $current) {
        Write-FlashNextLog 'runtime/current already exists; the new build remains in runtime/staging for manager smoke testing.' 'WARN'
    } else {
        [System.IO.Directory]::Move($staging, $current)
        Write-FlashNextLog "Activated initial runtime at '$current'."
    }
} elseif (-not $StageOnly) {
    Write-FlashNextLog 'Runtime was built into staging. Activation requires manager compatibility smoke tests.'
}
Write-FlashNextLog 'Pinned Vulkan runtime build completed.'
