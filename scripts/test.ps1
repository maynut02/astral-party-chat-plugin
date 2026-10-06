#requires -Version 7.0
param([string]$WorkRoot = '', [string]$DotNetPath = '')

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $WorkRoot) { $WorkRoot = Join-Path $repoRoot '.work' }
. (Join-Path $PSScriptRoot 'dotnet-sdk.ps1')
$dotnet = Get-AstralDotnet -Root $repoRoot -WorkRoot $WorkRoot -DotNetPath $DotNetPath
Push-Location -LiteralPath $repoRoot
try {
    foreach ($project in @('tests/AstralPartyChatPlugin.Tests.csproj', 'tests/overlay/OverlayTests.csproj', 'tests/security/PayloadTests.csproj')) {
        & $dotnet run --project (Join-Path $repoRoot $project) --configuration Release
        if ($LASTEXITCODE -ne 0) { throw "Runtime regression checks failed: $project" }
    }
} finally { Pop-Location }
