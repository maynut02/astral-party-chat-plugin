param()

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$resolver = Join-Path $repoRoot 'scripts\release-version.ps1'
$script:passed = 0

function Assert-Resolution {
    param(
        [string]$Name,
        [hashtable]$Arguments,
        [string]$Version,
        [string]$PreviousTag = '',
        [bool]$IsFirstRelease = $false
    )

    $outputs = @(& $resolver @Arguments)
    if ($outputs.Count -ne 1 -or $outputs[0] -isnot [pscustomobject]) {
        throw "${Name}: resolver must return exactly one PSCustomObject."
    }
    $result = $outputs[0]
    if (($result.PSObject.Properties.Name -join ',') -cne 'Version,Tag,PreviousTag,IsFirstRelease') {
        throw "${Name}: unexpected result properties."
    }
    if ($result.Version -isnot [string] -or $result.Tag -isnot [string] -or
        $result.PreviousTag -isnot [string] -or $result.IsFirstRelease -isnot [bool]) {
        throw "${Name}: unexpected result types."
    }
    if ($result.Version -cne $Version -or $result.Tag -cne "v$Version" -or
        $result.PreviousTag -cne $PreviousTag -or $result.IsFirstRelease -ne $IsFirstRelease) {
        throw "${Name}: unexpected resolution: $($result | ConvertTo-Json -Compress)"
    }
    $script:passed++
    Write-Output "PASS release version: $Name"
}

function Assert-Rejected {
    param([string]$Name, [hashtable]$Arguments, [string]$Message)

    $rejected = $false
    try { & $resolver @Arguments | Out-Null }
    catch {
        if ($_.Exception.Message -notlike "*$Message*") { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw "${Name}: resolver should have rejected this input."
    }
    $script:passed++
    Write-Output "PASS release version: $Name"
}

foreach ($bump in @('patch', 'minor', 'major')) {
    Assert-Resolution -Name "first-release-$bump" -Arguments @{
        InitialVersion = '1.0.7'; Bump = $bump
    } -Version '1.0.7' -IsFirstRelease $true
}
Assert-Resolution -Name 'default-patch' -Arguments @{
    InitialVersion = '1.0.7'; Tags = @('v1.0.7')
} -Version '1.0.8' -PreviousTag 'v1.0.7'
Assert-Resolution -Name 'minor' -Arguments @{
    InitialVersion = '1.0.7'; Bump = 'minor'; Tags = @('v1.0.7')
} -Version '1.1.0' -PreviousTag 'v1.0.7'
Assert-Resolution -Name 'major' -Arguments @{
    InitialVersion = '1.0.7'; Bump = 'major'; Tags = @('v1.0.7')
} -Version '2.0.0' -PreviousTag 'v1.0.7'
Assert-Resolution -Name 'numeric-patch-order' -Arguments @{
    InitialVersion = '1.0.7'; Tags = @('v1.0.9', 'v1.0.10', 'v1.0.2')
} -Version '1.0.11' -PreviousTag 'v1.0.10'
Assert-Resolution -Name 'numeric-component-order' -Arguments @{
    InitialVersion = '1.0.7'; Tags = @('v2.0.0', 'v1.10.0', 'v1.9.99', 'v10.0.0')
} -Version '10.0.1' -PreviousTag 'v10.0.0'
Assert-Resolution -Name 'initial-version-is-only-bootstrap' -Arguments @{
    InitialVersion = '99.0.0'; Tags = @('v1.0.7')
} -Version '1.0.8' -PreviousTag 'v1.0.7'
Assert-Resolution -Name 'lower-components-reset-on-minor' -Arguments @{
    InitialVersion = '1.0.7'; Bump = 'minor'; Tags = @('v1.12.345')
} -Version '1.13.0' -PreviousTag 'v1.12.345'
Assert-Resolution -Name 'lower-components-reset-on-major' -Arguments @{
    InitialVersion = '1.0.7'; Bump = 'major'; Tags = @('v7.12.345')
} -Version '8.0.0' -PreviousTag 'v7.12.345'

$ignoredTags = @(
    'release-9', '1.0.9', 'V9.0.0', 'v2.0.0-rc.1', 'v2.0.0+build.1',
    'v01.0.0', 'v1.00.0', 'v1.0.00', 'v1.2', 'v1.2.3.4',
    'v2147483648.0.0', 'v1.2147483648.0', 'v1.0.2147483648',
    'v-1.0.0', 'v+1.0.0', ' v9.0.0', 'v9.0.0 ', "v9.0.0`n", '', $null
)
Assert-Resolution -Name 'ignore-noncanonical-tags' -Arguments @{
    InitialVersion = '1.0.7'; Tags = @('v1.0.7') + $ignoredTags
} -Version '1.0.8' -PreviousTag 'v1.0.7'
Assert-Resolution -Name 'only-unrelated-tags-bootstrap' -Arguments @{
    InitialVersion = '1.0.7'; Bump = 'major'; Tags = $ignoredTags
} -Version '1.0.7' -IsFirstRelease $true
Assert-Resolution -Name 'zero-version-is-canonical' -Arguments @{
    InitialVersion = '0.0.0'; Tags = @('v0.0.0')
} -Version '0.0.1' -PreviousTag 'v0.0.0'
Assert-Resolution -Name 'explicit-existing-tag-skips-bump' -Arguments @{
    InitialVersion = '1.0.7'; Bump = 'major'; ExplicitTag = 'v1.0.10'
    Tags = @('v1.0.10', 'v1.0.9', 'v1.0.8', 'v2.0.0')
} -Version '1.0.10' -PreviousTag 'v1.0.9'
Assert-Resolution -Name 'explicit-new-tag-selects-numeric-predecessor' -Arguments @{
    InitialVersion = '1.0.7'; ExplicitTag = 'v1.0.11'
    Tags = @('v1.0.9', 'v1.0.10', 'v9.0.0')
} -Version '1.0.11' -PreviousTag 'v1.0.10'
Assert-Resolution -Name 'explicit-first-existing-tag' -Arguments @{
    InitialVersion = '1.0.7'; ExplicitTag = 'v1.0.7'; Tags = @('v1.0.7', 'v1.0.8')
} -Version '1.0.7' -IsFirstRelease $true
Assert-Resolution -Name 'explicit-tag-without-remote-tags' -Arguments @{
    InitialVersion = '1.0.7'; Bump = 'minor'; ExplicitTag = 'v3.4.5'
} -Version '3.4.5' -IsFirstRelease $true
Assert-Resolution -Name 'explicit-maximum-components' -Arguments @{
    InitialVersion = '1.0.7'; ExplicitTag = 'v2147483647.2147483647.2147483647'
    Tags = @('v2147483647.2147483647.2147483646')
} -Version '2147483647.2147483647.2147483647' -PreviousTag 'v2147483647.2147483647.2147483646'
Assert-Resolution -Name 'increment-reaches-maximum' -Arguments @{
    InitialVersion = '1.0.7'; Tags = @('v1.0.2147483646')
} -Version '1.0.2147483647' -PreviousTag 'v1.0.2147483646'
Assert-Resolution -Name 'major-reset-allows-maximum-lower-components' -Arguments @{
    InitialVersion = '1.0.7'; Bump = 'major'; Tags = @('v1.2147483647.2147483647')
} -Version '2.0.0' -PreviousTag 'v1.2147483647.2147483647'
Assert-Resolution -Name 'minor-reset-allows-maximum-patch' -Arguments @{
    InitialVersion = '1.0.7'; Bump = 'minor'; Tags = @('v1.0.2147483647')
} -Version '1.1.0' -PreviousTag 'v1.0.2147483647'

foreach ($bump in @('patch', 'minor', 'major')) {
    Assert-Rejected -Name "reject-$bump-overflow" -Arguments @{
        InitialVersion = '1.0.7'; Bump = $bump
        Tags = @('v2147483647.2147483647.2147483647')
    } -Message 'increment exceeds Int32.MaxValue'
}

$invalidVersions = @(
    '01.0.7', '1.00.7', '1.0.07', 'v1.0.7', '1.0', '1.0.7.0',
    '1.0.7-rc.1', '1.0.7+build.1', '-1.0.7', '+1.0.7',
    '2147483648.0.0', '1.2147483648.0', '1.0.2147483648',
    ' 1.0.7', '1.0.7 ', "1.0.7`n"
)
for ($index = 0; $index -lt $invalidVersions.Count; $index++) {
    Assert-Rejected -Name "reject-initial-version-$index" -Arguments @{
        InitialVersion = $invalidVersions[$index]; Tags = @('v1.0.7')
    } -Message 'InitialVersion must be canonical'
}
Assert-Rejected -Name 'reject-invalid-initial-even-with-explicit-tag' -Arguments @{
    InitialVersion = '01.0.7'; ExplicitTag = 'v1.0.8'; Tags = @('v1.0.7')
} -Message 'InitialVersion must be canonical'
foreach ($tag in @('1.0.7', 'V1.0.7', 'v01.0.7', 'v1.0.7-rc.1',
        'v2147483648.0.0', ' v1.0.7', "v1.0.7`n")) {
    Assert-Rejected -Name "reject-explicit-tag-$($script:passed)" -Arguments @{
        InitialVersion = '1.0.7'; ExplicitTag = $tag
    } -Message 'ExplicitTag must be canonical'
}
Assert-Rejected -Name 'reject-unsupported-bump' -Arguments @{
    InitialVersion = '1.0.7'; Bump = 'miner'
} -Message 'ValidateSet'

Assert-Rejected -Name 'manual-first-release-rerun-cannot-resolve-new-version' -Arguments @{
    InitialVersion = '1.0.7'; RunAttempt = 2
} -Message 'Manual release version resolution cannot be rerun'
Assert-Rejected -Name 'manual-rerun-after-tag-created-cannot-bump-again' -Arguments @{
    InitialVersion = '1.0.7'; Tags = @('v1.0.7'); RunAttempt = 2
} -Message 'Manual release version resolution cannot be rerun'
Assert-Resolution -Name 'explicit-tag-rerun-keeps-version' -Arguments @{
    InitialVersion = '1.0.7'; ExplicitTag = 'v1.0.8'; Tags = @('v1.0.7', 'v1.0.8'); RunAttempt = 2
} -Version '1.0.8' -PreviousTag 'v1.0.7'
Assert-Rejected -Name 'invalid-zero-run-attempt' -Arguments @{
    InitialVersion = '1.0.7'; RunAttempt = 0
} -Message 'RunAttempt'

Write-Output "Release version tests passed: $($script:passed) cases."
