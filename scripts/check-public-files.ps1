param()

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
# Include already tracked files: .gitignore cannot remove a previously committed secret.
$listing = & git -C $repoRoot ls-files --cached --others --exclude-standard -z
if ($LASTEXITCODE -ne 0) { throw 'Unable to list public repository candidates.' }
$files = @(($listing -join "`n") -split "`0" | Where-Object { $_ } | Sort-Object -Unique)
$findings = [Collections.Generic.List[string]]::new()
$secretPatterns = @(
    '-----BEGIN (?:[A-Z]+ )?PRIVATE KEY-----',
    '(?<![A-Za-z0-9])gh[pousr]_[A-Za-z0-9]{36,}',
    '(?<![A-Za-z0-9])github_pat_[A-Za-z0-9_]{60,}',
    '(?<![A-Za-z0-9])(?:AKIA|ASIA)[A-Z0-9]{16}(?![A-Za-z0-9])',
    '(?<![A-Za-z0-9])AIza[A-Za-z0-9_-]{35}(?![A-Za-z0-9])'
)
foreach ($file in $files) {
    $leaf = [IO.Path]::GetFileName($file)
    if (($leaf -match '^\.(env|dev\.vars)(\.|$)' -and $leaf -ne '.env.example') -or
        $file -match '(?i)\.(dll|exe|pdb|zip|7z|pfx|p12|pem|key|log)$' -or
        $file -match '(^|/)(\.work|dist|bin|obj|BepInEx)/' -or
        $file -match '^\.agents/docs/') {
        $findings.Add("$file : private/build file is a publication candidate")
        continue
    }
    $path = Join-Path $repoRoot $file
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
    if ($file -notmatch '(?i)\.(cs|csproj|ps1|md|json|ya?ml|txt|config|props|targets)$' -and $leaf -notin @('.gitignore', '.env.example')) { continue }
    $lines = [IO.File]::ReadAllLines($path)
    for ($index = 0; $index -lt $lines.Length; $index++) {
        foreach ($pattern in $secretPatterns) {
            if ($lines[$index] -cmatch $pattern) {
                # Never include a matched line or credential value in output.
                $findings.Add("${file}:$($index + 1) : possible credential")
                break
            }
        }
    }
}
if ($findings.Count -gt 0) {
    $findings | ForEach-Object { Write-Output $_ }
    throw 'Public file check failed. Remove private files or rotate exposed credentials before publication.'
}
Write-Output "Public file check passed ($($files.Count) candidate files). This is a limited pattern check, not a complete secret audit."
