param(
    [ValidateRange(5, 300)][int]$Seconds = 30,
    [ValidateRange(100, 5000)][int]$IntervalMilliseconds = 500,
    [string]$Installation = "$env:LOCALAPPDATA/Programs/TajemnikTV/TajsTokens/current",
    [string]$OutputPath = '.codex/temp/stage1/resources.json',
    [switch]$LaunchBackground
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
while ($clock.Elapsed.TotalSeconds -lt $Seconds) {
    foreach ($process in @(Get-Process -Name TajsTokens.App,TajsTokens.ForecastEvaluation -ErrorAction SilentlyContinue)) {
        try {
            if ([IO.Path]::GetDirectoryName($process.Path) -ne $installationPath) { continue }
            $process.Refresh()
            $io = [TajsTokensResourceProbe+IoCounters]::new()
            $ioAvailable = [TajsTokensResourceProbe]::GetProcessIoCounters($process.Handle, [ref]$io)
            $samples.Add([pscustomobject]@{
                processId = $process.Id
                role = if ($process.ProcessName -eq 'TajsTokens.App') { 'app' } else { 'worker' }
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
            })
        } catch [InvalidOperationException] {
            # A worker can finish between enumeration and sampling.
        } finally {
            $process.Dispose()
        }
    }
    Start-Sleep -Milliseconds $IntervalMilliseconds
}
$summaries = foreach ($group in $samples | Group-Object processId) {
    $first = $group.Group[0]
    $last = $group.Group[-1]
    $elapsed = $last.elapsedSeconds - $first.elapsedSeconds
    [pscustomobject]@{
        role = $first.role
        processId = $first.processId
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
    startedAtUtc = $started.ToString('O')
    durationSeconds = $clock.Elapsed.TotalSeconds
    logicalProcessors = [Environment]::ProcessorCount
    qualification = 'Process I/O is logical, not physical disk I/O. Private commit and working set are distinct. Samples do not establish a memory leak or a whole-cycle bound.'
    processes = @($summaries)
    samples = @($samples.ToArray())
}
$output = [IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output)) | Out-Null
[IO.File]::WriteAllText($output, ($result | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
$summaries | Format-Table -AutoSize
Write-Output "Saved $output"
