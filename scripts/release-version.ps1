param(
    [Parameter(Mandatory = $true)][string]$InitialVersion,
    [ValidateSet('patch', 'minor', 'major')][string]$Bump = 'patch',
    [string[]]$Tags = @(),
    [string]$ExplicitTag = '',
    [ValidateRange(1, 2147483647)][int]$RunAttempt = 1
)

$ErrorActionPreference = 'Stop'

function ConvertTo-CanonicalReleaseVersion {
    param([string]$Value, [switch]$HasTagPrefix)

    $prefix = if ($HasTagPrefix) { 'v' } else { '' }
    $pattern = '\A' + $prefix + '(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z'
    $match = [regex]::Match($Value, $pattern)
    if (-not $match.Success) { return $null }

    $parts = [int[]]@(0, 0, 0)
    for ($index = 0; $index -lt 3; $index++) {
        $parsed = 0
        if (-not [int]::TryParse($match.Groups[$index + 1].Value,
                [Globalization.NumberStyles]::None,
                [Globalization.CultureInfo]::InvariantCulture, [ref]$parsed)) {
            return $null
        }
        $parts[$index] = $parsed
    }

    $version = '{0}.{1}.{2}' -f $parts[0], $parts[1], $parts[2]
    return [pscustomobject]@{
        Version = $version
        Tag = 'v' + $version
        NumericVersion = [Version]::new($parts[0], $parts[1], $parts[2])
    }
}

$initial = ConvertTo-CanonicalReleaseVersion -Value $InitialVersion
if ($null -eq $initial) {
    throw 'InitialVersion must be canonical X.Y.Z with each component between 0 and 2147483647.'
}

if (-not $ExplicitTag -and $RunAttempt -gt 1) {
    throw 'Manual release version resolution cannot be rerun. Re-run failed build/publish jobs, or start a new workflow if checks failed.'
}

$eligibleTags = @($Tags | ForEach-Object {
    $candidate = ConvertTo-CanonicalReleaseVersion -Value $_ -HasTagPrefix
    if ($null -ne $candidate) { $candidate }
} | Sort-Object -Property NumericVersion -Descending)

if ($ExplicitTag) {
    $target = ConvertTo-CanonicalReleaseVersion -Value $ExplicitTag -HasTagPrefix
    if ($null -eq $target) {
        throw 'ExplicitTag must be canonical vX.Y.Z with each component between 0 and 2147483647.'
    }
}
elseif ($eligibleTags.Count -eq 0) {
    $target = $initial
}
else {
    $latest = $eligibleTags[0].NumericVersion
    $major = $latest.Major
    $minor = $latest.Minor
    $patch = $latest.Build
    switch ($Bump) {
        'major' {
            if ($major -eq [int]::MaxValue) { throw 'Major version increment exceeds Int32.MaxValue.' }
            $major++
            $minor = 0
            $patch = 0
        }
        'minor' {
            if ($minor -eq [int]::MaxValue) { throw 'Minor version increment exceeds Int32.MaxValue.' }
            $minor++
            $patch = 0
        }
        'patch' {
            if ($patch -eq [int]::MaxValue) { throw 'Patch version increment exceeds Int32.MaxValue.' }
            $patch++
        }
    }
    $target = ConvertTo-CanonicalReleaseVersion -Value ('{0}.{1}.{2}' -f $major, $minor, $patch)
}

$previousTag = ''
foreach ($candidate in $eligibleTags) {
    if ($candidate.NumericVersion.CompareTo($target.NumericVersion) -lt 0) {
        $previousTag = $candidate.Tag
        break
    }
}

# An explicit first tag can already exist remotely and still have no predecessor.
[pscustomobject]@{
    Version = $target.Version
    Tag = $target.Tag
    PreviousTag = $previousTag
    IsFirstRelease = [string]::IsNullOrEmpty($previousTag)
}
