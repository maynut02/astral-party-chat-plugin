# Shared commit parsing and Markdown for release preparation and upload.
function ConvertFrom-AstralCommitLog {
    param([string]$Log)

    if (-not $Log) { return }
    $fields = $Log.Split([char]0)
    for ($index = 0; $index + 1 -lt $fields.Length; $index += 2) {
        $message = $fields[$index + 1].TrimEnd()
        $subject = ($message -split "`n", 2)[0].TrimEnd("`r")
        $match = [regex]::Match($subject, '\A(?<type>[a-z]+)(?:\([^()\r\n]+\))?(?<breaking>!)?: .+', [Text.RegularExpressions.RegexOptions]::IgnoreCase)
        $type = if ($match.Success) { $match.Groups['type'].Value.ToLowerInvariant() } else { '' }
        $breaking = ($match.Success -and $match.Groups['breaking'].Success) -or
            [regex]::IsMatch($message, '(?m)^BREAKING(?: CHANGE|-CHANGE):\s*\S')
        [pscustomobject]@{ Sha = $fields[$index]; Subject = $subject; Type = $type; Breaking = $breaking }
    }
}

function New-AstralReleaseNotes {
    param(
        [Parameter(Mandatory = $true)][string]$Tag,
        [object[]]$Commits = @()
    )

    $baseUrl = 'https://github.com/maynut02/astral-party-chat-plugin'
    $notes = [Collections.Generic.List[string]]::new()
    $notes.Add('## 변경 사항')
    $notes.Add('')
    foreach ($commit in $Commits) {
        $notes.Add("- $($commit.Subject) ($($commit.Sha.Substring(0, 7)))")
    }
    if ($Commits.Count -eq 0) { $notes.Add('- 버전 및 배포 준비') }
    $notes.Add('')
    $installation = @'
## 설치

Windows x64의 글로벌 Steam판(`8vJXnINT` 폴더, `AstralParty_INT.exe`)만 지원합니다.

1. 게임을 종료합니다.
2. [AstralPartyChatPlugin-{{tag}}.zip]({{repo}}/releases/download/{{tag}}/AstralPartyChatPlugin-{{tag}}.zip)을 내려받아 **전체 압축을 풉니다**.
3. 압축을 푼 폴더에서 `install.bat`을 실행합니다.

설치 프로그램이 Steam 라이브러리에서 게임을 찾아 플러그인을 설치하고, 다른 폴더나 이전 이름(`AstralParty.Chat.dll`)으로 남아 있는 예전 플러그인 파일을 지웁니다. BepInEx 6가 없을 때만 함께 설치하며, 이미 있으면 그대로 두고 `BepInEx.cfg`의 필수 설정만 맞춥니다.

게임에서 방에 들어간 뒤 말풍선 모양의 채팅 버튼을 클릭하세요. 연결 상태가 **연결됨**으로 표시되면 메시지를 입력하고 Enter로 보낼 수 있습니다.

### 업데이트

설치한 뒤에는 게임을 시작할 때 새 버전을 자동으로 내려받아 설치합니다. 화면 왼쪽 위에 안내가 나타나면 **지금 다시 시작**을 눌러 적용하세요. 0.2.1 이하 버전에는 자동 업데이트가 없으므로 위 설치 과정을 한 번 진행해 주세요.

[전체 변경 내역]({{repo}}/commits/{{tag}}) · [설치 및 문제 해결]({{repo}}#설치) · [문제 제보]({{repo}}/issues)
'@
    $notes.Add($installation.Replace('{{repo}}', $baseUrl).Replace('{{tag}}', $Tag))
    return ($notes -join "`n").Replace("`r`n", "`n") + "`n"
}
