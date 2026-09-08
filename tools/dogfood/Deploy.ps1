[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$Rollback
)
. (Join-Path $PSScriptRoot 'Deployment.ps1')
Invoke-DogfoodDeployment -Configuration $Configuration -Rollback:$Rollback
