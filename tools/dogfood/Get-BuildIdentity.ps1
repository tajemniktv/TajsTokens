$ErrorActionPreference = 'Stop'
try {
    $repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
    $commit = & git -C $repo rev-parse HEAD 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'No Git commit' }
    $changes = & git -C $repo status --porcelain --untracked-files=normal 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'Git status unavailable' }
    $state = if ($changes) { 'dirty' } else { 'clean' }
    "$commit.$state"
} catch {
    'git-unknown'
}
