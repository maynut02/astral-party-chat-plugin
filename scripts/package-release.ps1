#requires -Version 7.0
[CmdletBinding(DefaultParameterSetName = 'Build')]
param(
    [string]$Tag = '',
    [string]$OutputRoot = '',
    [Parameter(Mandatory = $true, ParameterSetName = 'ExistingDll')]
    [ValidateNotNullOrEmpty()]
    [string]$DllPath,
    [Parameter(ParameterSetName = 'Build')]
    [string]$GameRoot = '',
    [Parameter(ParameterSetName = 'Build')]
    [string]$RefsRoot = '',
    [Parameter(ParameterSetName = 'Build')]
    [string]$WorkRoot = '',
    [Parameter(ParameterSetName = 'Build')]
    [string]$DotNetPath = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'project-version.ps1')
. (Join-Path $PSScriptRoot 'package-files.ps1')
if (-not $Tag) { $Tag = 'v' + (Get-AstralProjectVersion -Root $repoRoot) }
$version = ConvertTo-AstralVersion $Tag -Tag
$buildPlugin = $PSCmdlet.ParameterSetName -eq 'Build'
if ($buildPlugin -and $version -cne (Get-AstralProjectVersion -Root $repoRoot)) {
    throw 'Package tag must match VERSION when building. Prepare the VERSION file before packaging.'
}
if (-not $OutputRoot) { $OutputRoot = Join-Path (Join-Path $repoRoot 'dist\release') $Tag }
$OutputRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputRoot)
$zipName = "AstralPartyChatPlugin-$Tag.zip"
$allowed = @('AstralPartyChatPlugin.dll', $zipName, 'SHA256SUMS.txt')
Assert-AstralPackageDirectory -Root $OutputRoot -AllowedFiles $allowed

if ($buildPlugin) {
    $buildArguments = @{}
    if ($GameRoot) { $buildArguments.GameRoot = $GameRoot }
    if ($RefsRoot) { $buildArguments.RefsRoot = $RefsRoot }
    if ($WorkRoot) { $buildArguments.WorkRoot = $WorkRoot }
    if ($DotNetPath) { $buildArguments.DotNetPath = $DotNetPath }
    & (Join-Path $PSScriptRoot 'build.ps1') @buildArguments
    $dll = Join-Path $repoRoot 'dist\AstralPartyChatPlugin.dll'
}
else { $dll = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($DllPath) }
Assert-AstralPluginAssembly -Path $dll -Name 'AstralPartyChatPlugin' -Version $version
$files = [ordered]@{ 'BepInEx/plugins/AstralPartyChatPlugin/AstralPartyChatPlugin.dll' = $dll }
New-AstralPluginPackage -Files $files -OutputRoot $OutputRoot -ZipName $zipName | Out-Null
Write-Output "Release package ready: $Tag (plugin DLL, ZIP, SHA256SUMS.txt)"
