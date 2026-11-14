# AstralPartyChatPlugin

아스트랄 파티 글로벌 Steam판에서 같은 방의 참가자와 채팅할 수 있는 BepInEx 플러그인입니다. 게임의 현재 방 데이터와 인게임 닉네임을 읽어 채팅방에 연결합니다.

- 방 대기, 캐릭터 선택, 플레이 화면에서 채팅
- 게임 UI(FairyGUI) 안에 그려져 해상도·화면 비율·창 크기 변경을 게임 UI와 같은 기준으로 따름
- 한국어 입력과 Enter 전송, 입력 중 게임 키보드 조작 차단
- 채팅창 이동·크기 조절, 글자 크기·배경 불투명도 설정
- 메시지 스크롤바와 **새로운 메시지** 버튼
- 연결이 끊겼을 때 자동 재연결

## 설치

Windows x64의 글로벌 Steam판만 지원합니다. [Releases](https://github.com/maynut02/astral-party-chat-plugin/releases)의 `AstralPartyChatPlugin-v버전.zip`에는 플러그인, 설치 프로그램과 **BepInEx 6 Unity IL2CPP Windows x64**(`6.0.0-be.788`)가 함께 들어 있습니다.

| Steam판 | 설치 폴더 | 실행 파일 |
| --- | --- | --- |
| 글로벌판 | `8vJXnINT` | `AstralParty_INT.exe` |

### 간편 설치 (권장)

1. 게임을 종료합니다.
2. [Releases](https://github.com/maynut02/astral-party-chat-plugin/releases)에서 `AstralPartyChatPlugin-v버전.zip`을 내려받아 **전체 압축을 풉니다**.
3. 압축을 푼 폴더에서 `install.bat`을 실행합니다.

설치 프로그램은 다음 순서로 동작합니다. 게임 파일은 바꾸지 않습니다.

- Steam 라이브러리에서 게임을 찾습니다. 찾지 못하면 `AstralParty_INT.exe`가 있는 폴더를 입력하세요. 게임이 실행 중이면 종료할 때까지 기다립니다.
- BepInEx가 없을 때만 ZIP에 포함된 BepInEx를 설치합니다. 이미 있으면 덮어쓰지 않습니다.
- `BepInEx/config/BepInEx.cfg`의 `UnityLogListening`과 `[Logging.Console]`의 `Enabled`를 `false`로 맞춥니다. 다른 설정, 주석과 파일 인코딩은 유지합니다.
- `BepInEx/plugins` 아래의 다른 폴더나 이전 이름(`AstralParty.Chat.dll`)으로 남아 있는 예전 플러그인 파일을 지우고, 새 DLL을 `BepInEx/plugins/AstralPartyChatPlugin/`에 설치합니다. 다른 플러그인은 건드리지 않습니다.
- 원하면 Steam 로그인을 확인한 뒤 게임을 실행합니다. BepInEx를 처음 설치했다면 첫 실행은 1~2분 동안 화면이 뜨지 않습니다.

### 수동 설치

1. 게임을 종료하고, Steam 라이브러리에서 아스트랄 파티를 우클릭해 **관리 → 로컬 파일 보기**를 선택한 뒤 위 표의 `8vJXnINT` 폴더로 들어갑니다.
2. BepInEx가 없다면 ZIP의 내용 전체(`install.bat`·`install.ps1` 제외)를 이 위치에 복사합니다. ZIP의 `BepInEx/config/BepInEx.cfg`에는 아래 설정이 들어 있습니다.
3. BepInEx가 이미 있다면 ZIP의 `BepInEx/plugins/AstralPartyChatPlugin` 폴더만 복사하고, `BepInEx/config/BepInEx.cfg`에 아래 설정을 적용합니다. 같은 섹션과 항목을 중복으로 추가하지 않습니다.

```ini
[Logging]
UnityLogListening = false

[Logging.Console]
Enabled = false
```

이 설정은 아스트랄 파티의 BepInEx 초기화 문제를 피하기 위한 것입니다. 설치 후 파일 위치가 다음과 같으면 됩니다.

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

`BepInEx/plugins`에는 이 플러그인의 DLL을 한 개만 두세요. 다른 폴더의 사본이나 이전 이름인 `AstralParty.Chat.dll`이 남아 있으면 예전 버전이 대신 로드될 수 있습니다. Release에 함께 첨부된 `update-v버전.zip`·`update.json`·`update.json.sig`는 자동 업데이트용, `SHA256SUMS.txt`는 무결성 확인용이며 직접 내려받을 필요는 없습니다.

## 사용 방법

게임에서 방에 들어간 뒤 말풍선 모양의 채팅 버튼을 클릭하세요. 버튼은 화면마다 게임 버튼 옆에 붙어 표시됩니다.

| 화면 | 채팅 버튼 위치 |
| --- | --- |
| 방 대기 | **게임 시작**·**준비** 버튼 왼쪽 |
| 캐릭터 선택 | 캐릭터 확정 버튼의 오른쪽 위 |
| 플레이 | 화면 오른쪽 표정·빠른 채팅·핑 버튼 줄의 아래 칸 |

게임 업데이트로 해당 버튼을 찾지 못하면 이전 버전과 같은 화면 비율 위치에 표시합니다. 플레이 중에는 표정·빠른 채팅·핑 버튼 열 자체가 화면에서 사라지거나 숨겨지면 채팅 버튼도 숨깁니다. 이때 이미 열린 채팅창은 유지되며, 버튼 열이 표시된 상태에서 기준 버튼만 찾지 못한 경우에만 비율 위치를 사용합니다.

채팅창을 열면 입력칸에 바로 입력할 수 있습니다. 연결 상태가 **연결됨**으로 표시되면 메시지를 입력하고 Enter로 보낼 수 있습니다. 한국어 조합 중에도 Enter를 누르면 조합이 확정된 뒤 전송됩니다.

이전 메시지는 휠이나 스크롤바로 확인할 수 있습니다. 위로 스크롤한 상태에서 메시지가 추가되면 **새로운 메시지** 버튼이 표시되며, 클릭하면 가장 아래로 이동합니다. 입력칸에 커서가 있는 동안에는 게임의 키보드 조작이 차단되며, 입력칸 편집에 쓰는 Ctrl·Shift 조합은 그대로 동작합니다. 게임 조작으로 돌아가려면 입력칸 밖을 클릭하거나 채팅창을 닫으세요. 입력 중 Esc를 누르면 이번에 입력한 내용이 취소되고 입력칸에서 빠져나옵니다.

채팅창 위쪽 제목 영역을 끌면 창을 옮길 수 있고, 오른쪽 아래 모서리를 끌면 크기를 바꿀 수 있습니다. 제목 영역의 **≡** 버튼을 누르면 설정이 열립니다.

| 설정 | 범위 |
| --- | --- |
| 글자 크기 | 80~150%, 10% 단위 |
| 배경 불투명도 | 30~100%, 10% 단위. 메시지 글자는 투명해지지 않습니다. |
| 창 크기·위치 초기화 | 기본 크기(560×700)와 화면 오른쪽 위치로 되돌림 |

창 위치·크기와 설정은 게임에 저장되어 다음 실행에도 유지됩니다. 해상도나 창 크기가 바뀌면 창은 화면 오른쪽 가장자리와의 거리를 유지하며, 화면 밖으로 나가지 않도록 위치와 크기를 맞춥니다. 이전 버전에서 옮겨 둔 창 위치는 좌표 기준이 달라 한 번 기본 위치로 돌아갑니다.

게임 방 번호, 인게임 닉네임, 선택 캐릭터, 참가 순서(P1~P4), 작성한 메시지는 [채팅 서버](https://astral.maynutlab.com)로 전송됩니다. 방 번호를 아는 사용자가 같은 채팅방에 접근할 수 있으므로 개인 정보나 비밀번호를 입력하지 마세요.

진행 중인 게임에 재접속하면 게임이 복원한 방 정보로 같은 채팅방에 다시 연결합니다. 캐릭터를 고르기 전에는 **미선택**과 현재 픽순을 표시합니다. 게임 정보가 아직 준비되지 않은 동안에는 **게임 정보 확인 중**으로 표시됩니다.

## 업데이트와 제거

플러그인은 게임을 시작할 때 GitHub의 최신 릴리즈를 확인합니다. 새 버전이 있으면 배포 서명을 검증한 뒤 플러그인 DLL만 교체하고, 화면 왼쪽 위에 안내를 표시합니다. **지금 다시 시작**을 누르면 게임이 새 버전으로 다시 실행되며, 안내를 닫으면 다음 게임 실행부터 적용됩니다. 최신 버전이거나 인터넷에 연결되지 않은 경우처럼 알릴 내용이 없으면 안내는 나타나지 않습니다.

자동 설치를 끄려면 `BepInEx/config/astral-party.chat.cfg`에서 `AutoUpdate = false`로 바꾸세요. 이때는 새 버전이 나왔다는 안내만 표시합니다. 이전 DLL은 다음 실행 때까지 `AstralPartyChatPlugin.dll.old`로 남았다가 지워집니다.

0.2.1 이하 버전에는 자동 업데이트가 없습니다. [간편 설치](#간편-설치-권장)를 한 번 진행하면 이후 버전부터 자동으로 설치됩니다. 직접 업데이트할 때도 게임을 종료한 뒤 새 ZIP의 `install.bat`을 실행하면 됩니다.

플러그인을 제거하려면 게임을 종료하고 `BepInEx/plugins/AstralPartyChatPlugin` 폴더를 삭제하세요. 다른 플러그인이 쓰지 않는 BepInEx까지 지우려면 게임 폴더의 `BepInEx`·`dotnet` 폴더와 `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`, `changelog.txt`, `LICENSE-BepInEx.txt`를 삭제합니다.

## 문제가 생겼을 때

- **채팅 버튼이 보이지 않음:** 방에 들어갔는지, DLL이 위의 위치에 있는지, BepInEx가 설치되어 있는지 확인하세요.
- **채팅 버튼이 게임 버튼 옆이 아닌 곳에 보임:** 게임 업데이트로 기준 버튼을 찾지 못한 경우입니다. `BepInEx/LogOutput.log`의 `Chat button anchor:` 줄이 `screen ratio`로 표시됩니다. 이 줄을 제보에 포함해 주세요.
- **계속 연결 중으로 표시됨:** 인터넷 연결과 채팅 서버 상태를 확인하세요. 일시적인 연결 실패는 자동으로 재시도합니다.
- **업데이트 실패 안내가 표시됨:** 게임을 종료한 뒤 새 ZIP의 `install.bat`을 실행하세요. 원인은 `BepInEx/LogOutput.log`의 `Plugin update` 줄에 남습니다.
- **업데이트 후 동작하지 않음:** `install.bat`을 다시 실행해 중복 DLL을 정리하세요. 게임 업데이트로 호환성이 달라졌을 수도 있습니다.

해결되지 않으면 [Issues](https://github.com/maynut02/astral-party-chat-plugin/issues)에 플러그인 버전, 문제가 발생한 화면, 재현 방법과 `BepInEx/LogOutput.log`의 관련 내용을 알려주세요. 로그를 공개하기 전에 닉네임·방 번호·개인 경로 등은 가려주세요.

## 빌드

Windows x64에서 PowerShell 7.2 이상, Git과 BepInEx가 초기화된 게임을 준비하고 저장소 루트에서 실행합니다.

```powershell
.\scripts\setup.ps1
```

`setup.ps1`은 게임의 참조 DLL을 `.work/refs`에 복사하고 `global.json`이 지정한 로컬 .NET SDK를 `.work/dotnet`에 준비합니다. 첫 SDK 설치에는 인터넷 연결이 필요합니다.

DLL만 필요하면 빌드 명령을, 릴리즈 파일도 필요하면 패키징 명령을 선택하세요. 기본 패키징은 항상 최신 소스를 빌드하므로 선행 빌드는 필요 없습니다.

```powershell
.\scripts\build.ps1             # DLL 한 개와 빌드 참조 기록 생성
.\scripts\package-release.ps1   # 소스 빌드 후 설치 ZIP·업데이트 ZIP·서명·체크섬 생성 (서명 키 필요)
```

빌드 결과는 `dist/AstralPartyChatPlugin.dll`, 릴리즈 파일은 `dist/release/vX.Y.Z/`에 생성됩니다. `VERSION`이 제품 버전의 유일한 기준이며 빌드와 패키징은 버전을 올리지 않습니다. 경로 옵션과 전체 검사 명령은 [개발 문서](docs/development.md), 기존 DLL을 사용하는 `-DllPath`와 버전 준비·GitHub Release 업로드는 [릴리즈 문서](docs/releases.md)에 있습니다.

- [개발 환경과 검증](docs/development.md)
- [로컬 빌드와 릴리즈](docs/releases.md)
- [코드 구조와 실행 흐름](docs/architecture.md)

## 라이선스

소스 코드와 문서는 [MIT 라이선스](LICENSE)로 배포합니다. 저작권 및 라이선스 고지를 유지하면 사용, 수정, 재배포와 상업적 이용이 가능합니다. 게임과 외부 의존성에는 각각의 라이선스가 적용됩니다.
