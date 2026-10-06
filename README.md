# AstralPartyChatPlugin

아스트랄 파티 글로벌 Steam판에서 같은 방의 참가자와 채팅할 수 있는 BepInEx 플러그인입니다. 방 번호와 Steam 닉네임을 자동으로 읽어 채팅방에 연결합니다.

- 방 대기, 캐릭터 선택, 플레이 화면에서 채팅
- 한국어 입력과 Enter 전송, 입력 중 게임 키보드 조작 차단
- 메시지 스크롤바와 **새로운 메시지** 버튼
- 연결이 끊겼을 때 자동 재연결

## 설치

Windows x64의 글로벌 Steam판만 설치를 지원합니다. 게임에 **BepInEx 6의 Unity IL2CPP Windows x64 버전**이 먼저 설치되어 있어야 합니다. 이 플러그인에는 BepInEx가 포함되어 있지 않습니다.

| Steam판 | 설치 폴더 | 실행 파일 |
| --- | --- | --- |
| 글로벌판 | `8vJXnINT` | `AstralParty_INT.exe` |

### 1. BepInEx 설치와 게임용 설정

1. 게임을 종료하고, Steam 라이브러리에서 아스트랄 파티를 우클릭해 **관리 → 로컬 파일 보기**를 선택합니다.
2. 열린 폴더 안의 `8vJXnINT` 폴더로 들어갑니다. `AstralParty_INT.exe`가 있는 위치입니다.
3. [BepInEx 다운로드](https://builds.bepinex.dev/projects/bepinex_be)에서 **Unity IL2CPP Windows x64** 배포물(`BepInEx-Unity.IL2CPP-win-x64`로 이름이 시작하는 ZIP)을 내려받아 압축 내용 전체를 이 위치에 복사합니다. 이 플러그인에서 확인한 버전은 `6.0.0-be.788`입니다.
4. **게임을 처음 실행하기 전에** `BepInEx/config/BepInEx.cfg`를 메모장으로 열고 아래 설정을 적용합니다. 폴더나 파일이 없다면 직접 만드세요. 파일 이름은 `BepInEx.cfg`이며 `BepInEx.cfg.txt`로 저장되지 않도록 확인하세요.

```ini
[Logging]
UnityLogListening = false

[Logging.Console]
Enabled = false
```

기존 설정 파일이 있다면 해당 항목 두 개의 값만 바꾸고 다른 설정은 유지하세요. 같은 섹션과 항목을 중복으로 추가할 필요는 없습니다. 이 설정은 아스트랄 파티의 BepInEx 초기화 문제를 피하기 위한 것으로, 한글패치의 WindowsPlugin 배포물에도 포함되어 있습니다. 이미 해당 배포물로 설치했다면 위 값이 적용되어 있는지 확인하세요.

설정을 저장한 뒤 Steam에서 게임을 한 번 실행하고 종료합니다. 최초 실행은 BepInEx의 참조 파일 생성 때문에 시간이 걸릴 수 있습니다.

### 2. 채팅 플러그인 설치

1. 게임을 종료합니다.
2. [Releases](https://github.com/maynut02/astral-party-chat-plugin/releases)에서 `AstralPartyChatPlugin-v버전.zip`을 내려받아 압축을 풉니다.
3. 압축에서 꺼낸 `BepInEx` 폴더를 위의 `8vJXnINT` 위치에 복사하고, 기존 폴더와 합칩니다.

설치 후 파일 위치가 다음과 같으면 됩니다.

```text
Astral Party/
└─ 8vJXnINT/
   ├─ AstralParty_INT.exe
   └─ BepInEx/
      ├─ config/
      │  └─ BepInEx.cfg
      └─ plugins/
         └─ AstralPartyChatPlugin/
            └─ AstralPartyChatPlugin.dll
```

수동 설치하려면 ZIP에서 `AstralPartyChatPlugin.dll`을 꺼내 `BepInEx/plugins/AstralPartyChatPlugin` 폴더를 만들고 그 안에 넣으세요. Release에는 ZIP과 `SHA256SUMS.txt`만 첨부합니다. `SHA256SUMS.txt`는 ZIP의 무결성 확인용이며 설치할 필요가 없습니다.

## 사용 방법

게임에서 방에 들어간 뒤 말풍선 모양의 채팅 버튼을 클릭하세요. 연결 상태가 **연결됨**으로 표시되면 메시지를 입력하고 Enter로 보낼 수 있습니다. 한국어 조합 중에도 Enter를 누르면 조합이 확정된 뒤 전송됩니다.

이전 메시지는 휠이나 스크롤바로 확인할 수 있습니다. 위로 스크롤한 상태에서 메시지가 추가되면 **새로운 메시지** 버튼이 표시되며, 클릭하면 가장 아래로 이동합니다. 입력칸에 커서가 있는 동안에는 게임의 키보드 조작이 차단됩니다. 게임 조작으로 돌아가려면 입력칸 밖을 클릭하거나 채팅창을 닫으세요.

게임 방 번호, Steam 표시 닉네임, 선택 캐릭터, 참가 순서(P1~P4), 작성한 메시지는 [채팅 서버](https://astral.maynutlab.com)로 전송됩니다. 방 번호를 아는 사용자가 같은 채팅방에 접근할 수 있으므로 개인 정보나 비밀번호를 입력하지 마세요.

## 업데이트와 제거

업데이트할 때는 게임을 종료한 뒤 새 버전의 ZIP을 내려받아 위의 설치 위치에 DLL을 교체하세요. 이전 이름인 `AstralParty.Chat.dll`을 사용하고 있었다면 해당 파일을 제거하세요. `BepInEx/plugins`에는 최신 `AstralPartyChatPlugin.dll`을 한 개만 유지하세요.

플러그인을 제거하려면 게임을 종료하고 `BepInEx/plugins/AstralPartyChatPlugin` 폴더를 삭제하세요. 다른 플러그인이 사용하는 BepInEx 폴더 전체를 삭제할 필요는 없습니다.

## 문제가 생겼을 때

- **채팅 버튼이 보이지 않음:** 방에 들어갔는지, DLL이 위의 위치에 있는지, BepInEx가 설치되어 있는지 확인하세요.
- **계속 연결 중으로 표시됨:** 인터넷 연결과 채팅 서버 상태를 확인하세요. 일시적인 연결 실패는 자동으로 재시도합니다.
- **업데이트 후 동작하지 않음:** 중복 DLL을 확인하세요. 게임 업데이트로 호환성이 달라졌을 수도 있습니다.

해결되지 않으면 [Issues](https://github.com/maynut02/astral-party-chat-plugin/issues)에 플러그인 버전, 문제가 발생한 화면, 재현 방법과 `BepInEx/LogOutput.txt`의 관련 내용을 알려주세요. 로그를 공개하기 전에 닉네임·방 번호·개인 경로 등은 가려주세요.

## 빌드

BepInEx가 초기화된 게임과 Git이 있는 Windows 환경에서, 저장소 폴더의 PowerShell로 실행합니다.

```powershell
.\scripts\setup.ps1
.\scripts\test.ps1
```

DLL만 만들려면 `build.ps1`, 릴리즈용 DLL·ZIP·체크섬을 함께 만들려면 `package-release.ps1`을 실행합니다. 둘 중 필요한 명령 하나만 실행하면 됩니다.

```powershell
.\scripts\build.ps1             # DLL 생성
.\scripts\package-release.ps1   # 소스 빌드 후 릴리즈 파일 생성
```

`setup.ps1`은 빌드용 참조 DLL과 로컬 .NET SDK를 준비합니다. `package-release.ps1`은 최신 소스를 빌드한 뒤 패키징하므로 사전 빌드는 필요 없습니다. 빌드 결과는 `dist/AstralPartyChatPlugin.dll`, 패키지는 `dist/release/v버전/`에 생성됩니다. 버전은 `VERSION` 파일을 기준으로 하며, 새 릴리즈 버전은 빌드 전에 준비합니다.

로컬 패키지를 GitHub Release에 업로드하려면 다음 명령을 실행합니다. 실제 업로드 전에는 GitHub CLI 설치와 최초 `gh auth login`을 마치고, 미커밋 변경이 없는 HEAD를 먼저 `origin`에 push하세요.

```powershell
.\scripts\upload-release.ps1 -Preview  # GitHub 호출 없이 로컬 계획 확인
.\scripts\upload-release.ps1           # 기존 패키지 업로드 및 공개
```

업로드 명령은 빌드하지 않으며 ZIP과 `SHA256SUMS.txt`만 첨부합니다. 별도 DLL은 로컬 산출물로 유지하고, 체크섬에는 ZIP 한 개만 기록합니다. `-Draft`를 지정하면 draft로 유지합니다. 릴리즈 본문은 명시한 `-NotesFile`, 준비된 설명 파일, 로컬 Git 커밋 기록 순서로 선택합니다. Git 기록으로 생성할 때도 설치·업데이트 안내를 포함하며 `-Preview`의 `NotesContent`로 전체 본문을 확인할 수 있습니다. 옵션과 재시도 절차는 [릴리즈 문서](docs/releases.md#4-github-release에-업로드)에 있습니다.

- [개발 환경과 검증](docs/development.md)
- [로컬 빌드와 릴리즈](docs/releases.md)
- [코드 구조와 서버 연동](docs/architecture.md)

## 라이선스

이 플러그인의 소스 코드와 문서는 [MIT 라이선스](LICENSE)로 배포합니다. 저작권 및 라이선스 고지를 유지하면 사용, 수정, 재배포와 상업적 이용이 가능합니다. 게임과 외부 의존성에는 각각의 라이선스가 적용됩니다.
