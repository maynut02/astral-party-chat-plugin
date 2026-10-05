#requires -Version 7.0
param()

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$prepare = Join-Path $repoRoot 'scripts/prepare-release.ps1'
$testRoot = Join-Path $repoRoot ('.work/release-preparation-tests/' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$git = Get-Command git -CommandType Application -ErrorAction Stop | Select-Object -First 1 -ExpandProperty Source
$utf8 = [Text.UTF8Encoding]::new($false)
$script:passed = 0

function Invoke-TestGit([string]$Root, [string[]]$Arguments) {
    $output = @(& $git -C $Root @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "Fixture Git failed: $($output -join "`n")" }
    return ($output -join "`n")
}
function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Add-TestCommit([string]$Root, [string]$Message) {
    [IO.File]::AppendAllText((Join-Path $Root 'source.txt'), ([Guid]::NewGuid().ToString('N') + "`n"), $utf8)
    Invoke-TestGit $Root @('add', '.') | Out-Null
    Invoke-TestGit $Root @('commit', '-m', $Message) | Out-Null
}
function New-TestRepository([string]$Name, [string]$Version = '0.0.1', [switch]$Released) {
    $root = Join-Path $testRoot $Name
    [IO.Directory]::CreateDirectory($root) | Out-Null
    Invoke-TestGit $root @('init', '--initial-branch=main') | Out-Null
    Invoke-TestGit $root @('config', 'user.name', 'Release Tests') | Out-Null
    Invoke-TestGit $root @('config', 'user.email', 'release-tests@example.invalid') | Out-Null
    Invoke-TestGit $root @('config', 'core.autocrlf', 'false') | Out-Null
    Invoke-TestGit $root @('config', 'commit.gpgsign', 'false') | Out-Null
    [IO.File]::WriteAllText((Join-Path $root '.gitignore'), ".work/`n", $utf8)
    [IO.File]::WriteAllText((Join-Path $root 'VERSION'), "$Version`n", $utf8)
    Add-TestCommit $root 'chore: 초기 소스'
    if ($Released) { Invoke-TestGit $root @('tag', "v$Version") | Out-Null }
    return $root
}
function Assert-Rejected([string]$Root, [string]$Message, [hashtable]$Arguments = @{}) {
    $before = [IO.File]::ReadAllText((Join-Path $Root 'VERSION'))
    $failed = $false
    try { & $prepare -RepositoryRoot $Root @Arguments | Out-Null }
    catch {
        if ($_.Exception.Message -notlike "*$Message*") { throw }
        $failed = $true
    }
    Assert-True $failed 'Expected release preparation to fail.'
    Assert-True ([IO.File]::ReadAllText((Join-Path $Root 'VERSION')) -ceq $before) 'Rejected preparation changed VERSION.'
}
function Test-Case([string]$Name, [scriptblock]$Body) {
    & $Body
    $script:passed++
    Write-Output "PASS release preparation: $Name"
}

Test-Case 'first-release-keeps-0.0.1-and-generates-korean-notes' {
    $root = New-TestRepository 'first'
    Add-TestCommit $root 'feat: 채팅 기능 추가'
    Add-TestCommit $root 'fix: 입력 처리 수정'
    $result = & $prepare -RepositoryRoot $root
    Assert-True ($result.Version -ceq '0.0.1' -and $result.Tag -ceq 'v0.0.1' -and $result.Title -ceq 'ChatPlugin v0.0.1') 'First release version changed.'
    Assert-True ($result.CommitCount -eq 3 -and -not $result.VersionChanged) 'First release did not aggregate history.'
    $notes = [IO.File]::ReadAllText($result.NotesPath)
    Assert-True ($notes.Contains('채팅 기능 추가') -and $notes.Contains('입력 처리 수정') -and $notes.Contains('Install.cmd')) 'Korean release notes were corrupted.'
    Assert-True ((Invoke-TestGit $root @('tag', '--list')) -eq '') 'Preparation created a tag.'
    Assert-True ((Invoke-TestGit $root @('status', '--porcelain')) -eq '') 'Preparation changed source files.'
    $again = & $prepare -RepositoryRoot $root
    Assert-True ($again.Version -ceq '0.0.1') 'Repeated first release increased version.'
}
Test-Case 'many-fixes-bump-patch-once-and-repeat-after-version-commit' {
    $root = New-TestRepository 'patch' -Released
    1..3 | ForEach-Object { Add-TestCommit $root "fix: 수정 $_" }
    $head = Invoke-TestGit $root @('rev-parse', 'HEAD')
    $result = & $prepare -RepositoryRoot $root
    Assert-True ($result.Version -ceq '0.0.2' -and $result.CommitCount -eq 3 -and $result.VersionChanged) 'Batch did not produce one patch bump.'
    $again = & $prepare -RepositoryRoot $root
    Assert-True ($again.Version -ceq '0.0.2' -and -not $again.VersionChanged) 'Repeated preparation bumped twice.'
    Assert-True ((Invoke-TestGit $root @('rev-parse', 'HEAD')) -ceq $head) 'Preparation committed automatically.'
    Invoke-TestGit $root @('add', 'VERSION') | Out-Null
    Invoke-TestGit $root @('commit', '-m', 'chore: 릴리즈 버전 준비') | Out-Null
    $again = & $prepare -RepositoryRoot $root
    Assert-True ($again.Version -ceq '0.0.2' -and $again.CommitCount -eq 4) 'A version commit triggered another bump.'
}
Test-Case 'features-win-over-fixes-with-scopes-and-case' {
    $root = New-TestRepository 'minor' -Version '1.2.3' -Released
    Add-TestCommit $root 'fix: 버그 수정'
    Add-TestCommit $root 'Feat(chat): 메시지 알림 추가'
    Add-TestCommit $root 'docs: 사용법 수정'
    $result = & $prepare -RepositoryRoot $root
    Assert-True ($result.Version -ceq '1.3.0' -and $result.DetectedBump -ceq 'minor') 'Feature did not win over patch.'
}
foreach ($message in @('feat!: 서버 규약 변경', "refactor: 서버 규약 변경`n`nBREAKING CHANGE: 이전 서버와 호환되지 않음", "chore: 변경`n`nBREAKING-CHANGE: 이전 설정 제거")) {
    Test-Case "stable-breaking-$($script:passed)" {
        $root = New-TestRepository "breaking-$script:passed" -Version '1.2.3' -Released
        Add-TestCommit $root $message
        Assert-True ((& $prepare -RepositoryRoot $root).Version -ceq '2.0.0') 'Breaking change did not increase major.'
    }
}
Test-Case 'pre-1.0-breaking-is-minor-and-major-is-explicit' {
    $root = New-TestRepository 'early-breaking' -Released
    Add-TestCommit $root 'feat!: 이전 설정 제거'
    Assert-True ((& $prepare -RepositoryRoot $root).Version -ceq '0.1.0') 'Early breaking change did not increase minor.'
    Assert-True ((& $prepare -RepositoryRoot $root -Bump major).Version -ceq '1.0.0') 'Explicit stable promotion failed.'
}
Test-Case 'preview-has-no-writes' {
    $root = New-TestRepository 'preview' -Released
    Add-TestCommit $root 'fix: 입력 수정'
    $result = & $prepare -RepositoryRoot $root -Preview
    Assert-True ($result.Version -ceq '0.0.2' -and $result.Preview -and $result.NotesPath -eq '') 'Preview did not resolve version.'
    Assert-True ([IO.File]::ReadAllText((Join-Path $root 'VERSION')).Trim() -ceq '0.0.1') 'Preview wrote VERSION.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $root '.work'))) 'Preview wrote notes.'
}
Test-Case 'docs-only-do-not-trigger-release-and-override-is-available' {
    $root = New-TestRepository 'docs' -Released
    Add-TestCommit $root 'docs: 사용법 수정'
    Add-TestCommit $root 'chore: 검사 설정 수정'
    Assert-Rejected $root 'No release changes'
    Assert-True ((& $prepare -RepositoryRoot $root -Bump patch).Version -ceq '0.0.2') 'Manual patch override failed.'
}
foreach ($type in @('perf', 'revert')) {
    Test-Case "$type-is-patch" {
        $root = New-TestRepository $type -Released
        Add-TestCommit $root "${type}: 동작 변경"
        Assert-True ((& $prepare -RepositoryRoot $root).Version -ceq '0.0.2') 'Patch type not recognized.'
    }
}
Test-Case 'no-new-commits-do-not-release' {
    $root = New-TestRepository 'no-changes' -Released
    Assert-Rejected $root 'No release changes'
}
Test-Case 'next-release-analyzes-only-commits-after-manual-tag' {
    $root = New-TestRepository 'next' -Released
    Add-TestCommit $root 'feat: 기능 추가'
    $first = & $prepare -RepositoryRoot $root
    Invoke-TestGit $root @('add', 'VERSION') | Out-Null
    Invoke-TestGit $root @('commit', '-m', 'chore: 0.1.0 릴리즈 준비') | Out-Null
    Invoke-TestGit $root @('tag', '-a', 'v0.1.0', '-m', 'Manual release') | Out-Null
    Add-TestCommit $root 'fix: 새 버그 수정'
    $next = & $prepare -RepositoryRoot $root
    Assert-True ($next.Version -ceq '0.1.1' -and $next.PreviousTag -ceq 'v0.1.0' -and $next.CommitCount -eq 1) 'Next release included previous feature commits.'
    Assert-True (-not [IO.File]::ReadAllText($next.NotesPath).Contains('기능 추가')) 'Notes included old release changes.'
}
Test-Case 'numeric-tags-and-unrelated-branches' {
    $root = New-TestRepository 'tag-order' -Version '1.0.9' -Released
    Invoke-TestGit $root @('checkout', '-b', 'other') | Out-Null
    Add-TestCommit $root 'feat: 다른 브랜치 변경'
    Invoke-TestGit $root @('tag', 'v99.0.0') | Out-Null
    Invoke-TestGit $root @('checkout', 'main') | Out-Null
    [IO.File]::WriteAllText((Join-Path $root 'VERSION'), "1.0.10`n", $utf8)
    Add-TestCommit $root 'chore: 이전 릴리즈 준비'
    Invoke-TestGit $root @('tag', 'v1.0.10') | Out-Null
    Invoke-TestGit $root @('tag', 'v999.0.0-rc.1') | Out-Null
    Invoke-TestGit $root @('tag', 'v01.0.0') | Out-Null
    Add-TestCommit $root 'fix: 버그 수정'
    $result = & $prepare -RepositoryRoot $root
    Assert-True ($result.Version -ceq '1.0.11' -and $result.PreviousTag -ceq 'v1.0.10') 'Tag selection was not numeric/reachable.'
}
Test-Case 'merge-commit-is-excluded-but-branch-commits-are-analyzed' {
    $root = New-TestRepository 'merge' -Released
    Invoke-TestGit $root @('checkout', '-b', 'feature') | Out-Null
    Add-TestCommit $root 'feat: 브랜치 기능'
    Invoke-TestGit $root @('checkout', 'main') | Out-Null
    Invoke-TestGit $root @('merge', '--no-ff', 'feature', '-m', 'feat!: misleading merge message') | Out-Null
    $result = & $prepare -RepositoryRoot $root
    Assert-True ($result.Version -ceq '0.1.0' -and $result.CommitCount -eq 1 -and $result.DetectedBump -ceq 'minor') 'Merge analysis lost branch changes or included merge messages.'
}
Test-Case 'new-feature-after-patch-preparation-needs-explicit-replan' {
    $root = New-TestRepository 'new-feature' -Released
    Add-TestCommit $root 'fix: 수정'
    & $prepare -RepositoryRoot $root | Out-Null
    Invoke-TestGit $root @('add', 'VERSION') | Out-Null
    Invoke-TestGit $root @('commit', '-m', 'chore: 버전 준비') | Out-Null
    Add-TestCommit $root 'feat: 새 기능'
    Assert-Rejected $root 'Prepared VERSION is too low'
    Assert-True ((& $prepare -RepositoryRoot $root -Bump minor).Version -ceq '0.1.0') 'Explicit replan failed.'
}
Test-Case 'explicit-version-and-repeat-keep-same-value' {
    $root = New-TestRepository 'explicit' -Released
    Add-TestCommit $root 'fix: 수정'
    $result = & $prepare -RepositoryRoot $root -Version '0.5.0'
    Assert-True ($result.Version -ceq '0.5.0') 'Explicit version was not used.'
    Assert-True ((& $prepare -RepositoryRoot $root).Version -ceq '0.5.0') 'Repeat overwrote explicit version.'
    Assert-Rejected $root 'must not decrease' @{ Version = '0.2.0' }
    Assert-Rejected $root 'would decrease' @{ Bump = 'patch' }
    Assert-Rejected $root 'InitialVersion must be canonical' @{ Version = '0.05.0' }
    Assert-Rejected $root 'between 0 and 65534' @{ Version = '65535.0.0' }
}
Test-Case 'dirty-tracked-staged-and-untracked-source-are-blocked' {
    foreach ($kind in @('tracked', 'staged', 'untracked')) {
        $root = New-TestRepository "dirty-$kind" -Released
        Add-TestCommit $root 'fix: 수정'
        $leaf = if ($kind -eq 'untracked') { 'new.txt' } else { 'source.txt' }
        [IO.File]::AppendAllText((Join-Path $root $leaf), 'dirty', $utf8)
        if ($kind -eq 'staged') { Invoke-TestGit $root @('add', $leaf) | Out-Null }
        Assert-Rejected $root 'Commit source changes'
    }
}
Test-Case 'published-tag-version-mismatch-is-blocked' {
    $root = New-TestRepository 'tag-mismatch'
    Invoke-TestGit $root @('tag', 'v0.1.0') | Out-Null
    [IO.File]::WriteAllText((Join-Path $root 'VERSION'), "0.1.0`n", $utf8)
    Add-TestCommit $root 'fix: 수정'
    Assert-Rejected $root 'does not match its committed VERSION'
}
Test-Case 'older-version-file-is-blocked' {
    $root = New-TestRepository 'older' -Version '0.1.0' -Released
    [IO.File]::WriteAllText((Join-Path $root 'VERSION'), "0.0.1`n", $utf8)
    Assert-Rejected $root 'older than the latest'
}
Test-Case 'target-tag-on-unmerged-branch-cannot-be-reused' {
    $root = New-TestRepository 'tag-collision' -Released
    Invoke-TestGit $root @('checkout', '-b', 'other') | Out-Null
    Add-TestCommit $root 'fix: 다른 수정'
    Invoke-TestGit $root @('tag', 'v0.0.2') | Out-Null
    Invoke-TestGit $root @('checkout', 'main') | Out-Null
    Add-TestCommit $root 'fix: 수정'
    Assert-Rejected $root 'target release tag already exists'
}
Test-Case 'shallow-history-is-blocked' {
    $original = New-TestRepository 'full' -Released
    Add-TestCommit $original 'fix: 수정'
    $shallow = Join-Path $testRoot 'shallow'
    $url = ([Uri]::new($original + [IO.Path]::DirectorySeparatorChar)).AbsoluteUri
    Invoke-TestGit $testRoot @('clone', '--depth', '1', $url, $shallow) | Out-Null
    Assert-Rejected $shallow 'full Git history'
}

Test-Case 'windows-powershell-5.1-preserves-korean-release-notes' {
    $root = New-TestRepository 'windows-powershell'
    Add-TestCommit $root 'feat: 한글 메시지 입력'
    $windowsPowerShell = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
    $output = @(& $windowsPowerShell -NoProfile -File $prepare -RepositoryRoot $root 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "Windows PowerShell preparation failed: $($output -join "`n")" }
    $notes = [IO.File]::ReadAllText((Join-Path $root '.work/releases/v0.0.1/release-notes.md'))
    Assert-True ($notes.Contains('첫 릴리즈입니다.') -and $notes.Contains('한글 메시지 입력')) 'Windows PowerShell corrupted UTF-8 text.'
}

Test-Case 'real-project-generates-plugin-and-dll-versions-and-rejects-overrides' {
    $root = Join-Path $testRoot 'stamp'
    $src = Join-Path $root 'src'
    [IO.Directory]::CreateDirectory($src) | Out-Null
    # Exercise the production MSBuild target without redistributing game DLLs.
    [xml]$project = [IO.File]::ReadAllText((Join-Path $repoRoot 'src/AstralParty.Chat.csproj'))
    foreach ($reference in @($project.SelectNodes('//Reference'))) { $reference.ParentNode.RemoveChild($reference) | Out-Null }
    $projectPath = Join-Path $src 'AstralParty.Chat.csproj'
    $project.Save($projectPath)
    [IO.File]::WriteAllText((Join-Path $root 'NuGet.Config'), '<configuration><packageSources><clear /></packageSources></configuration>', $utf8)
    $fixture = @'
using System;
using System.Reflection;
using BepInEx;
[assembly: AssemblyVersion(AstralParty.Chat.AstralPartyChatPlugin.PluginVersion)]
[assembly: AssemblyFileVersion(AstralParty.Chat.AstralPartyChatPlugin.PluginVersion)]
[assembly: AssemblyInformationalVersion(AstralParty.Chat.AstralPartyChatPlugin.PluginVersion)]
namespace BepInEx {
    public sealed class BepInPlugin : Attribute {
        public BepInPlugin(string guid, string name, string version) { }
    }
}
namespace AstralParty.Chat {
    [BepInPlugin("fixture", "fixture", PluginVersion)]
    public sealed partial class AstralPartyChatPlugin { }
}
'@
    [IO.File]::WriteAllText((Join-Path $src 'Fixture.cs'), $fixture, $utf8)
    $localDotnet = Join-Path $repoRoot '.work/dotnet/dotnet.exe'
    $dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1 -ExpandProperty Source }
    foreach ($version in @('0.0.1', '0.1.0')) {
        [IO.File]::WriteAllText((Join-Path $root 'VERSION'), "$version`n", $utf8)
        $output = @(& $dotnet build $projectPath --configuration Release --nologo 2>&1)
        if ($LASTEXITCODE -ne 0) { throw "Version stamp build failed: $($output -join "`n")" }
        $dll = Join-Path $src 'bin/Release/net6.0/AstralParty.Chat.dll'
        $metadata = [Diagnostics.FileVersionInfo]::GetVersionInfo($dll)
        Assert-True ($metadata.ProductVersion -ceq $version -and $metadata.FileVersion -ceq $version) 'DLL metadata differs from VERSION.'
        Assert-True ([Reflection.AssemblyName]::GetAssemblyName($dll).Version.ToString() -ceq "$version.0") 'Assembly version differs from VERSION.'
        $assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($dll))
        $plugin = $assembly.GetType('AstralParty.Chat.AstralPartyChatPlugin')
        Assert-True ($plugin.GetField('PluginVersion').GetRawConstantValue() -ceq $version) 'Generated constant differs from VERSION.'
        Assert-True ($plugin.CustomAttributes.ConstructorArguments[2].Value -ceq $version) 'BepInPlugin metadata differs from VERSION.'
    }
    $override = @(& $dotnet build $projectPath --configuration Release --nologo '-p:Version=9.9.9' 2>&1)
    Assert-True ($LASTEXITCODE -ne 0 -and ($override -join "`n").Contains('must match the VERSION file')) 'MSBuild override bypassed VERSION.'
    [IO.File]::WriteAllText((Join-Path $root 'VERSION'), "0.01.0`n", $utf8)
    $invalid = @(& $dotnet build $projectPath --configuration Release --nologo 2>&1)
    Assert-True ($LASTEXITCODE -ne 0 -and ($invalid -join "`n").Contains('must contain canonical X.Y.Z')) 'Invalid VERSION was compiled.'
}

Write-Output "Release preparation tests passed: $script:passed cases."
# Failed Git probes in rejection tests must not become the pwsh process exit code.
$global:LASTEXITCODE = 0
