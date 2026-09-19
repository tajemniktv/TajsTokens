# Dependency-free transaction regression tests. External publish/process/shortcut effects
# are replaced; directory swaps, hashing, backups, locks and journals are the real code.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Deployment.ps1')
$realStopApp = ${function:Stop-App}
$testBase = Join-Path $repo ('.codex/temp/dogfood-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testBase -Force | Out-Null
$script:stops = 0
$script:starts = 0
$script:stopFails = $false
$script:startFailures = 0
$script:publishFails = $false
$script:incomplete = $false
$script:fakeProcesses = @()
$script:cleanupFails = $false
$script:retentionFails = $false

function Remove-Item {
    [CmdletBinding()]
    param([string]$LiteralPath, [switch]$Recurse, [switch]$Force)
    if ($script:retentionFails -and $LiteralPath.Replace('\', '/') -like '*/backups/*') { throw 'Simulated retention failure' }
    if ($script:cleanupFails -and $LiteralPath.Replace('\', '/') -like '*dogfood/staging/*') {
        throw 'Simulated staging deletion failure'
    }
    Microsoft.PowerShell.Management\Remove-Item -LiteralPath $LiteralPath -Recurse:$Recurse -Force:$Force
}

function Get-AppProcesses { @($script:fakeProcesses) }
function Stop-App {
    $script:stops++
    if ($script:stopFails) { throw 'Simulated refusal to exit' }
}
function Start-App([string]$Directory) {
    $script:starts++
    if ($script:startFailures -gt 0) { $script:startFailures--; throw 'Simulated startup failure' }
    if (-not (Test-Path -LiteralPath (Join-Path $Directory 'TajsTokens.App.exe'))) { throw 'No app to restart' }
}
function Set-Launcher { }
function dotnet {
    $script:publishes++
    $stagePath = $args[[Array]::IndexOf($args, '-o') + 1]
    New-Fixture $stagePath 'new'
    if ($script:publishFails) { $global:LASTEXITCODE = 1; return }
    if ($script:incomplete) { Remove-Item -LiteralPath (Join-Path $stagePath 'coreclr.dll') }
    $global:LASTEXITCODE = 0
}
function New-Fixture([string]$Path, [string]$Identity) {
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    foreach ($file in @('TajsTokens.App.exe', 'TajsTokens.App.dll', 'TajsTokens.App.deps.json',
        'TajsTokens.App.runtimeconfig.json', 'TajsTokens.Core.dll', 'TajsTokens.Infrastructure.dll',
        'Microsoft.UI.Xaml.dll', 'coreclr.dll', 'e_sqlite3.dll')) {
        Set-Content -LiteralPath (Join-Path $Path $file) -Value $Identity
    }
}
function Assert([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Expect-Failure([scriptblock]$Action, [string]$Pattern) {
    try { & $Action } catch {
        if ("$_" -notlike "*$Pattern*") { throw "Unexpected failure: $_" }
        return
    }
    throw "Expected failure: $Pattern"
}
function Identity([string]$Path) { (Get-Content -LiteralPath (Join-Path $Path 'TajsTokens.App.exe') -Raw).Trim() }

$controls = @('CI', 'GITHUB_ACTIONS', 'TF_BUILD', 'BUILD_BUILDID', 'JENKINS_URL', 'TEAMCITY_VERSION', 'DogfoodEnabled', 'Dogfood')
$ambient = @{}
foreach ($name in $controls) { $ambient[$name] = [Environment]::GetEnvironmentVariable($name); [Environment]::SetEnvironmentVariable($name, $null) }
try {
    foreach ($case in @('success', 'retention-failure', 'retention-boundary', 'cleanup-success', 'cleanup-failure', 'publish-failure', 'incomplete', 'shutdown-refusal', 'startup-failure', 'rollback', 'rollback-failure', 'lock', 'interrupted', 'move-boundary', 'move-existing', 'shutdown-preflight', 'ci', 'opt-out')) {
        $repo = Join-Path $testBase "$case/repo"
        $root = [IO.Path]::GetFullPath((Join-Path $testBase "$case/install"))
        $current = Join-Path $root 'current'
        $previous = Join-Path $root 'previous'
        $pending = Join-Path $root 'pending'
        $journal = Join-Path $root 'transaction.json'
        $data = Join-Path $root 'data'
        $legacyData = Join-Path $testBase "$case/legacy"
        New-Fixture $current 'old'
        New-Fixture $previous 'older'
        New-Item -ItemType Directory -Path $data -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $data 'settings.json') -Value 'keep me'
        $script:stops = 0; $script:starts = 0
        $script:publishes = 0
        $script:stopFails = $false; $script:startFailures = 0
        $script:publishFails = $false; $script:incomplete = $false
        $script:cleanupFails = $false
        $script:retentionFails = $false
        $oldBackup = Join-Path $root 'backups/20260101T000000000Z-12345678'
        New-Item -ItemType Directory -Path $oldBackup -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $oldBackup 'settings.json') -Value 'old backup'
        switch ($case) {
            'retention-failure' {
                $script:retentionFails = $true
                $warnings = @(Invoke-DogfoodDeployment 3>&1)
                Assert ((Identity $current) -eq 'new' -and $script:starts -eq 1) 'Retention failure rolled back successful deployment'
                Assert (-not (Test-Path -LiteralPath $journal)) 'Retention failure restored journal'
                Assert ($warnings.Count -gt 0 -and (Test-Path -LiteralPath $oldBackup)) 'Retention failure not preserved/reported'
            }
            'retention-boundary' {
                Expect-Failure { Remove-ObsoleteDeployments $data } 'Invalid recovery backup'
                $unknown = Join-Path $root 'retained/user-material'
                New-Item -ItemType Directory -Path $unknown -Force | Out-Null
                Invoke-DogfoodDeployment
                Assert (Test-Path -LiteralPath $unknown) 'Unrecognized material deleted'
            }
            'cleanup-success' {
                $script:cleanupFails = $true
                $warnings = @(Invoke-DogfoodDeployment 3>&1)
                Assert ((Identity $current) -eq 'new') 'Cleanup changed deployment result'
                Assert (-not (Test-Path -LiteralPath $journal)) 'Cleanup restored a committed journal'
                Assert ($script:starts -eq 1) 'Cleanup prevented restart'
                Assert ($warnings.Count -gt 0) 'Cleanup failure was not reported'
            }
            'cleanup-failure' {
                $script:cleanupFails = $true; $script:publishFails = $true
                Expect-Failure { Invoke-DogfoodDeployment } 'Publish failed'
                Assert ((Identity $current) -eq 'old') 'Failed publish changed live installation'
            }
            'success' {
                Invoke-DogfoodDeployment
                Assert ((Identity $current) -eq 'new') 'New build not installed'
                Assert ((Identity $previous) -eq 'old') 'Rollback lost'
                Assert (-not (Test-Path -LiteralPath $journal)) 'Journal not committed'
                Assert ($script:starts -eq 1) 'Did not restart'
                Assert (@(Get-ChildItem -LiteralPath (Join-Path $root 'backups') -Recurse -Filter settings.json).Count -eq 1) 'No settings backup'
                Assert (-not (Test-Path -LiteralPath $oldBackup)) 'Old backup retained'
                Assert (@(Get-ChildItem -LiteralPath (Join-Path $root 'retained')).Count -eq 0) 'Old binaries retained'
                $manifest = Get-Content -LiteralPath (Join-Path $current 'build-identity.json') -Raw | ConvertFrom-Json
                Assert ($manifest.schemaVersion -eq 1 -and $manifest.appName -eq 'TajsTokens' -and $manifest.files.Count -eq 9) 'Build manifest contract differs'
            }
            'publish-failure' {
                $script:publishFails = $true
                Expect-Failure { Invoke-DogfoodDeployment } 'Publish failed'
                Assert ($script:stops -eq 0) 'Stopped before publish succeeded'
            }
            'incomplete' {
                $script:incomplete = $true
                Expect-Failure { Invoke-DogfoodDeployment } 'Incomplete publish'
                Assert ($script:stops -eq 0) 'Stopped for incomplete output'
            }
            'shutdown-refusal' {
                $script:stopFails = $true
                Expect-Failure { Invoke-DogfoodDeployment } 'refusal to exit'
                Assert ((Identity $current) -eq 'old') 'Changed live binaries on refused shutdown'
                $script:stopFails = $false
                Invoke-DogfoodDeployment
                Assert ((Identity $current) -eq 'new') 'Refused shutdown was not retryable'
            }
            'startup-failure' {
                $script:startFailures = 1
                Expect-Failure { Invoke-DogfoodDeployment } 'prior binaries restored'
                Assert ((Identity $current) -eq 'old') 'Failed to restore prior build'
                Assert ((Identity $previous) -eq 'older') 'Lost earlier rollback after startup failure'
                Assert ($script:starts -eq 2) 'Prior build not restarted'
                Assert (Test-Path -LiteralPath $oldBackup) 'Failed deployment pruned recovery history'
            }
            'rollback' {
                Invoke-DogfoodDeployment -Rollback
                Assert ((Identity $current) -eq 'older') 'Rollback did not promote previous'
                Assert ((Identity $previous) -eq 'old') 'Rollback did not retain replaced build'
                Assert (@(Get-ChildItem -LiteralPath (Join-Path $root 'backups') -Directory).Count -eq 1) 'Rollback retained extra backups'
            }
            'rollback-failure' {
                $script:startFailures = 1
                Expect-Failure { Invoke-DogfoodDeployment -Rollback } 'prior binaries restored'
                Assert ((Identity $current) -eq 'old') 'Failed rollback did not restore current'
                Assert ((Identity $previous) -eq 'older') 'Failed rollback lost retry target'
                Invoke-DogfoodDeployment -Rollback
                Assert ((Identity $current) -eq 'older') 'Failed rollback was not retryable'
            }
            'lock' {
                $held = [IO.File]::Open((Join-Path $root 'deploy.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
                try { Expect-Failure { Invoke-DogfoodDeployment } 'installation lock' }
                finally { $held.Dispose() }
                Assert ($script:stops -eq 0) 'Concurrent deploy stopped app'
            }
            'interrupted' {
                Set-Content -LiteralPath $journal -Value '{}'
                Expect-Failure { Invoke-DogfoodDeployment } 'interrupted deployment'
                Assert ($script:stops -eq 0) 'Interrupted state was modified'
            }
            'move-boundary' {
                Expect-Failure { Move-InstallDirectory $current (Join-Path $testBase 'outside') } 'Path outside installation'
            }
            'move-existing' {
                Expect-Failure { Move-InstallDirectory $current $previous } 'destination already exists'
                Assert ((Identity $current) -eq 'old' -and (Identity $previous) -eq 'older') 'Existing destination was mutated'
            }
            'shutdown-preflight' {
                $session = (Get-Process -Id $PID).SessionId
                $script:fakeProcesses = @(
                    [pscustomobject]@{ Id = 123456; Path = (Join-Path $current 'TajsTokens.App.exe'); SessionId = $session },
                    [pscustomobject]@{ Id = 123457; Path = 'C:\unmanaged\TajsTokens.App.exe'; SessionId = $session }
                )
                try { Expect-Failure { & $realStopApp } 'Unmanaged TajsTokens process 123457' }
                finally { $script:fakeProcesses = @() }
            }
            'ci' {
                $oldCI = $env:CI
                try { $env:CI = '1'; Invoke-DogfoodDeployment }
                finally { $env:CI = $oldCI }
                Assert ($script:stops -eq 0) 'CI touched running app'
                Assert ($script:publishes -eq 0) 'CI published before skipping'
            }
            'opt-out' {
                $oldOptOut = $env:DogfoodEnabled
                try { $env:DogfoodEnabled = 'false'; Invoke-DogfoodDeployment }
                finally { $env:DogfoodEnabled = $oldOptOut }
                Assert ($script:stops -eq 0) 'Opt-out touched the running app'
                Assert ($script:publishes -eq 0) 'Opt-out published before skipping'
            }
        }
        Assert ((Get-Content -LiteralPath (Join-Path $data 'settings.json') -Raw).Trim() -eq 'keep me') 'Settings changed'
        Write-Host "PASS $case"
    }
} finally {
    foreach ($name in $controls) { [Environment]::SetEnvironmentVariable($name, $ambient[$name]) }
    $expected = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../.codex/temp')) + [IO.Path]::DirectorySeparatorChar
    if (-not [IO.Path]::GetFullPath($testBase).StartsWith($expected, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test cleanup path' }
    Remove-Item -LiteralPath $testBase -Recurse -Force
}
