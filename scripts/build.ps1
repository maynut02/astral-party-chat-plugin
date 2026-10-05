param(
    [string]$GameRoot = 'C:\Program Files (x86)\Steam\steamapps\common\Astral Party\8vJXnINT',
    [string]$RefsRoot = '',
    [string]$Version = '',
    [string]$SourceRoot = '',
    [string]$BuildRoot = '',
    [string]$OutputRoot = '',
    [string]$DotnetPath = '',
    [switch]$Deploy
)

$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $SourceRoot) { $SourceRoot = $repoRoot }
$SourceRoot = [IO.Path]::GetFullPath($SourceRoot)
$project = Join-Path $SourceRoot 'src\AstralParty.Chat.csproj'
. (Join-Path $PSScriptRoot 'project-version.ps1')
$projectVersion = Get-AstralProjectVersion -Root $SourceRoot
if ($Version -and $Version -cne $projectVersion) {
    throw 'Build version must match the VERSION file. Run scripts/prepare-release.ps1 first.'
}
$Version = $projectVersion
if (-not $OutputRoot) { $OutputRoot = Join-Path $repoRoot 'dist' }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$localRefs = Join-Path $repoRoot '.work\refs'
$localDotnet = Join-Path $repoRoot '.work\dotnet\dotnet.exe'

if ($DotnetPath) {
    $dotnet = [IO.Path]::GetFullPath($DotnetPath)
    if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) { throw 'Missing requested .NET SDK executable.' }
}
elseif (Test-Path -LiteralPath $localDotnet) {
    $dotnet = $localDotnet
}
else {
    $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnetCommand) {
        throw 'No .NET SDK found. Run scripts\setup.ps1 first.'
    }
    $dotnet = $dotnetCommand.Source
}

if (-not $RefsRoot -and (Test-Path -LiteralPath (Join-Path $localRefs 'core\BepInEx.Core.dll'))) {
    $RefsRoot = $localRefs
}

$buildArgs = @(
    'build',
    $project,
    '--configuration', 'Release',
    '--nologo',
    "-p:AstralGameRoot=$GameRoot",
    "-p:Version=$Version"
)

if ($RefsRoot) {
    $buildArgs += "-p:AstralRefsRoot=$RefsRoot"
}

if ($BuildRoot) {
    $BuildRoot = [IO.Path]::GetFullPath($BuildRoot)
    $binRoot = Join-Path $BuildRoot 'bin'
    $objRoot = Join-Path $BuildRoot 'obj'
    $buildArgs += "-p:BaseOutputPath=$binRoot$([IO.Path]::DirectorySeparatorChar)"
    $buildArgs += "-p:BaseIntermediateOutputPath=$objRoot$([IO.Path]::DirectorySeparatorChar)"
}

& $dotnet @buildArgs
if ($LASTEXITCODE -ne 0) {
    throw 'Astral Party Chat build failed.'
}

$dll = if ($BuildRoot) { Join-Path $BuildRoot 'bin\Release\net6.0\AstralParty.Chat.dll' } else { Join-Path $SourceRoot 'src\bin\Release\net6.0\AstralParty.Chat.dll' }
if (-not (Test-Path -LiteralPath $dll)) {
    throw "Missing build output: $dll"
}
if ([Diagnostics.FileVersionInfo]::GetVersionInfo($dll).ProductVersion -cne $Version) {
    throw 'Built DLL version does not match the requested release version.'
}

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
Copy-Item -LiteralPath $dll -Destination (Join-Path $OutputRoot 'AstralParty.Chat.dll') -Force

if ($Deploy) {
    if ($OutputRoot -cne (Join-Path $repoRoot 'dist')) { throw 'Deploy requires the default dist output directory.' }
    & (Join-Path $PSScriptRoot 'install.ps1') -GameRoot $GameRoot
}

Write-Output "dll=$(Join-Path $OutputRoot 'AstralParty.Chat.dll')"
Write-Output "version=$Version"
