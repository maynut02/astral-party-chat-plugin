param(
    [string]$GameRoot = 'C:\Program Files (x86)\Steam\steamapps\common\Astral Party\8vJXnINT'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$workRoot = Join-Path $repoRoot '.work'
$dotnetRoot = Join-Path $workRoot 'dotnet'
$dotnetExe = Join-Path $dotnetRoot 'dotnet.exe'
$installer = Join-Path $workRoot 'dotnet-install.ps1'

New-Item -ItemType Directory -Force -Path $workRoot | Out-Null

& (Join-Path $PSScriptRoot 'sync-refs.ps1') -GameRoot $GameRoot

if (-not (Test-Path -LiteralPath $dotnetExe)) {
    Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installer -Version 6.0.428 -InstallDir $dotnetRoot -NoPath

    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $dotnetExe)) {
        throw 'Failed to install local .NET 6 SDK.'
    }
}

Write-Output "dotnet=$dotnetExe"
& $dotnetExe --version
