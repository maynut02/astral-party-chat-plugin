#requires -Version 7.2
[CmdletBinding(DefaultParameterSetName = 'Build')]
param(
    [string]$Tag = '',
    [string]$OutputRoot = '',
    [string]$WorkRoot = '',
    [string]$SigningKeyPath = '',
    [string]$Repository = 'maynut02/astral-party-chat-plugin',
    [Parameter(Mandatory = $true, ParameterSetName = 'ExistingDll')]
    [ValidateNotNullOrEmpty()]
    [string]$DllPath,
    [Parameter(ParameterSetName = 'Build')]
    [string]$GameRoot = '',
    [Parameter(ParameterSetName = 'Build')]
    [string]$RefsRoot = '',
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
if ($Repository -cnotmatch '\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z') { throw 'Repository must be owner/name.' }
if (-not $OutputRoot) { $OutputRoot = Join-Path (Join-Path $repoRoot 'dist\release') $Tag }
$OutputRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputRoot)
$work = if ($WorkRoot) { $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($WorkRoot) } else { Join-Path $repoRoot '.work' }
$names = @(Get-AstralReleaseAssetNames $Tag)
$dllName = 'AstralPartyChatPlugin.dll'
Assert-AstralPackageDirectory -Root $OutputRoot -AllowedFiles (@($dllName) + $names)
# Fail before a long build when the release cannot be signed.
(Get-AstralSigningKey -Path $SigningKeyPath).Dispose()
$installerFiles = [ordered]@{
    'LICENSE-BepInEx.txt' = 'assets/third-party/LICENSE-BepInEx.txt'
    'install.bat' = 'installer/install.bat'
    'install.ps1' = 'installer/install.ps1'
}
foreach ($source in $installerFiles.Values) {
    if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $source) -PathType Leaf)) { throw "Missing install package source: $source" }
}
$bepInEx = Get-AstralBepInExArchive -WorkRoot $work

if ($buildPlugin) {
    $buildArguments = @{}
    if ($GameRoot) { $buildArguments.GameRoot = $GameRoot }
    if ($RefsRoot) { $buildArguments.RefsRoot = $RefsRoot }
    if ($WorkRoot) { $buildArguments.WorkRoot = $WorkRoot }
    if ($DotNetPath) { $buildArguments.DotNetPath = $DotNetPath }
    & (Join-Path $PSScriptRoot 'build.ps1') @buildArguments
    $dll = Join-Path $repoRoot "dist\$dllName"
}
else { $dll = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($DllPath) }
Assert-AstralPluginAssembly -Path $dll -Name 'AstralPartyChatPlugin' -Version $version

# Build and verify the whole asset set in a private directory; the output only ever holds a complete release.
$stage = Join-Path $work ('package-release/' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($stage) | Out-Null
try {
    $entry = Get-AstralPluginEntry
    New-AstralZipPackage -Files ([ordered]@{ $entry = $dll }) -OutputRoot $stage -ZipName $names[0] | Out-Null
    $installFiles = [ordered]@{ $entry = $dll }
    # BepInEx's Unity log listener breaks Astral Party startup; a fresh install starts with it disabled.
    $installFiles['BepInEx/config/BepInEx.cfg'] = [Text.Encoding]::UTF8.GetBytes("[Logging]`r`nUnityLogListening = false`r`n[Logging.Console]`r`nEnabled = false`r`n")
    foreach ($file in $installerFiles.GetEnumerator()) { $installFiles[$file.Key] = Join-Path $repoRoot $file.Value }
    New-AstralZipPackage -Files $installFiles -OutputRoot $stage -ZipName $names[1] -BaseArchive $bepInEx | Out-Null
    New-AstralUpdateManifest -Version $version -Repository $Repository -OutputRoot $stage -DllPath $dll -SigningKeyPath $SigningKeyPath
    Assert-AstralReleaseAssets -Tag $Tag -Repository $Repository -Root $stage | Out-Null
    Copy-Item -LiteralPath $dll -Destination (Join-Path $stage $dllName)
    [IO.Directory]::CreateDirectory($OutputRoot) | Out-Null
    foreach ($name in (@($dllName) + $names)) { Copy-Item -LiteralPath (Join-Path $stage $name) -Destination (Join-Path $OutputRoot $name) -Force }
} finally { Remove-Item -LiteralPath $stage -Recurse -Force }
Write-Output "Release package ready: $Tag (install ZIP, update ZIP, signed update.json, SHA256SUMS.txt)"
