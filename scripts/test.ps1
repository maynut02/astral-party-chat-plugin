param()

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$localDotnet = Join-Path $repoRoot '.work\dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1 -ExpandProperty Source }
& $dotnet run --project (Join-Path $repoRoot 'tests\AstralParty.Chat.Tests.csproj') --configuration Release
if ($LASTEXITCODE -ne 0) { throw 'Astral Party Chat regression checks failed.' }
& $dotnet run --project (Join-Path $repoRoot 'tests\overlay\OverlayTests.csproj') --configuration Release
if ($LASTEXITCODE -ne 0) { throw 'Astral Party Chat overlay regression checks failed.' }
& $dotnet run --project (Join-Path $repoRoot 'tests\security\PayloadTests.csproj') --configuration Release
if ($LASTEXITCODE -ne 0) { throw 'Astral Party Chat remote payload checks failed.' }
