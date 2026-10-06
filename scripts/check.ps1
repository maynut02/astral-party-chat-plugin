#requires -Version 7.0
param([string]$WorkRoot = '', [string]$DotNetPath = '')

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $WorkRoot) { $WorkRoot = Join-Path $repoRoot '.work' }
. (Join-Path $PSScriptRoot 'dotnet-sdk.ps1')
$dotnet = Get-AstralDotnet -Root $repoRoot -WorkRoot $WorkRoot -DotNetPath $DotNetPath
& (Join-Path $PSScriptRoot 'check-public-files.ps1')
foreach ($suite in @('DotNetSdkTests', 'ReferenceFilesTests', 'ProjectVersionTests', 'BuildVersionTests', 'PackageReleaseTests', 'ReleasePreparationTests', 'ReleaseUploadTests')) {
    $arguments = @{}
    if ($suite -in @('DotNetSdkTests', 'BuildVersionTests', 'PackageReleaseTests')) { $arguments.DotNetPath = $dotnet }
    $global:LASTEXITCODE = 0
    & (Join-Path $repoRoot "tests/scripts/$suite.ps1") @arguments
    if ($LASTEXITCODE -ne 0) { throw "Repository checks failed: $suite" }
}
& (Join-Path $PSScriptRoot 'test.ps1') -WorkRoot $WorkRoot -DotNetPath $dotnet
Write-Output 'All repository checks passed.'
