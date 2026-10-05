param()

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$localDotnet = Join-Path $repoRoot '.work\dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1 -ExpandProperty Source }
& $dotnet run --project (Join-Path $repoRoot 'tests\AstralPartyChatPlugin.Tests.csproj') --configuration Release
if ($LASTEXITCODE -ne 0) { throw 'AstralPartyChatPlugin regression checks failed.' }
& $dotnet run --project (Join-Path $repoRoot 'tests\overlay\OverlayTests.csproj') --configuration Release
if ($LASTEXITCODE -ne 0) { throw 'AstralPartyChatPlugin overlay regression checks failed.' }
& $dotnet run --project (Join-Path $repoRoot 'tests\security\PayloadTests.csproj') --configuration Release
if ($LASTEXITCODE -ne 0) { throw 'AstralPartyChatPlugin remote payload checks failed.' }
