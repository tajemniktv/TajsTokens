$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSHOME 'Modules/Microsoft.PowerShell.Management/Microsoft.PowerShell.Management.psd1')
Import-Module (Join-Path $PSHOME 'Modules/Microsoft.PowerShell.Utility/Microsoft.PowerShell.Utility.psd1')

# Disposable staging stays in the repository; successful installs prune managed recovery history.
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$root = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs/TajemnikTV/TajsTokens'))
$data = Join-Path $root 'data'
$legacyData = Join-Path $env:LOCALAPPDATA 'TajsTokens'
$current = Join-Path $root 'current'
$previous = Join-Path $root 'previous'
$pending = Join-Path $root 'pending'
$journal = Join-Path $root 'transaction.json'

function Assert-PlainTree([string]$Path) {
    if (Test-Path -LiteralPath $Path) {
        $items = @((Get-Item -LiteralPath $Path -Force)) + @(Get-ChildItem -LiteralPath $Path -Recurse -Force)
        if ($items | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) {
            throw "Refusing reparse points in $Path"
        }
    }
}

function Move-InstallDirectory([string]$Source, [string]$Destination) {
    foreach ($path in @($Source, $Destination)) {
        if (-not [IO.Path]::GetFullPath($path).StartsWith([IO.Path]::GetFullPath($root) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Path outside installation: $path"
        }
    }
    Assert-PlainTree $Source
    if (Test-Path -LiteralPath $Destination) { throw "Move destination already exists: $Destination" }
    for ($attempt = 0; ; $attempt++) {
        try { Move-Item -LiteralPath $Source -Destination $Destination -ErrorAction Stop; break }
        catch [IO.IOException] {
            if ($attempt -ge 9 -or ($_.Exception.HResult -band 0xffff) -notin @(32, 33)) { throw }
            Start-Sleep -Milliseconds 200
        }
    }
}

function Get-AppProcesses {
    @(Get-Process -Name 'TajsTokens.App' -ErrorAction SilentlyContinue)
}

function Remove-ObsoleteDeployments([string]$KeepBackup) {
    if (Test-Path -LiteralPath $journal) { throw 'Cannot prune an unfinished deployment' }
    $backupRoot = [IO.Path]::GetFullPath((Join-Path $root 'backups'))
    $keep = [IO.Path]::GetFullPath($KeepBackup)
    $generation = '\d{8}T\d{9}Z-[0-9a-f]{8}'
    if ([IO.Path]::GetDirectoryName($keep) -ne $backupRoot -or
        [IO.Path]::GetFileName($keep) -notmatch "^$generation`$" -or
        -not (Test-Path -LiteralPath $keep -PathType Container)) { throw 'Invalid recovery backup to keep' }
    foreach ($container in @($backupRoot, [IO.Path]::GetFullPath((Join-Path $root 'retained')))) {
        Assert-PlainTree $container
        if (-not (Test-Path -LiteralPath $container)) { continue }
        foreach ($item in Get-ChildItem -LiteralPath $container -Force) {
            $target = [IO.Path]::GetFullPath($item.FullName)
            if ($target -eq $keep) { continue }
            $pattern = if ($container -eq $backupRoot) { "^$generation`$" } else { "^(failed-|unused-)?$generation`$" }
            if (-not $item.PSIsContainer -or $item.Name -notmatch $pattern -or
                [IO.Path]::GetDirectoryName($target) -ne $container) {
                Write-Warning "Unrecognized deployment material preserved: $target" -WarningAction Continue
                continue
            }
            Assert-PlainTree $target
            Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction Stop
        }
    }
}

function Stop-App {
    $processes = @(Get-AppProcesses)
    # Validate every process before asking any of them to stop.
    foreach ($process in $processes) {
        # Refuse unknown/other-session processes rather than terminate a namesake.
        $path = $process.Path
        if (-not $path -or $process.SessionId -ne (Get-Process -Id $PID).SessionId -or
            (-not $path.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase) -and
             -not $path.StartsWith($repo + '\', [StringComparison]::OrdinalIgnoreCase))) {
            throw "Unmanaged TajsTokens process $($process.Id) at '$path'. Exit it explicitly before deploying."
        }
    }
    if ($processes.Count -gt 1) { throw 'Exit extra TajsTokens instances before deploying; no app has been asked to stop.' }
    $signals = @()
    try {
        foreach ($process in $processes) {
            try { $signals += [Threading.EventWaitHandle]::OpenExisting("Local\TajsTokens.Stop.$($process.Id)") }
            catch [Threading.WaitHandleCannotBeOpenedException] {
                throw "App $($process.Id) predates graceful updates or is busy. Exit it from its tray before deploying; no app has been asked to stop."
            }
        }
        foreach ($signal in $signals) { [void]$signal.Set() }
        foreach ($process in $processes) {
            if (-not $process.WaitForExit(30000)) { throw "App $($process.Id) did not exit gracefully. Exit it from the tray and rebuild; no force-kill was used." }
        }
    } finally { foreach ($signal in $signals) { $signal.Dispose() } }
    if (Get-AppProcesses) { throw 'Another app instance appeared during shutdown; deployment aborted.' }
}

function Start-App([string]$Directory) {
    $exe = Join-Path $Directory 'TajsTokens.App.exe'
    $process = Start-Process -FilePath $exe -ArgumentList '--dogfood-start' -WorkingDirectory $Directory -WindowStyle Hidden -PassThru
    try {
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    $ready = $false
    while ([DateTime]::UtcNow -lt $deadline) {
        $process.Refresh()
        if ($process.HasExited) { throw "App exited during startup ($($process.ExitCode))." }
        $signal = $null
        try {
            $signal = [Threading.EventWaitHandle]::OpenExisting("Local\TajsTokens.Ready.$($process.Id)")
            $ready = $signal.WaitOne(0)
        } catch [Threading.WaitHandleCannotBeOpenedException] {
        } finally { if ($signal) { $signal.Dispose() } }
        if ($ready) { break }
        Start-Sleep -Milliseconds 250
    }
    if (-not $ready) { throw 'App did not acknowledge startup within 45 seconds.' }
    if ($process.WaitForExit(3000)) { throw 'App exited immediately after startup.' }
    $signal = [Threading.EventWaitHandle]::OpenExisting("Local\TajsTokens.Ready.$($process.Id)")
    try { if (!$signal.WaitOne(0)) { throw 'App withdrew readiness during startup.' } }
    finally { $signal.Dispose() }
    Write-Host "Dogfood app ready: PID $($process.Id), $exe"
    } catch {
        # Only the deployment-owned child may be terminated after failed startup.
        # Pre-existing user instances remain protected by Stop-App's graceful-only policy.
        if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
        throw
    } finally { $process.Dispose() }
}

function Validate-Publish([string]$Directory) {
    Assert-PlainTree $Directory
    foreach ($file in @('TajsTokens.App.exe', 'TajsTokens.App.dll', 'TajsTokens.App.deps.json',
        'TajsTokens.App.runtimeconfig.json', 'TajsTokens.Core.dll', 'TajsTokens.Infrastructure.dll',
        'Microsoft.UI.Xaml.dll', 'coreclr.dll', 'e_sqlite3.dll')) {
        $path = Join-Path $Directory $file
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) {
            throw "Incomplete publish: missing/empty $file"
        }
    }
}

function Get-Sha256([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $hash = [Security.Cryptography.SHA256]::Create()
    try { [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-', '') }
    finally { $stream.Dispose(); $hash.Dispose() }
}

function Set-Launcher {
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) 'TajsTokens.lnk'))
    $shortcut.TargetPath = Join-Path $current 'TajsTokens.App.exe'
    $shortcut.WorkingDirectory = $current
    $shortcut.Save()
}

function Invoke-DogfoodDeployment {
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$Rollback
)
foreach ($name in @('CI', 'GITHUB_ACTIONS', 'TF_BUILD', 'BUILD_BUILDID', 'JENKINS_URL', 'TEAMCITY_VERSION')) {
    $value = [Environment]::GetEnvironmentVariable($name)
    if ($value -and $value -notin @('false', '0')) { Write-Host "Skipping dogfood in CI ($name)."; return }
}
if ($env:DogfoodEnabled -eq 'false' -or $env:Dogfood -eq 'false') {
    Write-Host 'Dogfooding is disabled by the environment.'; return
}
New-Item -ItemType Directory -Path $root -Force | Out-Null
Assert-PlainTree $root
$lock = $null
$startupLock = $null
$stage = $null
try {
    # FileShare.None is cross-process and cross-checkout; a crashed deploy releases it.
    $lock = [IO.File]::Open((Join-Path $root 'deploy.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
} catch { throw 'Another deployment owns the installation lock. Retry after it finishes.' }
try {
    if (Test-Path -LiteralPath $journal) {
        throw "An interrupted deployment is recorded in $journal. Installed directories and backups have been retained. Inspect them before another deployment; see README recovery instructions."
    }
    $id = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    if (Test-Path -LiteralPath $pending) {
        # A failed copy or refused shutdown is safe to retry; retain its unused candidate.
        New-Item -ItemType Directory -Path (Join-Path $root 'retained') -Force | Out-Null
        Move-InstallDirectory $pending (Join-Path $root "retained/unused-$id")
    }
    if ($Rollback) {
        Validate-Publish $previous
    } else {
        $stage = Join-Path $repo ".codex/temp/dogfood/staging/$id"
        $rid = if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'win-arm64' } else { 'win-x64' }
        $platform = if ($rid -eq 'win-arm64') { 'arm64' } else { 'x64' }
        & dotnet publish (Join-Path $repo 'src/TajsTokens.App/TajsTokens.App.csproj') -c $Configuration -r $rid --self-contained true -o $stage '-p:DogfoodEnabled=false' "-p:Platform=$platform" '-p:WindowsAppSDKSelfContained=true'
        if ($LASTEXITCODE -ne 0) { throw "Publish failed ($LASTEXITCODE); running app and installation unchanged." }
        Validate-Publish $stage
        $manifest = @{
            schemaVersion = 1
            appName = 'TajsTokens'
            builtAtUtc = [DateTime]::UtcNow.ToString('o')
            identity = (Get-Item -LiteralPath (Join-Path $stage 'TajsTokens.App.dll')).VersionInfo.ProductVersion
            configuration = $Configuration
            runtime = $rid
            files = @(Get-ChildItem -LiteralPath $stage -Recurse -File | ForEach-Object {
                @{ path = $_.FullName.Substring($stage.Length + 1); sha256 = (Get-Sha256 $_.FullName) }
            })
        }
        $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $stage 'build-identity.json') -Encoding UTF8
        Copy-Item -LiteralPath $stage -Destination $pending -Recurse
        foreach ($file in $manifest.files) {
            if ((Get-Sha256 (Join-Path $pending $file.path)) -ne $file.sha256) { throw "Install copy hash mismatch: $($file.path)" }
        }
    }
    # Ordinary app startup takes the same exclusive lease before opening/migrating data.
    # Hold it until swaps and the deployment-owned child's readiness have completed.
    $startupLock = [IO.File]::Open((Join-Path $root 'startup.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
    $wasRunning = [bool](Get-AppProcesses)
    Stop-App
    $backup = Join-Path $root "backups/$id"
    try {
        New-Item -ItemType Directory -Path $backup -Force | Out-Null
        # Copy the owned DB (including any WAL/SHM) and settings only, never Codex sources.
        $backupSource = if (Test-Path -LiteralPath $data) { $data } else { $legacyData }
        foreach ($name in @('settings.json', 'telemetry.db', 'telemetry.db-wal', 'telemetry.db-shm')) {
            $source = Join-Path $backupSource $name
            if (Test-Path -LiteralPath $source) {
                Assert-PlainTree $source
                Copy-Item -LiteralPath $source -Destination $backup
            }
        }
    $hadCurrent = Test-Path -LiteralPath $current
    @{ schemaVersion = 1; id = $id; rollback = [bool]$Rollback; backup = $backup; hadCurrent = $hadCurrent;
       installed = $current; previous = $previous; pending = $pending; retired = (Join-Path $root "retained/$id") } |
        ConvertTo-Json | Set-Content -LiteralPath $journal -Encoding UTF8
    $retired = Join-Path $root "retained/$id"
    New-Item -ItemType Directory -Path (Split-Path $retired) -Force | Out-Null
    } catch {
        if ($wasRunning -and (Test-Path -LiteralPath $current)) { Start-App $current }
        throw
    }
    $oldMoved = $false
    $newMoved = $false
    try {
        if ($Rollback) {
            if ($hadCurrent) { Move-InstallDirectory $current $retired; $oldMoved = $true }
            Move-InstallDirectory $previous $current
        } else {
            if (Test-Path -LiteralPath $previous) { Move-InstallDirectory $previous $retired }
            if ($hadCurrent) { Move-InstallDirectory $current $previous; $oldMoved = $true }
            Move-InstallDirectory $pending $current
        }
        $newMoved = $true
        Start-App $current
        # Stable user-visible launcher; launch-at-login continues using the app's existing setting.
        Set-Launcher
        if ($Rollback -and $hadCurrent) { Move-InstallDirectory $retired $previous }
        Remove-Item -LiteralPath $journal
        Write-Host "Dogfood deployment complete. Data unchanged at $data; backup: $backup; rollback: $previous"
    } catch {
        $failure = $_
        # Never swap files while the failed app is still running. If graceful exit fails,
        # leave the journal and all directories for explicit recovery instead of force-killing.
        Stop-App
        if ($newMoved) { Move-InstallDirectory $current (Join-Path $root "retained/failed-$id") }
        if ($oldMoved) {
            $old = if ($Rollback -and (Test-Path -LiteralPath $retired)) { $retired } else { $previous }
            Move-InstallDirectory $old $current
            Start-App $current
        } elseif ($hadCurrent -and (Test-Path -LiteralPath $current)) {
            Start-App $current
        }
        if (-not $Rollback -and (Test-Path -LiteralPath $retired) -and -not (Test-Path -LiteralPath $previous)) {
            Move-InstallDirectory $retired $previous
        }
        if ($Rollback -and $newMoved -and -not (Test-Path -LiteralPath $previous)) {
            Move-InstallDirectory (Join-Path $root "retained/failed-$id") $previous
        }
        Remove-Item -LiteralPath $journal
        throw "Deployment failed; prior binaries restored where available. Data and backup preserved (no automatic DB downgrade): $failure"
    }
    # The transaction has committed and startup is verified. Cleanup must never trigger rollback.
    try { Remove-ObsoleteDeployments $backup }
    catch { Write-Warning "Deployment succeeded; obsolete recovery material could not be fully pruned: $_" -WarningAction Continue }
} finally {
    if ($startupLock) { $startupLock.Dispose() }
    $lock.Dispose()
    if ($stage -and (Test-Path -LiteralPath $stage)) {
        $stagingRoot = [IO.Path]::GetFullPath((Join-Path $repo '.codex/temp/dogfood/staging')) + [IO.Path]::DirectorySeparatorChar
        if (-not [IO.Path]::GetFullPath($stage).StartsWith($stagingRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe publish staging cleanup path' }
        try {
            Assert-PlainTree $stage
            Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction Stop
        } catch {
            Write-Warning "Disposable staging retained at ${stage}: $_" -WarningAction Continue
        }
    }
}
}
