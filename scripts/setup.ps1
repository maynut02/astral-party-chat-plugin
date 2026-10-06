#requires -Version 7.0
param(
    [string]$GameRoot = 'C:\Program Files (x86)\Steam\steamapps\common\Astral Party\8vJXnINT',
    [string]$WorkRoot = ''
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'reference-files.ps1')
. (Join-Path $PSScriptRoot 'dotnet-sdk.ps1')
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $WorkRoot) { $WorkRoot = Join-Path $repoRoot '.work' }
$WorkRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($WorkRoot)
Sync-AstralReferences -GameRoot $GameRoot -WorkRoot $WorkRoot
$dotnet = Install-AstralLocalSdk -Root $repoRoot -WorkRoot $WorkRoot
Write-Output "dotnet=$dotnet"
