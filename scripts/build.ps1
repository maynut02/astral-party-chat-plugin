param(
    [string]$GameRoot = 'C:\Program Files (x86)\Steam\steamapps\common\Astral Party\8vJXnINT',
    [string]$RefsRoot = '',
    [string]$Version = '',
    [switch]$Deploy
)

$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repoRoot 'src\AstralParty.Chat.csproj'
$outputRoot = Join-Path $repoRoot 'dist'
$localRefs = Join-Path $repoRoot '.work\refs'
$localDotnet = Join-Path $repoRoot '.work\dotnet\dotnet.exe'

if (Test-Path -LiteralPath $localDotnet) {
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
    "-p:AstralGameRoot=$GameRoot"
)

if ($RefsRoot) {
    $buildArgs += "-p:AstralRefsRoot=$RefsRoot"
}

if ($Version) {
    # Validate before writing C# or passing properties to MSBuild.
    $resolved = & (Join-Path $PSScriptRoot 'release-version.ps1') -InitialVersion $Version -ExplicitTag "v$Version"
    $versionSource = Join-Path $repoRoot '.work\generated\AstralBuildVersion.g.cs'
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $versionSource) | Out-Null
    $content = "internal static class AstralBuildVersion { public const string Value = `"$($resolved.Version)`"; }"
    [IO.File]::WriteAllText($versionSource, $content, [Text.UTF8Encoding]::new($false))
    $buildArgs += "-p:AstralBuildVersionSource=$versionSource"
}

& $dotnet @buildArgs
if ($LASTEXITCODE -ne 0) {
    throw 'Astral Party Chat build failed.'
}

$dll = Join-Path $repoRoot 'src\bin\Release\net6.0\AstralParty.Chat.dll'
if (-not (Test-Path -LiteralPath $dll)) {
    throw "Missing build output: $dll"
}
if ($Version -and [Diagnostics.FileVersionInfo]::GetVersionInfo($dll).ProductVersion -cne $Version) {
    throw 'Built DLL version does not match the requested release version.'
}

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
Copy-Item -LiteralPath $dll -Destination (Join-Path $outputRoot 'AstralParty.Chat.dll') -Force

if ($Deploy) {
    & (Join-Path $PSScriptRoot 'install.ps1') -GameRoot $GameRoot
}

Write-Output "dll=$(Join-Path $outputRoot 'AstralParty.Chat.dll')"
