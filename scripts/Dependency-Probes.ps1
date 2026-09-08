Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

function ConvertTo-FlashNextComparableVersion {
    param([AllowNull()][string]$Text)
    if ([string]::IsNullOrWhiteSpace($Text)) { return $null }
    $normalized = [regex]::Replace($Text, '(?i)\.windows\.', '.')
    $match = [regex]::Match($normalized, '(?<!\d)(\d+)\.(\d+)(?:\.(\d+))?(?:\.(\d+))?')
    if (-not $match.Success) { return $null }
    $major = [int]$match.Groups[1].Value
    $minor = [int]$match.Groups[2].Value
    $build = if ($match.Groups[3].Success) { [int]$match.Groups[3].Value } else { 0 }
    $revision = if ($match.Groups[4].Success) { [int]$match.Groups[4].Value } else { 0 }
    return (New-Object System.Version -ArgumentList $major,$minor,$build,$revision)
}

function Test-FlashNextVersionAtLeast {
    param([AllowNull()][string]$Actual, [Parameter(Mandatory=$true)][string]$Minimum)
    $actualVersion = ConvertTo-FlashNextComparableVersion -Text $Actual
    $minimumVersion = ConvertTo-FlashNextComparableVersion -Text $Minimum
    if ($null -eq $actualVersion -or $null -eq $minimumVersion) { return $false }
    return ($actualVersion.CompareTo($minimumVersion) -ge 0)
}

function Invoke-FlashNextProbeCommand {
    param(
        [Parameter(Mandatory=$true)][string]$FilePath,
        [Parameter(Mandatory=$true)][string[]]$Arguments,
        [string]$WorkingDirectory
    )
    if ([string]::IsNullOrWhiteSpace($WorkingDirectory)) {
        $WorkingDirectory = Split-Path -Parent $FilePath
        if ([string]::IsNullOrWhiteSpace($WorkingDirectory)) { $WorkingDirectory = $pwd.Path }
    }
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $FilePath
    $start.WorkingDirectory = $WorkingDirectory
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.CreateNoWindow = $true
    $start.Arguments = (($Arguments | ForEach-Object { ConvertTo-FlashNextWindowsArgument -Value $_ }) -join ' ')
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw "Could not start '$FilePath'." }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(60000)) {
            try { $process.Kill() } catch { }
            try { [void]$process.WaitForExit(5000) } catch { }
            return [pscustomobject]@{
                ExitCode = -1
                StandardOutput = ''
                StandardError = "Capability probe '$FilePath' exceeded the 60-second limit and was terminated."
            }
        }
        return [pscustomobject]@{
            ExitCode = [int]$process.ExitCode
            StandardOutput = $stdoutTask.GetAwaiter().GetResult()
            StandardError = $stderrTask.GetAwaiter().GetResult()
        }
    }
    catch {
        return [pscustomobject]@{
            ExitCode = -1
            StandardOutput = ''
            StandardError = $_.Exception.Message
        }
    }
    finally {
        $process.Dispose()
    }
}


function Get-FlashNextExecutableCandidates {
    param([Parameter(Mandatory=$true)][string[]]$Names, [string[]]$AdditionalPaths = @())
    $values = New-Object System.Collections.Generic.List[string]
    foreach ($candidate in @($AdditionalPaths)) {
        if ([string]::IsNullOrWhiteSpace([string]$candidate)) { continue }
        try {
            $full = [System.IO.Path]::GetFullPath([string]$candidate)
            if (Test-Path -LiteralPath $full -PathType Leaf) { [void]$values.Add($full) }
        }
        catch { }
    }
    foreach ($name in $Names) {
        $command = Get-Command $name -ErrorAction SilentlyContinue
        if ($command -and $command.Source) { [void]$values.Add([string]$command.Source) }
    }
    $seen = @{}
    foreach ($value in $values) {
        try { $full = [System.IO.Path]::GetFullPath($value) } catch { continue }
        $key = $full.ToUpperInvariant()
        if ($seen.ContainsKey($key)) { continue }
        $seen[$key] = $true
        Write-Output $full
    }
}

function New-FlashNextDependencyProbeResult {
    param(
        [Parameter(Mandatory=$true)][string]$PackageId,
        [Parameter(Mandatory=$true)][bool]$Present,
        [Parameter(Mandatory=$true)][bool]$Compatible,
        [AllowNull()][string]$InstalledVersion,
        [AllowNull()][string]$ExecutablePath,
        [AllowNull()][string]$RootPath,
        [AllowNull()][string]$Evidence,
        [string[]]$CommandPrefix = @()
    )
    return [pscustomobject]@{
        packageId = $PackageId
        present = $Present
        compatible = $Compatible
        installedVersion = $InstalledVersion
        executablePath = $ExecutablePath
        rootPath = $RootPath
        commandPrefix = @($CommandPrefix)
        evidence = $Evidence
        checkedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    }
}

function Get-FlashNextPackageMinimumVersion {
    param([Parameter(Mandatory=$true)]$Package)
    if ($Package.PSObject.Properties.Name -contains 'minimumVersion' -and -not [string]::IsNullOrWhiteSpace([string]$Package.minimumVersion)) {
        return [string]$Package.minimumVersion
    }
    return [string]$Package.version
}

function Get-FlashNextGitProbe {
    param([Parameter(Mandatory=$true)]$Package)
    $candidates = @()
    if ($env:ProgramFiles) {
        $candidates += (Join-Path $env:ProgramFiles 'Git\cmd\git.exe')
        $candidates += (Join-Path $env:ProgramFiles 'Git\bin\git.exe')
    }
    if (${env:ProgramFiles(x86)}) { $candidates += (Join-Path ${env:ProgramFiles(x86)} 'Git\cmd\git.exe') }
    if ($env:LOCALAPPDATA) { $candidates += (Join-Path $env:LOCALAPPDATA 'Programs\Git\cmd\git.exe') }
    $firstPresent = $null
    foreach ($path in @(Get-FlashNextExecutableCandidates -Names @('git.exe','git') -AdditionalPaths $candidates)) {
        $result = Invoke-FlashNextProbeCommand -FilePath $path -Arguments @('--version')
        $text = (($result.StandardOutput + [Environment]::NewLine + $result.StandardError).Trim())
        $version = ConvertTo-FlashNextComparableVersion -Text $text
        $versionText = if ($version) { $version.ToString() } else { $null }
        $compatible = ($result.ExitCode -eq 0 -and $version -and (Test-FlashNextVersionAtLeast -Actual $versionText -Minimum (Get-FlashNextPackageMinimumVersion -Package $Package)))
        $probe = New-FlashNextDependencyProbeResult -PackageId ([string]$Package.id) -Present $true -Compatible ([bool]$compatible) -InstalledVersion $versionText -ExecutablePath $path -RootPath (Split-Path -Parent (Split-Path -Parent $path)) -Evidence $text
        if ($compatible) { return $probe }
        if ($null -eq $firstPresent) { $firstPresent = $probe }
    }
    if ($firstPresent) { return $firstPresent }
    return (New-FlashNextDependencyProbeResult -PackageId ([string]$Package.id) -Present $false -Compatible $false -Evidence 'git.exe was not found in the standard machine, user, or PATH locations.')
}

function Get-FlashNextCMakeProbe {
    param([Parameter(Mandatory=$true)]$Package)
    $candidates = @()
    if ($env:ProgramFiles) { $candidates += (Join-Path $env:ProgramFiles 'CMake\bin\cmake.exe') }
    if (${env:ProgramFiles(x86)}) { $candidates += (Join-Path ${env:ProgramFiles(x86)} 'CMake\bin\cmake.exe') }
    if ($env:LOCALAPPDATA) { $candidates += (Join-Path $env:LOCALAPPDATA 'Programs\CMake\bin\cmake.exe') }
    $firstPresent = $null
    foreach ($path in @(Get-FlashNextExecutableCandidates -Names @('cmake.exe','cmake') -AdditionalPaths $candidates)) {
        $result = Invoke-FlashNextProbeCommand -FilePath $path -Arguments @('--version')
        $text = (($result.StandardOutput + [Environment]::NewLine + $result.StandardError).Trim())
        $version = ConvertTo-FlashNextComparableVersion -Text $text
        $versionText = if ($version) { $version.ToString() } else { $null }
        $compatible = ($result.ExitCode -eq 0 -and $version -and (Test-FlashNextVersionAtLeast -Actual $versionText -Minimum (Get-FlashNextPackageMinimumVersion -Package $Package)))
        $probe = New-FlashNextDependencyProbeResult -PackageId ([string]$Package.id) -Present $true -Compatible ([bool]$compatible) -InstalledVersion $versionText -ExecutablePath $path -RootPath (Split-Path -Parent (Split-Path -Parent $path)) -Evidence $text
        if ($compatible) { return $probe }
        if ($null -eq $firstPresent) { $firstPresent = $probe }
    }
    if ($firstPresent) { return $firstPresent }
    return (New-FlashNextDependencyProbeResult -PackageId ([string]$Package.id) -Present $false -Compatible $false -Evidence 'cmake.exe was not found in the standard machine, user, or PATH locations.')
}

function Get-FlashNextPythonProbe {
    param([Parameter(Mandatory=$true)]$Package)
    $command = Resolve-FlashNextPython313Command
    if (-not $command) {
        return (New-FlashNextDependencyProbeResult -PackageId ([string]$Package.id) -Present $false -Compatible $false -Evidence 'A startable 64-bit Python 3.13 interpreter was not found.')
    }
    $pythonCode = 'import struct,sys; print("%d.%d.%d|%d|%s" % (sys.version_info.major,sys.version_info.minor,sys.version_info.micro,struct.calcsize("P")*8,sys.executable))'
    $arguments = @($command.Prefix) + @('-c',$pythonCode)
    $result = Invoke-FlashNextProbeCommand -FilePath ([string]$command.Path) -Arguments $arguments
    $text = (($result.StandardOutput + [Environment]::NewLine + $result.StandardError).Trim())
    $line = @($result.StandardOutput -split "`r?`n" | Where-Object { $_ -match '^\d+\.\d+\.\d+\|\d+\|' } | Select-Object -First 1)
    $versionText = $null
    $bits = 0
    $interpreter = [string]$command.Path
    if ($line.Count -gt 0) {
        $parts = ([string]$line[0]) -split '\|',3
        if ($parts.Count -ge 1) { $versionText = $parts[0] }
        if ($parts.Count -ge 2) { [void]([int]::TryParse($parts[1],[ref]$bits)) }
        if ($parts.Count -ge 3 -and $parts[2]) { $interpreter = $parts[2] }
    }
    $version = ConvertTo-FlashNextComparableVersion -Text $versionText
    $compatible = ($result.ExitCode -eq 0 -and $version -and $version.Major -eq 3 -and $version.Minor -eq 13 -and $bits -eq 64 -and (Test-FlashNextVersionAtLeast -Actual $versionText -Minimum (Get-FlashNextPackageMinimumVersion -Package $Package)))
    $verifiedExecutable = [string]$command.Path
    $verifiedPrefix = @($command.Prefix)
    if ($interpreter -and (Test-Path -LiteralPath $interpreter -PathType Leaf)) {
        $verifiedExecutable = [System.IO.Path]::GetFullPath($interpreter)
        $verifiedPrefix = @()
    }
    return (New-FlashNextDependencyProbeResult -PackageId ([string]$Package.id) -Present $true -Compatible ([bool]$compatible) -InstalledVersion $versionText -ExecutablePath $verifiedExecutable -RootPath (Split-Path -Parent $interpreter) -CommandPrefix $verifiedPrefix -Evidence $text)
}

function Get-FlashNextDotNetProbe {
    param([Parameter(Mandatory=$true)]$Package)
    $candidates = @()
    if ($env:ProgramFiles) { $candidates += (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe') }
    if (${env:ProgramFiles(x86)}) { $candidates += (Join-Path ${env:ProgramFiles(x86)} 'dotnet\dotnet.exe') }
    if ($env:LOCALAPPDATA) { $candidates += (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe') }
    $firstPresent = $null
    foreach ($path in @(Get-FlashNextExecutableCandidates -Names @('dotnet.exe','dotnet') -AdditionalPaths $candidates)) {
        $result = Invoke-FlashNextProbeCommand -FilePath $path -Arguments @('--list-sdks')
        $text = (($result.StandardOutput + [Environment]::NewLine + $result.StandardError).Trim())
        $selected = $null
        foreach ($line in @($result.StandardOutput -split "`r?`n")) {
            if ($line -notmatch '^\s*(\d+\.\d+\.\d+(?:\.\d+)?)\s+\[') { continue }
            $candidate = ConvertTo-FlashNextComparableVersion -Text $matches[1]
            if ($candidate -and $candidate.Major -eq 10 -and ($null -eq $selected -or $candidate.CompareTo($selected) -gt 0)) { $selected = $candidate }
        }
        $versionText = if ($selected) { $selected.ToString() } else { $null }
        $compatible = ($result.ExitCode -eq 0 -and $selected -and (Test-FlashNextVersionAtLeast -Actual $versionText -Minimum (Get-FlashNextPackageMinimumVersion -Package $Package)))
        $probe = New-FlashNextDependencyProbeResult -PackageId ([string]$Package.id) -Present $true -Compatible ([bool]$compatible) -InstalledVersion $versionText -ExecutablePath $path -RootPath (Split-Path -Parent $path) -Evidence $text
        if ($compatible) { return $probe }
        if ($null -eq $firstPresent) { $firstPresent = $probe }
    }
    if ($firstPresent) { return $firstPresent }
    return (New-FlashNextDependencyProbeResult -PackageId ([string]$Package.id) -Present $false -Compatible $false -Evidence 'dotnet.exe was not found in Program Files or PATH.')
}

function Get-FlashNextVisualStudioProbe {
    param([Parameter(Mandatory=$true)]$Package)
    $candidates = @()
    if (${env:ProgramFiles(x86)}) { $candidates += (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe') }
    if ($env:ProgramFiles) { $candidates += (Join-Path $env:ProgramFiles 'Microsoft Visual Studio\Installer\vswhere.exe') }
    $vswhere = Find-FlashNextExecutable -Names @('vswhere.exe') -AdditionalPaths $candidates
    if (-not $vswhere) {
        return (New-FlashNextDependencyProbeResult -PackageId ([string]$Package.id) -Present $false -Compatible $false -Evidence 'vswhere.exe was not found, so Visual Studio Build Tools could not be verified.')
    }

    $component = 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64'
    $pathResult = Invoke-FlashNextProbeCommand -FilePath $vswhere -Arguments @('-latest','-products','*','-requires',$component,'-property','installationPath')
    $versionResult = Invoke-FlashNextProbeCommand -FilePath $vswhere -Arguments @('-latest','-products','*','-requires',$component,'-property','installationVersion')
    $installationPath = $pathResult.StandardOutput.Trim()
    $installationVersion = $versionResult.StandardOutput.Trim()
    $qualifiedByComponent = -not [string]::IsNullOrWhiteSpace($installationPath)

    if (-not $qualifiedByComponent) {
        $pathResult = Invoke-FlashNextProbeCommand -FilePath $vswhere -Arguments @('-latest','-products','Microsoft.VisualStudio.Product.BuildTools','-property','installationPath')
        $versionResult = Invoke-FlashNextProbeCommand -FilePath $vswhere -Arguments @('-latest','-products','Microsoft.VisualStudio.Product.BuildTools','-property','installationVersion')
        $installationPath = $pathResult.StandardOutput.Trim()
        $installationVersion = $versionResult.StandardOutput.Trim()
    }

    if ([string]::IsNullOrWhiteSpace($installationPath) -or -not (Test-Path -LiteralPath $installationPath -PathType Container)) {
        $evidence = (($pathResult.StandardOutput + [Environment]::NewLine + $pathResult.StandardError).Trim())
        return (New-FlashNextDependencyProbeResult -PackageId ([string]$Package.id) -Present $false -Compatible $false -ExecutablePath $vswhere -Evidence ("Visual Studio Build Tools installation was not found. " + $evidence))
    }

    $vcvars = Join-Path $installationPath 'VC\Auxiliary\Build\vcvars64.bat'
    $msvcRoot = Join-Path $installationPath 'VC\Tools\MSVC'
    $clPath = $null
    if (Test-Path -LiteralPath $msvcRoot -PathType Container) {
        foreach ($directory in @(Get-ChildItem -LiteralPath $msvcRoot -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending)) {
            $candidate = Join-Path $directory.FullName 'bin\Hostx64\x64\cl.exe'
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { $clPath = $candidate; break }
        }
    }

    $sdkRoot = if (${env:ProgramFiles(x86)}) { Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10' } else { $null }
    $sdkVersion = $null
    $sdkComplete = $false
    if ($sdkRoot -and (Test-Path -LiteralPath $sdkRoot -PathType Container)) {
        $includeRoot = Join-Path $sdkRoot 'Include'
        foreach ($directory in @(Get-ChildItem -LiteralPath $includeRoot -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending)) {
            $windowsHeader = Join-Path $directory.FullName 'um\Windows.h'
            $kernelLibrary = Join-Path (Join-Path $sdkRoot ('Lib\' + $directory.Name)) 'um\x64\kernel32.lib'
            $resourceCompiler = Join-Path (Join-Path $sdkRoot ('bin\' + $directory.Name)) 'x64\rc.exe'
            if ((Test-Path -LiteralPath $windowsHeader -PathType Leaf) -and (Test-Path -LiteralPath $kernelLibrary -PathType Leaf) -and (Test-Path -LiteralPath $resourceCompiler -PathType Leaf)) {
                $sdkVersion = $directory.Name
                $sdkComplete = $true
                break
            }
        }
    }

    $version = ConvertTo-FlashNextComparableVersion -Text $installationVersion
    $versionText = if ($version) { $version.ToString() } else { $installationVersion }
    $compatible = ($qualifiedByComponent -and $version -and (Test-FlashNextVersionAtLeast -Actual $versionText -Minimum (Get-FlashNextPackageMinimumVersion -Package $Package)) -and (Test-Path -LiteralPath $vcvars -PathType Leaf) -and $clPath -and $sdkComplete)
    $evidenceParts = @(
        "installationPath=$installationPath",
        "installationVersion=$installationVersion",
        "vctoolsComponent=$qualifiedByComponent",
        "vcvars64=$([bool](Test-Path -LiteralPath $vcvars -PathType Leaf))",
        "cl.exe=$clPath",
        "windowsSdk=$sdkVersion"
    )
    return (New-FlashNextDependencyProbeResult -PackageId ([string]$Package.id) -Present $true -Compatible ([bool]$compatible) -InstalledVersion $versionText -ExecutablePath $vswhere -RootPath $installationPath -Evidence ($evidenceParts -join '; '))
}

function Get-FlashNextVulkanInfoCandidates {
    param([AllowNull()][string]$VulkanSdkRoot)

    $additionalPaths = @()
    $roots = @()
    if (-not [string]::IsNullOrWhiteSpace($VulkanSdkRoot)) { $roots += $VulkanSdkRoot }
    foreach ($scope in @('Process','Machine','User')) {
        $environmentRoot = [Environment]::GetEnvironmentVariable('VULKAN_SDK',$scope)
        if ($environmentRoot) { $roots += $environmentRoot }
    }
    if (Test-Path -LiteralPath 'C:\VulkanSDK' -PathType Container) {
        $roots += @(Get-ChildItem -LiteralPath 'C:\VulkanSDK' -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending | Select-Object -ExpandProperty FullName)
    }

    $seenRoots = @{}
    foreach ($candidateRoot in $roots) {
        if ([string]::IsNullOrWhiteSpace([string]$candidateRoot)) { continue }
        try { $root = [System.IO.Path]::GetFullPath([string]$candidateRoot).TrimEnd('\') } catch { continue }
        $key = $root.ToUpperInvariant()
        if ($seenRoots.ContainsKey($key)) { continue }
        $seenRoots[$key] = $true
        $additionalPaths += (Join-Path $root 'Bin\vulkaninfoSDK.exe')
        $additionalPaths += (Join-Path $root 'Bin\vulkaninfo.exe')
    }

    return @(Get-FlashNextExecutableCandidates -Names @('vulkaninfoSDK.exe','vulkaninfo.exe','vulkaninfoSDK','vulkaninfo') -AdditionalPaths $additionalPaths)
}

function Resolve-FlashNextVulkanInfoPath {
    param([AllowNull()][string]$VulkanSdkRoot)
    $matches = @(Get-FlashNextVulkanInfoCandidates -VulkanSdkRoot $VulkanSdkRoot | Select-Object -First 1)
    if ($matches.Count -eq 0) { return $null }
    return [string]$matches[0]
}

function Get-FlashNextVulkanSdkProbe {
    param([Parameter(Mandatory=$true)]$Package)
    $minimum = Get-FlashNextPackageMinimumVersion -Package $Package
    $candidateRoots = @()
    if (Test-Path -LiteralPath 'C:\VulkanSDK' -PathType Container) {
        $exact = Join-Path 'C:\VulkanSDK' ([string]$Package.version)
        if (Test-Path -LiteralPath $exact -PathType Container) { $candidateRoots += $exact }
    }
    foreach ($scope in @('Process','Machine','User')) {
        $environmentRoot = [Environment]::GetEnvironmentVariable('VULKAN_SDK',$scope)
        if ($environmentRoot) { $candidateRoots += $environmentRoot }
    }
    if (Test-Path -LiteralPath 'C:\VulkanSDK' -PathType Container) {
        $candidateRoots += @(Get-ChildItem -LiteralPath 'C:\VulkanSDK' -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending | Select-Object -ExpandProperty FullName)
    }

    $seen = @{}
    $firstPresent = $null
    foreach ($candidateRoot in $candidateRoots) {
        if ([string]::IsNullOrWhiteSpace([string]$candidateRoot)) { continue }
        try { $root = [System.IO.Path]::GetFullPath([string]$candidateRoot).TrimEnd('\') } catch { continue }
        $key = $root.ToUpperInvariant()
        if ($seen.ContainsKey($key)) { continue }
        $seen[$key] = $true
        if (-not (Test-Path -LiteralPath $root -PathType Container)) { continue }
        $glslc = Join-Path $root 'Bin\glslc.exe'
        $header = Join-Path $root 'Include\vulkan\vulkan.h'
        $library = Join-Path $root 'Lib\vulkan-1.lib'
        $vulkanInfo = Resolve-FlashNextVulkanInfoPath -VulkanSdkRoot $root
        $versionName = Split-Path -Leaf $root
        $version = ConvertTo-FlashNextComparableVersion -Text $versionName
        $versionText = if ($version) { $version.ToString() } else { $versionName }

        # vulkaninfo is an observability tool, not a build dependency.  The Windows SDK
        # officially names its bundled binary vulkaninfoSDK.exe.  Compile capability is
        # established by glslc, the Vulkan headers, and the import library.
        $complete = ((Test-Path -LiteralPath $glslc -PathType Leaf) -and (Test-Path -LiteralPath $header -PathType Leaf) -and (Test-Path -LiteralPath $library -PathType Leaf))
        $compatible = ($complete -and $version -and (Test-FlashNextVersionAtLeast -Actual $versionText -Minimum $minimum))
        $evidence = "root=$root; glslc=$([bool](Test-Path -LiteralPath $glslc -PathType Leaf)); header=$([bool](Test-Path -LiteralPath $header -PathType Leaf)); library=$([bool](Test-Path -LiteralPath $library -PathType Leaf)); vulkanInfo=$vulkanInfo"
        $probe = New-FlashNextDependencyProbeResult -PackageId ([string]$Package.id) -Present $true -Compatible ([bool]$compatible) -InstalledVersion $versionText -ExecutablePath $glslc -RootPath $root -Evidence $evidence
        if ($compatible) { return $probe }
        if ($null -eq $firstPresent) { $firstPresent = $probe }
    }
    if ($firstPresent) { return $firstPresent }
    return (New-FlashNextDependencyProbeResult -PackageId ([string]$Package.id) -Present $false -Compatible $false -Evidence 'No Vulkan SDK installation was found under C:\VulkanSDK or VULKAN_SDK.')
}

function Get-FlashNextDependencyProbe {
    param([Parameter(Mandatory=$true)]$Package)
    switch ([string]$Package.id) {
        'Git.Git' { return (Get-FlashNextGitProbe -Package $Package) }
        'Kitware.CMake' { return (Get-FlashNextCMakeProbe -Package $Package) }
        'Python.Python.3.13' { return (Get-FlashNextPythonProbe -Package $Package) }
        'Microsoft.DotNet.SDK.10' { return (Get-FlashNextDotNetProbe -Package $Package) }
        'Microsoft.VisualStudio.2022.BuildTools' { return (Get-FlashNextVisualStudioProbe -Package $Package) }
        'KhronosGroup.VulkanSDK' { return (Get-FlashNextVulkanSdkProbe -Package $Package) }
        default { throw "No direct dependency probe is implemented for '$($Package.id)'." }
    }
}

function Wait-FlashNextDependencyProbe {
    param(
        [Parameter(Mandatory=$true)]$Package,
        [int]$TimeoutSeconds = 30,
        [int]$IntervalSeconds = 2
    )
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $last = $null
    do {
        Refresh-FlashNextPath
        $last = Get-FlashNextDependencyProbe -Package $Package
        if ($last.compatible) { return $last }
        if ([DateTimeOffset]::UtcNow -ge $deadline) { break }
        Start-Sleep -Seconds $IntervalSeconds
    } while ($true)
    return $last
}

function Test-FlashNextDependencyProbeHelpers {
    $gitVersion = ConvertTo-FlashNextComparableVersion -Text 'git version 2.55.0.windows.3'
    if ($null -eq $gitVersion -or $gitVersion.ToString() -ne '2.55.0.3') { throw 'Dependency probe self-test failed to normalize the Git for Windows version.' }
    if (-not (Test-FlashNextVersionAtLeast -Actual '10.0.401' -Minimum '10.0.400')) { throw 'Dependency probe self-test failed a valid minimum-version comparison.' }
    if (Test-FlashNextVersionAtLeast -Actual '3.12.99' -Minimum '3.13.15') { throw 'Dependency probe self-test accepted an invalid older version.' }
    return $true
}
