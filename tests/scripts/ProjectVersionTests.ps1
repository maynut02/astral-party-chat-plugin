param()

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $repoRoot 'scripts/project-version.ps1')
$passed = 0

foreach ($value in @('0.0.0', '0.0.1', '1.2.3', '65534.65534.65534')) {
    if ((ConvertTo-AstralVersion $value) -cne $value -or (ConvertTo-AstralVersion "v$value" -Tag) -cne $value) {
        throw "Valid version was not preserved: $value"
    }
    $passed++
}
foreach ($value in @('', '01.0.1', '1.00.0', '1.0.01', '1.0', '1.0.0.0', '1.0.0-rc.1',
        '1.0.0+build.1', 'v1.0.0', '-1.0.0', ' 1.0.0', "1.0.0`n", '65535.0.0', '1.65535.0', '1.0.65535', '99999999999999999.0.0')) {
    $failed = $false
    try { ConvertTo-AstralVersion $value | Out-Null } catch { $failed = $true }
    if (-not $failed) { throw 'Invalid version was accepted.' }
    $passed++
}
foreach ($tag in @('1.0.0', 'V1.0.0', 'v01.0.0', 'v1.0.0-rc.1', 'v65535.0.0')) {
    $failed = $false
    try { ConvertTo-AstralVersion $tag -Tag | Out-Null } catch { $failed = $true }
    if (-not $failed) { throw 'Invalid tag was accepted.' }
    $passed++
}
foreach ($case in @(
        @{ Version = '0.0.1'; Bump = 'patch'; Expected = '0.0.2' },
        @{ Version = '1.2.3'; Bump = 'minor'; Expected = '1.3.0' },
        @{ Version = '1.2.3'; Bump = 'major'; Expected = '2.0.0' },
        @{ Version = '1.65534.65534'; Bump = 'major'; Expected = '2.0.0' },
        @{ Version = '1.2.65534'; Bump = 'minor'; Expected = '1.3.0' },
        @{ Version = '1.2.65533'; Bump = 'patch'; Expected = '1.2.65534' })) {
    if ((Get-AstralNextVersion -Version $case.Version -Bump $case.Bump) -cne $case.Expected) {
        throw 'Version increment did not preserve/reset the correct components.'
    }
    $passed++
}
foreach ($bump in @('patch', 'minor', 'major', 'miner')) {
    $failed = $false
    try { Get-AstralNextVersion -Version '65534.65534.65534' -Bump $bump | Out-Null } catch { $failed = $true }
    if (-not $failed) { throw 'An overflow or invalid increment type was accepted.' }
    $passed++
}
$testRoot = Join-Path $repoRoot ('.work/project-version-tests/' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$failed = $false
try { Get-AstralProjectVersion -Root $testRoot | Out-Null } catch { $failed = $true }
if (-not $failed) { throw 'Missing VERSION file was accepted.' }
$passed++
[IO.File]::WriteAllText((Join-Path $testRoot 'VERSION'), "0.0.1`r`n", [Text.UTF8Encoding]::new($false))
if ((Get-AstralProjectVersion -Root $testRoot) -cne '0.0.1') { throw 'VERSION file was not read correctly.' }
$passed++
Write-Output "Project version tests passed: $passed cases."
