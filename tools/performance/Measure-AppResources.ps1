param(
    [ValidateRange(5, 300)][int]$Seconds = 30,
    [ValidateRange(100, 5000)][int]$IntervalMilliseconds = 500,
    [string]$Installation = "$env:LOCALAPPDATA/Programs/TajemnikTV/TajsTokens/current",
    [string]$OutputPath = '.codex/temp/stage1/resources.json',
    [switch]$LaunchBackground,
    [switch]$PhysicalDiskCounters
)

$ErrorActionPreference = 'Stop'
if (-not ('TajsTokensResourceProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class TajsTokensResourceProbe {
    [StructLayout(LayoutKind.Sequential)]
    public struct IoCounters {
        public ulong ReadOperations, WriteOperations, OtherOperations;
        public ulong ReadBytes, WriteBytes, OtherBytes;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetProcessIoCounters(IntPtr handle, out IoCounters counters);
}
'@
}

$installationPath = [IO.Path]::GetFullPath($Installation)
$identityPath = Join-Path $installationPath 'build-identity.json'
$identity = if (Test-Path -LiteralPath $identityPath) {
    (Get-Content -LiteralPath $identityPath -Raw | ConvertFrom-Json).identity
} else { 'unrecorded' }
$appExecutablePath = Join-Path $installationPath 'TajsTokens.App.exe'
$executableSha256 = if (Test-Path -LiteralPath $appExecutablePath) {
    (Get-FileHash -LiteralPath $appExecutablePath -Algorithm SHA256).Hash
} else { $null }
$buildManifestSha256 = if (Test-Path -LiteralPath $identityPath) {
    (Get-FileHash -LiteralPath $identityPath -Algorithm SHA256).Hash
} else { $null }
function Read-PhysicalDisks {
    @(Get-CimInstance Win32_PerfRawData_PerfDisk_PhysicalDisk | Where-Object Name -ne '_Total' |
        Select-Object Name,DiskReadBytesPersec,DiskWriteBytesPersec,Timestamp_PerfTime,Frequency_PerfTime)
}
$diskBefore = @()
$physicalDiskError = $null
if ($PhysicalDiskCounters) {
    try { $diskBefore = Read-PhysicalDisks }
    catch { $physicalDiskError = $_.Exception.GetType().Name }
}
$started = [DateTimeOffset]::UtcNow
if ($LaunchBackground) {
    if (Get-Process -Name TajsTokens.App -ErrorAction SilentlyContinue) {
        throw 'App already running; omit LaunchBackground to sample it.'
    }
    Start-Process -FilePath (Join-Path $installationPath 'TajsTokens.App.exe') `
        -ArgumentList '--background' -WindowStyle Hidden | Out-Null
}

$clock = [Diagnostics.Stopwatch]::StartNew()
$samples = [Collections.Generic.List[object]]::new()
$combinedSamples = [Collections.Generic.List[object]]::new()
while ($clock.Elapsed.TotalSeconds -lt $Seconds) {
    $cycle = [Collections.Generic.List[object]]::new()
    $roots = @{}
    $birth = @{}
    foreach ($app in @(Get-Process -Name TajsTokens.App -ErrorAction SilentlyContinue)) {
        try { if ([IO.Path]::GetDirectoryName($app.Path) -eq $installationPath) {
            $roots[$app.Id] = $app.Id; $birth[$app.Id] = $app.StartTime.ToUniversalTime()
        } }
        finally { $app.Dispose() }
    }
    # Include app-server launchers and their children as well as calibration. An
    # installation-qualified root owns the tree; unrelated Codex processes stay out.
    $tree = @{}
    $nodes = @()
    if ($roots.Count -gt 0) {
        $nodes = @(Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId,CreationDate)
        foreach ($key in $roots.Keys) { $tree[$key] = $key }
        do {
            $added = $false
            foreach ($node in $nodes) {
                $nodeId = [int]$node.ProcessId; $ownerId = [int]$node.ParentProcessId
                if (-not $tree.ContainsKey($nodeId) -and $tree.ContainsKey($ownerId) -and
                    $node.CreationDate.ToUniversalTime() -ge $birth[$ownerId]) {
                    $tree[$nodeId] = $tree[$ownerId]; $added = $true
                    $birth[$nodeId] = $node.CreationDate.ToUniversalTime()
                }
            }
        } while ($added)
    }
    foreach ($process in @(if ($tree.Count -gt 0) { Get-Process -Id @($tree.Keys) -ErrorAction SilentlyContinue })) {
        try {
            $process.Refresh()
            $creation = $process.StartTime.ToUniversalTime().ToString('O')
            $native = $nodes | Where-Object { [int]$_.ProcessId -eq $process.Id } | Select-Object -First 1
            if ($null -eq $native -or [Math]::Abs(($process.StartTime.ToUniversalTime()-$native.CreationDate.ToUniversalTime()).TotalMilliseconds) -gt 2) { continue }
            $parentId = $tree[$process.Id]
            $io = [TajsTokensResourceProbe+IoCounters]::new()
            $ioAvailable = [TajsTokensResourceProbe]::GetProcessIoCounters($process.Handle, [ref]$io)
            $sample = [pscustomobject]@{
                processId = $process.Id
                processName = $process.ProcessName
                parentAppProcessId = $parentId
                processCreatedAtUtc = $creation
                role = if ($roots.ContainsKey($process.Id)) { 'app' } elseif ($process.ProcessName -eq 'TajsTokens.ForecastEvaluation') { 'worker' } else { 'helper' }
                elapsedSeconds = $clock.Elapsed.TotalSeconds
                cpuSeconds = $process.TotalProcessorTime.TotalSeconds
                privateCommitBytes = $process.PrivateMemorySize64
                workingSetBytes = $process.WorkingSet64
                handles = $process.HandleCount
                threads = $process.Threads.Count
                ioAvailable = $ioAvailable
                logicalReadBytes = if ($ioAvailable) { $io.ReadBytes } else { $null }
                logicalWriteBytes = if ($ioAvailable) { $io.WriteBytes } else { $null }
                readOperations = if ($ioAvailable) { $io.ReadOperations } else { $null }
                writeOperations = if ($ioAvailable) { $io.WriteOperations } else { $null }
            }
            $samples.Add($sample)
            $cycle.Add($sample)
        } catch [InvalidOperationException] {
            # A worker can finish between enumeration and sampling.
        } finally {
            $process.Dispose()
        }
    }
    if ($cycle.Count -gt 0) {
        $combinedSamples.Add([pscustomobject]@{
            elapsedSeconds = $clock.Elapsed.TotalSeconds
            privateCommitMiB = ($cycle.privateCommitBytes | Measure-Object -Sum).Sum / 1MB
            processCount = $cycle.Count
        })
    }
    Start-Sleep -Milliseconds $IntervalMilliseconds
}
$diskSummaries = @()
if ($PhysicalDiskCounters -and $diskBefore.Count -gt 0) {
    try {
        $diskAfter = Read-PhysicalDisks
        $diskSummaries = @(foreach ($before in $diskBefore) {
            $after = $diskAfter | Where-Object Name -eq $before.Name | Select-Object -First 1
            if ($null -eq $after) { continue }
            $duration = ([decimal]$after.Timestamp_PerfTime - [decimal]$before.Timestamp_PerfTime) / [decimal]$after.Frequency_PerfTime
            $read = [decimal]$after.DiskReadBytesPersec - [decimal]$before.DiskReadBytesPersec
            $write = [decimal]$after.DiskWriteBytesPersec - [decimal]$before.DiskWriteBytesPersec
            $valid = $duration -gt 0 -and $read -ge 0 -and $write -ge 0 -and $after.Frequency_PerfTime -eq $before.Frequency_PerfTime
            [pscustomobject]@{
                device = $before.Name
                elapsedSeconds = $duration
                readBytes = if ($valid) { $read } else { $null }
                writeBytes = if ($valid) { $write } else { $null }
                readBytesPerSecond = if ($valid) { $read / $duration } else { $null }
                writeBytesPerSecond = if ($valid) { $write / $duration } else { $null }
            }
        })
    } catch { $physicalDiskError = $_.Exception.GetType().Name }
}
$summaries = foreach ($group in $samples | Group-Object processId,processCreatedAtUtc) {
    $first = $group.Group[0]
    $last = $group.Group[-1]
    $elapsed = $last.elapsedSeconds - $first.elapsedSeconds
    [pscustomobject]@{
        role = $first.role
        processId = $first.processId
        processName = $first.processName
        processCreatedAtUtc = $first.processCreatedAtUtc
        sampleCount = $group.Count
        elapsedSeconds = $elapsed
        totalSystemCpuPercent = if ($elapsed -gt 0) {
            100 * ($last.cpuSeconds - $first.cpuSeconds) / $elapsed / [Environment]::ProcessorCount
        } else { $null }
        peakPrivateCommitMiB = ($group.Group.privateCommitBytes | Measure-Object -Maximum).Maximum / 1MB
        finalPrivateCommitMiB = $last.privateCommitBytes / 1MB
        peakWorkingSetMiB = ($group.Group.workingSetBytes | Measure-Object -Maximum).Maximum / 1MB
        logicalReadBytes = if ($first.ioAvailable -and $last.ioAvailable) { $last.logicalReadBytes - $first.logicalReadBytes } else { $null }
        logicalWriteBytes = if ($first.ioAvailable -and $last.ioAvailable) { $last.logicalWriteBytes - $first.logicalWriteBytes } else { $null }
    }
}
$result = [ordered]@{
    sourceIdentity = $identity
    executableSha256 = $executableSha256
    buildManifestSha256 = $buildManifestSha256
    startedAtUtc = $started.ToString('O')
    durationSeconds = $clock.Elapsed.TotalSeconds
    logicalProcessors = [Environment]::ProcessorCount
    qualification = 'An installation-qualified app owns the observed child tree, including calibration and transient helpers. Process I/O is logical, not physical disk I/O. Device counters are system-wide physical-disk traffic and cannot be attributed to this app. Private commit and working set are distinct; working sets can share pages. Peaks are sampled, not OS lifetime peaks; combined peaks sum one sampling cycle, not separate process peaks. Child CPU/I/O covers only its observed lifetime; processes shorter than the sampling interval and unsampled start/end work are unknown. Samples do not establish visibility, a memory leak or a whole-cycle bound.'
    processes = @($summaries)
    peakCombinedPrivateCommitMiB = if ($combinedSamples.Count -gt 0) { ($combinedSamples.privateCommitMiB | Measure-Object -Maximum).Maximum } else { $null }
    finalCombinedPrivateCommitMiB = if ($combinedSamples.Count -gt 0) { $combinedSamples[$combinedSamples.Count-1].privateCommitMiB } else { $null }
    systemPhysicalDisks = @($diskSummaries)
    physicalDiskError = $physicalDiskError
    combinedSamples = @($combinedSamples.ToArray())
    samples = @($samples.ToArray())
}
$output = [IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output)) | Out-Null
[IO.File]::WriteAllText($output, ($result | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
$summaries | Format-Table -AutoSize
Write-Output "Saved $output"
