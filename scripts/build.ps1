param(
    [string]$GameRoot = 'C:\Program Files (x86)\Steam\steamapps\common\Astral Party\8vJXnINT',
    [string]$RefsRoot = ''
)

$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repoRoot 'src\AstralParty.Chat.csproj'
. (Join-Path $PSScriptRoot 'project-version.ps1')
$Version = Get-AstralProjectVersion -Root $repoRoot
$OutputRoot = Join-Path $repoRoot 'dist'
$localRefs = Join-Path $repoRoot '.work\refs'
$localDotnet = Join-Path $repoRoot '.work\dotnet\dotnet.exe'

if (Test-Path -LiteralPath $localDotnet) {
    $dotnet = $localDotnet
}
else {
    $dotnetCommand = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
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

& $dotnet @buildArgs
if ($LASTEXITCODE -ne 0) {
    throw 'Astral Party Chat build failed.'
}

$dll = Join-Path $repoRoot 'src\bin\Release\net6.0\AstralParty.Chat.dll'
if (-not (Test-Path -LiteralPath $dll)) {
    throw "Missing build output: $dll"
}
if ([Diagnostics.FileVersionInfo]::GetVersionInfo($dll).ProductVersion -cne $Version) {
    throw 'Built DLL version does not match the requested release version.'
}

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
Copy-Item -LiteralPath $dll -Destination (Join-Path $OutputRoot 'AstralParty.Chat.dll') -Force

Write-Output "dll=$(Join-Path $OutputRoot 'AstralParty.Chat.dll')"
Write-Output "version=$Version"
