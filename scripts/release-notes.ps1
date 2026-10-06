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

Windows x64의 글로벌 Steam판만 설치를 지원합니다.

1. 게임을 종료합니다.
2. **BepInEx 6 Unity IL2CPP Windows x64**를 먼저 설치하고, [설치 안내]({{repo}}#설치)에 따라 `BepInEx/config/BepInEx.cfg`를 설정합니다.
3. [AstralPartyChatPlugin-{{tag}}.zip]({{repo}}/releases/download/{{tag}}/AstralPartyChatPlugin-{{tag}}.zip)을 내려받아 압축을 풉니다.
4. 압축에서 꺼낸 `BepInEx` 폴더를 **게임 실행 파일이 있는 폴더**에 복사하고 기존 폴더와 합칩니다.

| Steam판 | 설치 폴더 | 실행 파일 |
| --- | --- | --- |
| 글로벌판 | `8vJXnINT` | `AstralParty_INT.exe` |

설치 후 아래 DLL 한 개가 있어야 합니다. ZIP에는 플러그인 DLL 한 개만 포함됩니다.

```text
BepInEx/
└─ plugins/
   └─ AstralPartyChatPlugin/
      └─ AstralPartyChatPlugin.dll
```

게임에서 방에 들어간 뒤 말풍선 모양의 채팅 버튼을 클릭하세요. 연결 상태가 **연결됨**으로 표시되면 메시지를 입력하고 Enter로 보낼 수 있습니다.

### 기존 버전에서 업데이트

게임을 종료한 뒤 새 DLL로 교체하세요. 이전 이름인 `AstralParty.Chat.dll`이 남아 있다면 삭제하고, `BepInEx/plugins`에는 최신 `AstralPartyChatPlugin.dll`을 한 개만 유지하세요.

[전체 변경 내역]({{repo}}/commits/{{tag}}) · [설치 및 문제 해결]({{repo}}#설치) · [문제 제보]({{repo}}/issues)
'@
    $notes.Add($installation.Replace('{{repo}}', $baseUrl).Replace('{{tag}}', $Tag))
    return ($notes -join "`n").Replace("`r`n", "`n") + "`n"
}
