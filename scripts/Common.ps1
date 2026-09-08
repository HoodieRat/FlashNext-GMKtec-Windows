Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$script:FlashNextLogPath = $null
$script:FlashNextMirrorLogPath = $null

function Get-FlashNextRepositoryRoot {
    param([Parameter(Mandatory=$true)][string]$ScriptDirectory)
    return [System.IO.Path]::GetFullPath((Join-Path $ScriptDirectory '..'))
}

function Get-FlashNextUserRoot {
    return (Join-Path $env:LOCALAPPDATA 'FlashNextManager')
}

function Get-FlashNextMachineRoot {
    return (Join-Path $env:ProgramData 'FlashNextManager')
}

function Initialize-FlashNextLogging {
    param(
        [string]$Root = (Get-FlashNextUserRoot),
        [string]$Name = 'install',
        [string]$MirrorPath
    )
    $logDirectory = Join-Path $Root 'logs'
    New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
    $script:FlashNextLogPath = Join-Path $logDirectory ($Name + '-' + (Get-Date -Format 'yyyy-MM-dd') + '.log')
    $script:FlashNextMirrorLogPath = $null
    if (-not [string]::IsNullOrWhiteSpace($MirrorPath)) {
        $script:FlashNextMirrorLogPath = [System.IO.Path]::GetFullPath($MirrorPath)
        $mirrorDirectory = Split-Path -Parent $script:FlashNextMirrorLogPath
        if ($mirrorDirectory) { New-Item -ItemType Directory -Path $mirrorDirectory -Force | Out-Null }
    }
}

function Add-FlashNextLogLine {
    param([AllowNull()][string]$Path, [Parameter(Mandatory=$true)][string]$Line)
    if ([string]::IsNullOrWhiteSpace($Path)) { return }
    try { Add-Content -LiteralPath $Path -Value $Line -Encoding UTF8 }
    catch { Write-Warning ("Could not append to log '{0}': {1}" -f $Path, $_.Exception.Message) }
}

function Write-FlashNextLog {
    param([Parameter(Mandatory=$true)][string]$Message, [ValidateSet('INFO','WARN','ERROR')][string]$Level = 'INFO')
    $line = ('{0:o} [{1}] {2}' -f [DateTimeOffset]::UtcNow, $Level, $Message)
    Write-Host $line
    Add-FlashNextLogLine -Path $script:FlashNextLogPath -Line $line
    if ($script:FlashNextMirrorLogPath -and -not $script:FlashNextMirrorLogPath.Equals($script:FlashNextLogPath, [StringComparison]::OrdinalIgnoreCase)) {
        Add-FlashNextLogLine -Path $script:FlashNextMirrorLogPath -Line $line
    }
}

function Test-FlashNextAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Write-FlashNextJsonAtomic {
    param([Parameter(Mandatory=$true)][string]$Path, [Parameter(Mandatory=$true)]$Value)
    $directory = Split-Path -Parent $Path
    if ($directory) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
    $temporary = $Path + '.new-' + [Guid]::NewGuid().ToString('N')
    $json = $Value | ConvertTo-Json -Depth 20
    [System.IO.File]::WriteAllText($temporary, $json + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding($false)))
    if (Test-Path -LiteralPath $Path) {
        $backup = $Path + '.bak'
        [System.IO.File]::Replace($temporary, $Path, $backup, $true)
    } else {
        [System.IO.File]::Move($temporary, $Path)
    }
}

function Read-FlashNextJson {
    param([Parameter(Mandatory=$true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    return (Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json)
}

function ConvertTo-FlashNextWindowsArgument {
    param([AllowEmptyString()][string]$Value)
    if ($null -eq $Value -or $Value.Length -eq 0) { return '""' }
    if ($Value -notmatch '[\s"]') { return $Value }
    $builder = New-Object System.Text.StringBuilder
    [void]$builder.Append('"')
    $slashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') { $slashes++; continue }
        if ($character -eq '"') {
            [void]$builder.Append(('\' * (($slashes * 2) + 1)))
            [void]$builder.Append('"')
            $slashes = 0
            continue
        }
        if ($slashes -gt 0) { [void]$builder.Append(('\' * $slashes)); $slashes = 0 }
        [void]$builder.Append($character)
    }
    if ($slashes -gt 0) { [void]$builder.Append(('\' * ($slashes * 2))) }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function Get-FlashNextTextTail {
    param([AllowNull()][string]$Text, [int]$MaximumLines = 40, [int]$MaximumCharacters = 8000)
    if ([string]::IsNullOrWhiteSpace($Text)) { return '' }
    $lines = @($Text.Trim() -split "`r?`n")
    $tail = (@($lines | Select-Object -Last $MaximumLines) -join [Environment]::NewLine).Trim()
    if ($tail.Length -gt $MaximumCharacters) { $tail = $tail.Substring($tail.Length - $MaximumCharacters) }
    return $tail
}

function Get-FlashNextRedactedText {
    param([AllowNull()][string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return $Value }
    $result = [regex]::Replace($Value, '(?i)(Bearer\s+)[A-Za-z0-9._~+\-/=]{8,}', '${1}[REDACTED]')
    $result = [regex]::Replace($result, '(?i)(--api-key(?:-file)?(?:\s+|=))\S+', '${1}[REDACTED]')
    $result = [regex]::Replace($result, '(?i)("(apiKey|api_key|key)"\s*:\s*")[^"]+(")', '${1}[REDACTED]$3')
    return $result
}

function Get-FlashNextDefaultTimeoutSeconds {
    param([Parameter(Mandatory=$true)][string]$FilePath, [AllowEmptyCollection()][string[]]$Arguments = @())
    $name = [System.IO.Path]::GetFileName($FilePath).ToLowerInvariant()
    $joined = (($Arguments | ForEach-Object { [string]$_ }) -join ' ')
    if ($name -eq 'flashnext.vulkanprobe.exe') { return 15 }
    if ($name -match 'vulkaninfo') { return 20 }
    if ($name -eq 'flashnext.testhanghelper.exe') { return 15 }
    if ($joined -match '(?i)--hardware-report') { return 30 }
    if ($joined -match '(?i)--initialize|--set-model-directory|--verify-model|--verify-runtime') { return 120 }
    if ($joined -match '(?i)--smoke-test|--download-model') { return 3600 }
    if ($name -eq 'git.exe' -or $name -eq 'git') {
        if ($joined -match '(?i)\bfetch\b|\bclone\b') { return 900 }
        return 60
    }
    if ($name -eq 'cmake.exe' -or $name -eq 'cmake') {
        if ($joined -match '(?i)--build') { return 14400 }
        if ($joined -match '(?i)--help|--version') { return 45 }
        return 1800
    }
    if ($name -eq 'dotnet.exe' -or $name -eq 'dotnet') {
        if ($joined -match '(?i)\btest\b') { return 1800 }
        if ($joined -match '(?i)\bpublish\b') { return 1800 }
        if ($joined -match '(?i)\brestore\b') { return 900 }
        return 120
    }
    if ($name -eq 'winget.exe' -or $name -eq 'winget') {
        if ($joined -match '(?i)\binstall\b|\bupgrade\b') { return 1800 }
        return 120
    }
    if ($name -eq 'python.exe' -or $name -eq 'python' -or $name -eq 'py.exe' -or $name -eq 'py') {
        if ($joined -match '(?i)model_download\.py') { return 108000 }
        if ($joined -match '(?i)pip') { return 1800 }
        if ($joined -match '(?i)-c\s') { return 45 }
        return 300
    }
    if ($name -eq 'llama-cli.exe' -or $name -eq 'llama-server.exe') { return 90 }
    if ($name -eq 'flashnext.manager.exe') { return 180 }
    if ($joined -match '(?i)--version|--help|list-sdks|-latest') { return 45 }
    return 120
}

function Initialize-FlashNextJobObjectSupport {
    if ('FlashNext.Native.JobObject' -as [type]) { return }
    $definition = @'
using System;
using System.Runtime.InteropServices;
namespace FlashNext.Native
{
    public static class JobObject
    {
        public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
        public const int JobObjectExtendedLimitInformation = 9;
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetInformationJobObject(IntPtr job, int informationClass, IntPtr information, uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool TerminateJobObject(IntPtr job, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);
        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }
    }
}
'@
    Add-Type -TypeDefinition $definition -Language CSharp -ErrorAction Stop
}

function New-FlashNextJobObject {
    Initialize-FlashNextJobObjectSupport
    $handle = [FlashNext.Native.JobObject]::CreateJobObject([IntPtr]::Zero, ('FlashNext-Job-' + [Guid]::NewGuid().ToString('N')))
    if ($handle -eq [IntPtr]::Zero) { return [IntPtr]::Zero }
    $info = New-Object FlashNext.Native.JobObject+JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    $info.BasicLimitInformation.LimitFlags = [FlashNext.Native.JobObject]::JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
    $length = [Runtime.InteropServices.Marshal]::SizeOf($info)
    $pointer = [Runtime.InteropServices.Marshal]::AllocHGlobal($length)
    try {
        [Runtime.InteropServices.Marshal]::StructureToPtr($info, $pointer, $false)
        [void][FlashNext.Native.JobObject]::SetInformationJobObject($handle, [FlashNext.Native.JobObject]::JobObjectExtendedLimitInformation, $pointer, [uint32]$length)
    }
    finally {
        [Runtime.InteropServices.Marshal]::FreeHGlobal($pointer)
    }
    return $handle
}

function Stop-FlashNextProcessTree {
    param(
        [Parameter(Mandatory=$true)][int]$ProcessId,
        [int]$GraceMilliseconds = 2000,
        [System.IntPtr]$JobHandle = [IntPtr]::Zero
    )
    $terminated = $false
    if ($JobHandle -ne [IntPtr]::Zero) {
        try { $terminated = [FlashNext.Native.JobObject]::TerminateJobObject($JobHandle, 1) } catch { }
    }
    if ($GraceMilliseconds -gt 0) { Start-Sleep -Milliseconds $GraceMilliseconds }
    $alive = $false
    try { $alive = -not (Get-Process -Id $ProcessId -ErrorAction Stop).HasExited } catch { $alive = $false }
    if ($alive) {
        $taskkill = Join-Path $env:SystemRoot 'System32\taskkill.exe'
        if (Test-Path -LiteralPath $taskkill -PathType Leaf) {
            try { & $taskkill /T /PID $ProcessId | Out-Null } catch { }
            Start-Sleep -Milliseconds 400
            try { & $taskkill /F /T /PID $ProcessId | Out-Null } catch { }
        }
    }
    try { return -not (Get-Process -Id $ProcessId -ErrorAction Stop) } catch { return $true }
}

function Convert-FlashNextInstallState {
    param(
        [Parameter(Mandatory=$true)]$State,
        [Parameter(Mandatory=$true)][string]$ProjectRevision,
        [Parameter(Mandatory=$true)][string]$SourceRoot,
        [string]$StatePath
    )
    if (-not $State) { $State = [pscustomobject]@{} }
    $recorded = ''
    if ($State.PSObject.Properties.Name -contains 'projectRevision') { $recorded = [string]$State.projectRevision }
    $changed = $false
    if ($recorded -ne $ProjectRevision) {
        $backup = $null
        if ($StatePath -and (Test-Path -LiteralPath $StatePath -PathType Leaf)) {
            $backup = $StatePath + '.pre-' + $ProjectRevision + '.bak'
            Copy-Item -LiteralPath $StatePath -Destination $backup -Force
        }
        $stage = 'new'
        if ($State.PSObject.Properties.Name -contains 'stage') { $stage = [string]$State.stage }
        $keepDependencies = $false
        if ($recorded -eq '1.0.4' -and $ProjectRevision -eq '1.0.5') {
            $keepDependencies = @('dependencies-complete','build-complete','machine-complete','hardware-complete','model-complete','complete') -contains $stage
            if ($keepDependencies) { $stage = 'dependencies-complete' } else { $stage = 'new' }
        } else {
            $stage = 'new'
        }
        $State | Add-Member -NotePropertyName projectRevision -NotePropertyValue $ProjectRevision -Force
        $State | Add-Member -NotePropertyName migratedFromRevision -NotePropertyValue $recorded -Force
        $State | Add-Member -NotePropertyName migratedAtUtc -NotePropertyValue ([DateTimeOffset]::UtcNow.ToString('o')) -Force
        $State | Add-Member -NotePropertyName stage -NotePropertyValue $stage -Force
        $State | Add-Member -NotePropertyName sourceRoot -NotePropertyValue $SourceRoot -Force
        $State | Add-Member -NotePropertyName vulkanProbeKind -NotePropertyValue 'direct-vulkan-memory-probe' -Force
        $State | Add-Member -NotePropertyName vulkanHardwareResultInvalidated -NotePropertyValue $true -Force
        $State | Add-Member -NotePropertyName vulkanProbeVersion -NotePropertyValue 0 -Force
        if ($backup) { $State | Add-Member -NotePropertyName stateBackupPath -NotePropertyValue $backup -Force }
        $changed = $true
    }

    $recordedSource = ''
    if ($State.PSObject.Properties.Name -contains 'sourceRoot') { $recordedSource = [string]$State.sourceRoot }
    if ($recordedSource) {
        try {
            $recordedFull = [System.IO.Path]::GetFullPath($recordedSource)
            $expectedFull = [System.IO.Path]::GetFullPath($SourceRoot)
            if (-not $recordedFull.Equals($expectedFull, [StringComparison]::OrdinalIgnoreCase)) {
                $State | Add-Member -NotePropertyName previousSourceRoot -NotePropertyValue $recordedFull -Force
                $State | Add-Member -NotePropertyName sourceRoot -NotePropertyValue $SourceRoot -Force
                $currentStage = 'new'
                if ($State.PSObject.Properties.Name -contains 'stage') { $currentStage = [string]$State.stage }
                if (@('build-complete','machine-complete','hardware-complete','model-complete','complete') -contains $currentStage) {
                    $State | Add-Member -NotePropertyName stage -NotePropertyValue 'dependencies-complete' -Force
                }
                $changed = $true
            }
        } catch { }
    } else {
        $State | Add-Member -NotePropertyName sourceRoot -NotePropertyValue $SourceRoot -Force
        $changed = $true
    }
    return [pscustomobject]@{ State = $State; Changed = $changed }
}

function Invoke-FlashNextNative {
    param(
        [Parameter(Mandatory=$true)][string]$FilePath,
        [AllowEmptyCollection()][string[]]$Arguments = @(),
        [string]$WorkingDirectory = $pwd.Path,
        [int]$SuccessCode = 0,
        [int]$TimeoutSeconds = 0,
        [int]$HeartbeatSeconds = 15,
        [int]$GraceMilliseconds = 2000,
        [string]$Stage = '',
        [switch]$AllowRebootCode,
        [switch]$PassThru,
        [switch]$IgnoreExitCode,
        [switch]$StreamOutput
    )
    if ($TimeoutSeconds -le 0) { $TimeoutSeconds = Get-FlashNextDefaultTimeoutSeconds -FilePath $FilePath -Arguments @($Arguments) }
    if ($TimeoutSeconds -le 0) { throw "A positive timeout is required for '$FilePath'. Indefinite process waits are not permitted." }
    $displayArguments = Get-FlashNextRedactedText -Value (($Arguments | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' ')
    Write-FlashNextLog ('Running: {0} {1}' -f $FilePath, $displayArguments)
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $FilePath
    $start.WorkingDirectory = $WorkingDirectory
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.CreateNoWindow = $true
    $start.StandardOutputEncoding = New-Object System.Text.UTF8Encoding $false
    $start.StandardErrorEncoding = New-Object System.Text.UTF8Encoding $false
    try {
        $start.EnvironmentVariables['PYTHONUNBUFFERED'] = '1'
        $start.EnvironmentVariables['PYTHONIOENCODING'] = 'utf-8'
    } catch { }
    $usedArgumentList = $false
    try {
        if ($null -ne $start.ArgumentList) {
            foreach ($argument in @($Arguments)) { [void]$start.ArgumentList.Add([string]$argument) }
            $usedArgumentList = ($start.ArgumentList.Count -eq @($Arguments).Count)
        }
    } catch { $usedArgumentList = $false }
    if (-not $usedArgumentList) {
        $start.Arguments = (($Arguments | ForEach-Object { ConvertTo-FlashNextWindowsArgument -Value $_ }) -join ' ')
    }
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $start
    $job = [IntPtr]::Zero
    $stdout = ''
    $stderr = ''
    $timedOut = $false
    $interrupted = $false
    $terminationSucceeded = $true
    $startedAt = [DateTimeOffset]::UtcNow
    $pidValue = 0
    $script:FlashNextLastExitCode = $null
    try {
        if (-not $process.Start()) { throw "Could not start '$FilePath'." }
        $pidValue = [int]$process.Id
        Write-FlashNextLog ('Process started: executable={0} pid={1} timeoutSeconds={2} workingDirectory={3}' -f $FilePath, $pidValue, $TimeoutSeconds, $WorkingDirectory)
        try {
            $job = New-FlashNextJobObject
            if ($job -ne [IntPtr]::Zero) { [void][FlashNext.Native.JobObject]::AssignProcessToJobObject($job, $process.Handle) }
        } catch { $job = [IntPtr]::Zero }
        $stdoutTask = $null
        $stderrTask = $null
        $stdoutBuilder = $null
        $stderrBuilder = $null
        if ([bool]$StreamOutput) {
            $stdoutBuilder = New-Object System.Text.StringBuilder
            $stderrBuilder = New-Object System.Text.StringBuilder
            $script:FlashNextStreamStdout = $stdoutBuilder
            $script:FlashNextStreamStderr = $stderrBuilder
            $process.add_OutputDataReceived({
                param($eventSender, $eventArgs)
                if ($eventArgs -and -not [string]::IsNullOrEmpty($eventArgs.Data)) {
                    [void]$script:FlashNextStreamStdout.AppendLine($eventArgs.Data)
                    Write-Host $eventArgs.Data
                }
            })
            $process.add_ErrorDataReceived({
                param($eventSender, $eventArgs)
                if ($eventArgs -and -not [string]::IsNullOrEmpty($eventArgs.Data)) {
                    [void]$script:FlashNextStreamStderr.AppendLine($eventArgs.Data)
                    Write-Host $eventArgs.Data
                }
            })
            $process.BeginOutputReadLine()
            $process.BeginErrorReadLine()
        } else {
            $stdoutTask = $process.StandardOutput.ReadToEndAsync()
            $stderrTask = $process.StandardError.ReadToEndAsync()
        }
        $lastHeartbeat = Get-Date
        do {
            $exited = $false
            try { $exited = [bool]$process.HasExited } catch { $exited = $true }
            if ($exited) { break }
            Start-Sleep -Milliseconds 200
            $elapsed = ([DateTimeOffset]::UtcNow - $startedAt).TotalSeconds
            if ($HeartbeatSeconds -gt 0 -and ((Get-Date) - $lastHeartbeat).TotalSeconds -ge $HeartbeatSeconds) {
                Write-FlashNextLog ('Still running pid={0} elapsed={1:N0}s timeout={2}s stage={3}' -f $pidValue, $elapsed, $TimeoutSeconds, $Stage)
                $downloadLog = [string]$env:FLASHNEXT_DOWNLOAD_LOG
                if ($downloadLog -and (Test-Path -LiteralPath $downloadLog -PathType Leaf)) {
                    try {
                        $progressLine = Get-Content -LiteralPath $downloadLog -Tail 1 -ErrorAction Stop
                        if ($progressLine) { Write-FlashNextLog ('download-log: {0}' -f $progressLine) }
                    } catch { }
                }
                $lastHeartbeat = Get-Date
            }
            if ($elapsed -ge $TimeoutSeconds) {
                $timedOut = $true
                break
            }
        } while ($true)
        if ($timedOut) {
            Write-FlashNextLog ('Timeout: terminating process tree pid={0} after {1:N0}s' -f $pidValue, ([DateTimeOffset]::UtcNow - $startedAt).TotalSeconds) 'WARN'
            $terminationSucceeded = Stop-FlashNextProcessTree -ProcessId $pidValue -GraceMilliseconds $GraceMilliseconds -JobHandle $job
            try { if (-not $process.HasExited) { $process.Kill() } } catch { }
            $killWait = Get-Date
            while ($true) {
                $dead = $false
                try { $dead = [bool]$process.HasExited } catch { $dead = $true }
                if ($dead) { break }
                if (((Get-Date) - $killWait).TotalSeconds -ge 10) { break }
                Start-Sleep -Milliseconds 200
            }
        }
        if ($stdoutTask) {
            try {
                if ($stdoutTask.Wait(5000)) { $stdout = $stdoutTask.Result } else { $stdout = '' }
            } catch { $stdout = '' }
        }
        if ($stderrTask) {
            try {
                if ($stderrTask.Wait(5000)) { $stderr = $stderrTask.Result } else { $stderr = '' }
            } catch { $stderr = '' }
        }
        if ($stdoutBuilder) {
            Start-Sleep -Milliseconds 200
            $stdout = $stdoutBuilder.ToString()
            $stderr = $stderrBuilder.ToString()
        }
        try { if ($process.HasExited) { $script:FlashNextLastExitCode = [int]$process.ExitCode } } catch { }
    }
    catch [System.Management.Automation.PipelineStoppedException] {
        $interrupted = $true
        if ($pidValue -gt 0) { $terminationSucceeded = Stop-FlashNextProcessTree -ProcessId $pidValue -GraceMilliseconds $GraceMilliseconds -JobHandle $job }
        throw
    }
    catch [System.OperationCanceledException] {
        $interrupted = $true
        if ($pidValue -gt 0) { $terminationSucceeded = Stop-FlashNextProcessTree -ProcessId $pidValue -GraceMilliseconds $GraceMilliseconds -JobHandle $job }
        throw
    }
    finally {
        if ($job -ne [IntPtr]::Zero) {
            try { [void][FlashNext.Native.JobObject]::CloseHandle($job) } catch { }
        }
        try { $process.Dispose() } catch { }
    }
    $elapsed = [DateTimeOffset]::UtcNow - $startedAt
    $exitCode = $script:FlashNextLastExitCode
    if ($null -eq $exitCode -and $timedOut) { $exitCode = -1 }
    if ($stdout) {
        Add-FlashNextLogLine -Path $script:FlashNextLogPath -Line (Get-FlashNextRedactedText -Value $stdout)
        Add-FlashNextLogLine -Path $script:FlashNextMirrorLogPath -Line (Get-FlashNextRedactedText -Value $stdout)
    }
    if ($stderr) {
        Add-FlashNextLogLine -Path $script:FlashNextLogPath -Line (Get-FlashNextRedactedText -Value $stderr)
        Add-FlashNextLogLine -Path $script:FlashNextMirrorLogPath -Line (Get-FlashNextRedactedText -Value $stderr)
    }
    $result = [pscustomobject]@{
        ExitCode = $exitCode
        StandardOutput = $stdout
        StandardError = $stderr
        FilePath = $FilePath
        Arguments = $Arguments
        WorkingDirectory = $WorkingDirectory
        ProcessId = $pidValue
        StartedAtUtc = $startedAt.ToString('o')
        TimeoutSeconds = $TimeoutSeconds
        Elapsed = $elapsed
        TimedOut = $timedOut
        Interrupted = $interrupted
        TerminationSucceeded = $terminationSucceeded
        Stage = $Stage
    }
    if ($timedOut) {
        $combined = Get-FlashNextRedactedText -Value (($stdout + [Environment]::NewLine + $stderr).Trim())
        $tail = Get-FlashNextTextTail -Text $combined
        $message = @(
            "Command timed out after $TimeoutSeconds seconds."
            "executable=$FilePath"
            "arguments=$displayArguments"
            "workingDirectory=$WorkingDirectory"
            "pid=$pidValue"
            "startedAtUtc=$($result.StartedAtUtc)"
            "timeoutSeconds=$TimeoutSeconds"
            "elapsedSeconds=$([int]$elapsed.TotalSeconds)"
            "terminationSucceeded=$terminationSucceeded"
            "exitCode=$exitCode"
            "stage=$Stage"
            "recovery=Inspect the log, correct the hanging child process, and rerun install.cmd. Completed installer stages remain resumable."
            "stdoutTail=$tail"
        ) -join [Environment]::NewLine
        throw (New-Object System.TimeoutException $message)
    }
    $allowed = @($SuccessCode)
    if ($AllowRebootCode) { $allowed += @(1641, 3010) }
    if (-not $IgnoreExitCode -and $allowed -notcontains $exitCode) {
        $combined = Get-FlashNextRedactedText -Value (($stdout + [Environment]::NewLine + $stderr).Trim())
        $tail = Get-FlashNextTextTail -Text $combined
        if ($tail) { throw ("Command '{0}' failed with exit code {1}. Output tail:`n{2}" -f $FilePath, $exitCode, $tail) }
        $sidecar = [string]$env:FLASHNEXT_DOWNLOAD_LOG
        $sidecarHint = ''
        if ($sidecar -and (Test-Path -LiteralPath $sidecar -PathType Leaf)) {
            try { $sidecarHint = Get-FlashNextTextTail -Text ((Get-Content -LiteralPath $sidecar -Raw -ErrorAction Stop)) } catch { }
        }
        if ($sidecarHint) { throw ("Command '{0}' failed with exit code {1} and produced no captured process output. Sidecar log tail:`n{2}" -f $FilePath, $exitCode, $sidecarHint) }
        throw ("Command '{0}' failed with exit code {1} and produced no captured output. Command line: {2} {3}" -f $FilePath, $exitCode, $FilePath, $displayArguments)
    }
    if ($PassThru) { return $result }
    return $exitCode
}

function Test-FlashNextWindows11X64 {
    $os = Get-CimInstance Win32_OperatingSystem
    $build = [int]$os.BuildNumber
    return ([Environment]::Is64BitOperatingSystem -and $build -ge 22000)
}

function ConvertTo-FlashNextHardwareIdentity {
    param([AllowNull()][string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return '' }
    $normalized = [regex]::Replace($Value.ToUpperInvariant(), '[^A-Z0-9]+', ' ')
    return [regex]::Replace($normalized.Trim(), '\s+', ' ')
}

function Test-FlashNextCpuTarget {
    param([AllowNull()][string]$Name)
    $normalized = ConvertTo-FlashNextHardwareIdentity -Value $Name
    if (-not $normalized) { return $false }
    $tokens = @($normalized -split ' ')
    return (($tokens -contains 'RYZEN') -and ($tokens -contains 'AI') -and ($tokens -contains 'MAX') -and ($tokens -contains '395'))
}

function Test-FlashNextGpuTarget {
    param([AllowNull()][string[]]$Names)
    foreach ($name in @($Names)) {
        $normalized = ConvertTo-FlashNextHardwareIdentity -Value $name
        if (-not $normalized) { continue }
        $tokens = @($normalized -split ' ')
        if (($tokens -contains 'RADEON') -and ($tokens -contains '8060S')) { return $true }
    }
    return $false
}

function Get-FlashNextMemorySnapshot {
    [UInt64]$installedBytes = 0
    [UInt64]$osVisibleBytes = 0
    $source = 'Unavailable'
    $reliable = $false

    try {
        if (-not ('FlashNext.Native.SystemMemory' -as [type])) {
            $definition = @'
using System;
using System.Runtime.InteropServices;
namespace FlashNext.Native
{
    public static class SystemMemory
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);
    }
}
'@
            Add-Type -TypeDefinition $definition -Language CSharp -ErrorAction Stop
        }
        [UInt64]$installedKilobytes = 0
        if ([FlashNext.Native.SystemMemory]::GetPhysicallyInstalledSystemMemory([ref]$installedKilobytes) -and $installedKilobytes -gt 0) {
            $installedBytes = $installedKilobytes * [UInt64]1024
            $source = 'GetPhysicallyInstalledSystemMemory (SMBIOS)'
            $reliable = $true
        }
    } catch {
        Write-FlashNextLog -Message ('SMBIOS installed-memory API was unavailable: {0}' -f $_.Exception.Message) -Level 'WARN'
    }

    if ($installedBytes -eq 0) {
        try {
            foreach ($module in @(Get-CimInstance Win32_PhysicalMemory -ErrorAction Stop)) {
                if ($null -ne $module.Capacity) { $installedBytes += [UInt64]$module.Capacity }
            }
            if ($installedBytes -gt 0) {
                $source = 'Win32_PhysicalMemory.Capacity sum'
                $reliable = $true
            }
        } catch {
            Write-FlashNextLog -Message ('Win32_PhysicalMemory could not report installed memory: {0}' -f $_.Exception.Message) -Level 'WARN'
        }
    }

    try {
        $system = Get-CimInstance Win32_ComputerSystem -ErrorAction Stop
        if ($null -ne $system.TotalPhysicalMemory) { $osVisibleBytes = [UInt64]$system.TotalPhysicalMemory }
    } catch {
        Write-FlashNextLog -Message ('Windows OS-visible memory could not be read: {0}' -f $_.Exception.Message) -Level 'WARN'
    }

    if ($installedBytes -eq 0 -and $osVisibleBytes -gt 0) {
        $installedBytes = $osVisibleBytes
        $source = 'Win32_ComputerSystem.TotalPhysicalMemory fallback'
        $reliable = $false
    }

    return [pscustomobject]@{
        InstalledBytes = $installedBytes
        InstalledGiB = ([double]$installedBytes / 1GB)
        OsVisibleBytes = $osVisibleBytes
        OsVisibleGiB = ([double]$osVisibleBytes / 1GB)
        Source = $source
        ReliableInstalledCapacity = $reliable
    }
}

function Get-FlashNextPhysicalMemoryGiB {
    $snapshot = Get-FlashNextMemorySnapshot
    return [double]$snapshot.InstalledGiB
}

function Get-FlashNextCpuName {
    return [string](Get-CimInstance Win32_Processor | Select-Object -First 1 -ExpandProperty Name)
}

function Get-FlashNextGpuNames {
    return @((Get-CimInstance Win32_VideoController | Select-Object -ExpandProperty Name))
}

function Refresh-FlashNextPath {
    $machine = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $user = [Environment]::GetEnvironmentVariable('Path', 'User')
    $env:Path = $machine + ';' + $user
}

function Find-FlashNextExecutable {
    param([Parameter(Mandatory=$true)][string[]]$Names, [string[]]$AdditionalPaths)
    foreach ($path in @($AdditionalPaths)) {
        if (-not [string]::IsNullOrWhiteSpace($path) -and (Test-Path -LiteralPath $path -PathType Leaf)) {
            return [System.IO.Path]::GetFullPath($path)
        }
    }
    foreach ($name in $Names) {
        $command = Get-Command $name -ErrorAction SilentlyContinue
        if ($command -and $command.Source) { return $command.Source }
    }
    return $null
}

function Resolve-FlashNextPython313Command {
    $launcherCandidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Python\Launcher\py.exe'),
        (Join-Path $env:ProgramFiles 'Python Launcher\py.exe')
    )
    $launcher = Find-FlashNextExecutable -Names @('py.exe','py') -AdditionalPaths $launcherCandidates
    if ($launcher) {
        $probe = Invoke-FlashNextNative -FilePath $launcher -Arguments @('-3.13','-c','import sys; assert sys.version_info[:2] == (3,13), sys.version') -PassThru -IgnoreExitCode
        if ($probe.ExitCode -eq 0) { return [pscustomobject]@{ Path = $launcher; Prefix = @('-3.13') } }
    }
    $pythonCandidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Python\Python313\python.exe'),
        (Join-Path $env:ProgramFiles 'Python313\python.exe')
    )
    $commands = New-Object System.Collections.Generic.List[string]
    foreach ($candidate in $pythonCandidates) { if ($candidate) { [void]$commands.Add($candidate) } }
    $pathPython = Find-FlashNextExecutable -Names @('python.exe','python')
    if ($pathPython) { [void]$commands.Add($pathPython) }
    $seen = @{}
    foreach ($command in $commands) {
        if ([string]::IsNullOrWhiteSpace($command) -or -not (Test-Path -LiteralPath $command -PathType Leaf)) { continue }
        $full = [System.IO.Path]::GetFullPath($command)
        if ($seen.ContainsKey($full.ToUpperInvariant())) { continue }
        $seen[$full.ToUpperInvariant()] = $true
        $probe = Invoke-FlashNextNative -FilePath $full -Arguments @('-c','import sys; assert sys.version_info[:2] == (3,13), sys.version') -PassThru -IgnoreExitCode
        if ($probe.ExitCode -eq 0) { return [pscustomobject]@{ Path = $full; Prefix = @() } }
    }
    return $null
}

function Get-FlashNextWinGetCandidates {
    param([string]$PreferredPath)
    $candidates = New-Object System.Collections.Generic.List[string]
    if (-not [string]::IsNullOrWhiteSpace($PreferredPath)) { [void]$candidates.Add($PreferredPath) }
    try {
        $package = Get-AppxPackage -Name 'Microsoft.DesktopAppInstaller' -ErrorAction Stop | Sort-Object Version -Descending | Select-Object -First 1
        if ($package -and $package.InstallLocation) { [void]$candidates.Add((Join-Path $package.InstallLocation 'winget.exe')) }
    } catch { }
    if ($env:LOCALAPPDATA) { [void]$candidates.Add((Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\winget.exe')) }
    $command = Get-Command 'winget.exe' -ErrorAction SilentlyContinue
    if ($command -and $command.Source) { [void]$candidates.Add($command.Source) }
    $command = Get-Command 'winget' -ErrorAction SilentlyContinue
    if ($command -and $command.Source) { [void]$candidates.Add($command.Source) }
    if (Test-FlashNextAdministrator) {
        try {
            $windowsApps = Join-Path $env:ProgramFiles 'WindowsApps'
            $directory = Get-ChildItem -LiteralPath $windowsApps -Directory -Filter 'Microsoft.DesktopAppInstaller_*_x64__8wekyb3d8bbwe' -ErrorAction Stop | Sort-Object Name -Descending | Select-Object -First 1
            if ($directory) { [void]$candidates.Add((Join-Path $directory.FullName 'winget.exe')) }
        } catch { }
    }
    $seen = @{}
    foreach ($candidate in $candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
        try { $full = [System.IO.Path]::GetFullPath($candidate) } catch { continue }
        if ($seen.ContainsKey($full.ToUpperInvariant())) { continue }
        $seen[$full.ToUpperInvariant()] = $true
        Write-Output $full
    }
}

function Resolve-FlashNextWinGetPath {
    param([string]$PreferredPath, [switch]$AttemptRegistration)
    foreach ($candidate in @(Get-FlashNextWinGetCandidates -PreferredPath $PreferredPath)) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
        try {
            $probe = Invoke-FlashNextNative -FilePath $candidate -Arguments @('--version') -PassThru -IgnoreExitCode
            if ($probe.ExitCode -eq 0) { return $candidate }
        } catch { }
    }
    if ($AttemptRegistration) {
        try {
            Write-FlashNextLog 'The WinGet application alias is unavailable; registering Microsoft Desktop App Installer for the current user.' 'WARN'
            Add-AppxPackage -RegisterByFamilyName -MainPackage 'Microsoft.DesktopAppInstaller_8wekyb3d8bbwe' -ErrorAction Stop
            foreach ($candidate in @(Get-FlashNextWinGetCandidates)) {
                if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
                $probe = Invoke-FlashNextNative -FilePath $candidate -Arguments @('--version') -PassThru -IgnoreExitCode
                if ($probe.ExitCode -eq 0) { return $candidate }
            }
        } catch {
            Write-FlashNextLog ("Desktop App Installer registration was not available: {0}" -f $_.Exception.Message) 'WARN'
        }
    }
    return $null
}

function Initialize-FlashNextWinGetSource {
    param([Parameter(Mandatory=$true)][string]$WinGetPath)
    $info = Invoke-FlashNextNative -FilePath $WinGetPath -Arguments @('--info') -PassThru
    $sources = Invoke-FlashNextNative -FilePath $WinGetPath -Arguments @('source','list','--disable-interactivity') -PassThru
    if (($sources.StandardOutput + $sources.StandardError) -notmatch '(?im)^\s*winget\s') {
        throw "The official 'winget' package source is not registered for the current Windows user. Open App Installer once or run 'winget source reset --force', then rerun install.cmd. Existing custom sources were not changed automatically."
    }
    Invoke-FlashNextNative -FilePath $WinGetPath -Arguments @('source','update','--name','winget','--disable-interactivity') | Out-Null
    return [pscustomobject]@{ Path = $WinGetPath; Version = ($info.StandardOutput + $info.StandardError).Trim() }
}

function Get-FlashNextFileSha256 {
    param([Parameter(Mandatory=$true)][string]$Path)
    $stream = [IO.File]::OpenRead([IO.Path]::GetFullPath($Path))
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $algorithm.Dispose(); $stream.Dispose() }
}

function Set-FlashNextInstallStage {
    param([Parameter(Mandatory=$true)][string]$StatePath, [Parameter(Mandatory=$true)][string]$Stage, [hashtable]$Additional)
    $state = Read-FlashNextJson -Path $StatePath
    if (-not $state) { $state = [pscustomobject]@{} }
    $state | Add-Member -NotePropertyName stage -NotePropertyValue $Stage -Force
    $state | Add-Member -NotePropertyName updatedAtUtc -NotePropertyValue ([DateTimeOffset]::UtcNow.ToString('o')) -Force
    if ($Additional) {
        foreach ($key in $Additional.Keys) { $state | Add-Member -NotePropertyName $key -NotePropertyValue $Additional[$key] -Force }
    }
    Write-FlashNextJsonAtomic -Path $StatePath -Value $state
}

function Write-FlashNextOperationResult {
    param(
        [Parameter(Mandatory=$true)][string]$Path,
        [Parameter(Mandatory=$true)][ValidateSet('succeeded','failed','reboot-required')][string]$Status,
        [Parameter(Mandatory=$true)][string]$Stage,
        [Parameter(Mandatory=$true)][string]$Message,
        [int]$ExitCode = 0,
        [string]$LogPath,
        [hashtable]$Additional
    )
    $value = [ordered]@{
        schemaVersion = 1
        status = $Status
        stage = $Stage
        message = $Message
        exitCode = $ExitCode
        logPath = $LogPath
        timestampUtc = [DateTimeOffset]::UtcNow.ToString('o')
    }
    if ($Additional) { foreach ($key in $Additional.Keys) { $value[$key] = $Additional[$key] } }
    Write-FlashNextJsonAtomic -Path $Path -Value ([pscustomobject]$value)
}

function Invoke-FlashNextElevated {
    param(
        [Parameter(Mandatory=$true)][string]$ScriptPath,
        [Parameter(Mandatory=$true)][hashtable]$Parameters,
        [string]$ResultPath,
        [string]$OperationName = 'Elevated operation',
        [switch]$PassThru
    )
    $ScriptPath = [System.IO.Path]::GetFullPath($ScriptPath)
    if (-not (Test-Path -LiteralPath $ScriptPath -PathType Leaf)) { throw "Elevated helper is missing: $ScriptPath" }
    if ($ResultPath) {
        $ResultPath = [System.IO.Path]::GetFullPath($ResultPath)
        if (Test-Path -LiteralPath $ResultPath) { Remove-Item -LiteralPath $ResultPath -Force }
    }
    $parameterJson = $Parameters | ConvertTo-Json -Depth 20 -Compress
    $parameterPayload = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($parameterJson))
    $scriptPayload = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($ScriptPath))
    $command = @"
`$ErrorActionPreference = 'Stop'
`$scriptPath = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('$scriptPayload'))
`$parameterJson = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('$parameterPayload'))
`$parameterObject = `$parameterJson | ConvertFrom-Json
`$parameterTable = @{}
foreach (`$property in `$parameterObject.PSObject.Properties) { `$parameterTable[`$property.Name] = `$property.Value }
try {
    & `$scriptPath @parameterTable
    if (`$null -ne `$LASTEXITCODE) { exit [int]`$LASTEXITCODE }
    exit 0
}
catch {
    Write-Error `$_.Exception.Message
    exit 1
}
"@
    $encodedCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    try {
        $process = Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList @('-NoLogo','-NoProfile','-ExecutionPolicy','Bypass','-EncodedCommand',$encodedCommand) -PassThru
        if (-not $process) { throw "Could not start $OperationName." }
        if (-not $process.WaitForExit(7200000)) {
            try { Stop-FlashNextProcessTree -ProcessId $process.Id | Out-Null } catch { }
            throw "$OperationName exceeded the 2-hour administrator-process limit."
        }
    }
    catch {
        if ($_.Exception.Message -match '(?i)canceled|cancelled|1223') { throw "$OperationName was cancelled at the Windows administrator prompt." }
        throw ("Could not start {0}: {1}" -f $OperationName.ToLowerInvariant(), $_.Exception.Message)
    }

    $result = $null
    if ($ResultPath -and (Test-Path -LiteralPath $ResultPath)) {
        try { $result = Read-FlashNextJson -Path $ResultPath }
        catch { throw ("{0} produced an unreadable result file '{1}': {2}" -f $OperationName, $ResultPath, $_.Exception.Message) }
    }
    if ($process.ExitCode -eq 10) {
        $message = 'A dependency requested a reboot. Restart Windows, then rerun install.cmd to continue.'
        if ($result -and $result.message) { $message = [string]$result.message }
        throw $message
    }
    if ($process.ExitCode -ne 0) {
        if ($result) {
            $childMessage = [string]$result.message
            $childStage = [string]$result.stage
            $childLog = [string]$result.logPath
            throw ("{0} failed during stage '{1}': {2} Child log: {3}" -f $OperationName, $childStage, $childMessage, $childLog)
        }
        if ($ResultPath) {
            throw ("{0} failed with exit code {1}, but the helper did not create its required result file '{2}'. This normally indicates a PowerShell parse, parameter-binding, or startup failure." -f $OperationName, $process.ExitCode, $ResultPath)
        }
        throw ("{0} failed with exit code {1}." -f $OperationName, $process.ExitCode)
    }
    if ($ResultPath -and -not $result) { throw ("{0} reported success but did not create its required result file '{1}'." -f $OperationName, $ResultPath) }
    if ($result -and [string]$result.status -ne 'succeeded') {
        throw ("{0} returned status '{1}' during stage '{2}': {3}" -f $OperationName, $result.status, $result.stage, $result.message)
    }
    if ($PassThru) { return $result }
}

function New-FlashNextShortcut {
    param([Parameter(Mandatory=$true)][string]$TargetPath)
    $programs = [Environment]::GetFolderPath('Programs')
    $folder = Join-Path $programs 'FlashNext'
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    $managerPath = Join-Path $folder 'FlashNext Manager.lnk'
    $manager = $shell.CreateShortcut($managerPath)
    $manager.TargetPath = $TargetPath
    $manager.WorkingDirectory = Split-Path -Parent $TargetPath
    $manager.Description = 'FlashNext console manager'
    $manager.Save()
    $dashboardExe = Join-Path (Split-Path -Parent $TargetPath) 'FlashNext.Dashboard.exe'
    if (Test-Path -LiteralPath $dashboardExe -PathType Leaf) {
        $dashPath = Join-Path $folder 'FlashNext.lnk'
        $dash = $shell.CreateShortcut($dashPath)
        $dash.TargetPath = $dashboardExe
        $dash.WorkingDirectory = Split-Path -Parent $dashboardExe
        $dash.Description = 'FlashNext tray dashboard'
        $dash.IconLocation = $dashboardExe + ',0'
        $dash.Save()
    }
    return $managerPath
}
