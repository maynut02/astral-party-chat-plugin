#requires -Version 7.0
param(
    [string]$Version = '',
    [string]$GameRoot = 'C:\Program Files (x86)\Steam\steamapps\common\Astral Party\8vJXnINT',
    [string]$RefsRoot = '',
    [string]$WorkRoot = '',
    [string]$OutputRoot = '',
    [string]$DotNetPath = ''
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$pluginRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'project-version.ps1')
$currentVersion = Get-AstralProjectVersion -Root $pluginRoot
if ($Version -and (ConvertTo-AstralVersion $Version) -cne $currentVersion) { throw 'Build Version must match the VERSION file. Run scripts/prepare-release.ps1 first.' }
$Version = $currentVersion
if (-not $WorkRoot) { $WorkRoot = Join-Path $pluginRoot '.work' }
if (-not $OutputRoot) { $OutputRoot = Join-Path $pluginRoot 'dist' }
$work = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($WorkRoot)
$dist = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputRoot)
. (Join-Path $PSScriptRoot 'dotnet-sdk.ps1')
$DotNetPath = Get-AstralDotnet -Root $pluginRoot -WorkRoot $work -DotNetPath $DotNetPath

. (Join-Path $PSScriptRoot 'reference-files.ps1')
if (-not $RefsRoot) {
    $RefsRoot = Join-Path $work 'refs'
    if ($PSBoundParameters.ContainsKey('GameRoot') -or -not (Test-Path -LiteralPath (Join-Path $RefsRoot 'core/BepInEx.Core.dll'))) {
        Sync-AstralReferences -GameRoot $GameRoot -WorkRoot $work
    }
}
$RefsRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($RefsRoot)
Assert-AstralReferences $RefsRoot
$projects = @(
    'src/AstralPartyChatPlugin.csproj'
)
# Resolve global.json from this project even when invoked from another working directory.
Push-Location $pluginRoot
try {
    foreach ($project in $projects) {
        & $DotNetPath build (Join-Path $pluginRoot $project) --configuration Release --nologo `
            "-p:AstralRefsRoot=$RefsRoot" `
            "-p:Version=$Version" -p:ContinuousIntegrationBuild=true
        if ($LASTEXITCODE -ne 0) { throw "dotnet build failed: $project" }
    }
}
finally { Pop-Location }

$outputs = foreach ($project in $projects) {
    $projectPath = Join-Path $pluginRoot $project
    $assembly = [IO.Path]::GetFileNameWithoutExtension($projectPath)
    $dll = Join-Path (Split-Path -Parent $projectPath) "bin/Release/net6.0/$assembly.dll"
    if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) { throw "Build output is missing: $dll" }
    if ([Reflection.AssemblyName]::GetAssemblyName($dll).Name -cne $assembly -or [Diagnostics.FileVersionInfo]::GetVersionInfo($dll).ProductVersion -cne $Version) {
        throw "Built DLL identity or version does not match VERSION: $dll"
    }
    $dll
}
New-Item -ItemType Directory -Path $dist -Force | Out-Null
foreach ($dll in $outputs) { Copy-Item -LiteralPath $dll -Destination (Join-Path $dist ([IO.Path]::GetFileName($dll))) -Force }
[ordered]@{ references = @(Get-AstralReferenceVersions $RefsRoot) } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $dist "build-references.json") -Encoding utf8NoBOM
Write-Output "output=$dist"
Write-Output "version=$Version"
