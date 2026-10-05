param(
    [string]$GameRoot = 'C:\Program Files (x86)\Steam\steamapps\common\Astral Party\8vJXnINT'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sdkVersion = ([IO.File]::ReadAllText((Join-Path $repoRoot 'global.json')) | ConvertFrom-Json).sdk.version
if ($sdkVersion -notmatch '\A[0-9]+\.[0-9]+\.[0-9]+\z') { throw 'global.json must specify a complete SDK version.' }
$workRoot = Join-Path $repoRoot '.work'
$dotnetRoot = Join-Path $workRoot 'dotnet'
$dotnetExe = Join-Path $dotnetRoot 'dotnet.exe'
$installer = Join-Path $workRoot 'dotnet-install.ps1'

New-Item -ItemType Directory -Force -Path $workRoot | Out-Null

& (Join-Path $PSScriptRoot 'sync-refs.ps1') -GameRoot $GameRoot

$hasSdk = $false
if (Test-Path -LiteralPath $dotnetExe) {
    $sdks = @(& $dotnetExe --list-sdks)
    if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect the local .NET SDK installation.' }
    $hasSdk = @($sdks | Where-Object { $_.StartsWith("$sdkVersion ") }).Count -gt 0
}
if (-not $hasSdk) {
    Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installer -Version $sdkVersion -InstallDir $dotnetRoot -NoPath

    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $dotnetExe)) {
        throw 'Failed to install the local .NET SDK.'
    }
}

Write-Output "dotnet=$dotnetExe"
& $dotnetExe --version
if ($LASTEXITCODE -ne 0) { throw 'The local .NET SDK does not satisfy global.json.' }
