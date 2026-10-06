# AstralPartyChatPlugin

아스트랄 파티 글로벌 Steam판에서 같은 방의 참가자와 채팅할 수 있는 BepInEx 플러그인입니다. 방 번호와 Steam 닉네임을 자동으로 읽어 채팅방에 연결합니다.

- 방 대기, 캐릭터 선택, 플레이 화면에서 채팅
- 한국어 입력과 Enter 전송, 입력 중 게임 키보드 조작 차단
- 메시지 스크롤바와 **새로운 메시지** 버튼
- 연결이 끊겼을 때 자동 재연결

## 설치

Windows x64의 글로벌 Steam판만 지원합니다. 게임에 **BepInEx 6 Unity IL2CPP Windows x64**가 먼저 설치되어 있어야 합니다. 플러그인 ZIP에는 DLL 한 개만 들어 있으며 BepInEx는 포함하지 않습니다.

| Steam판 | 설치 폴더 | 실행 파일 |
| --- | --- | --- |
| 글로벌판 | `8vJXnINT` | `AstralParty_INT.exe` |

### 1. BepInEx 설치와 게임용 설정

1. 게임을 종료하고, Steam 라이브러리에서 아스트랄 파티를 우클릭해 **관리 → 로컬 파일 보기**를 선택합니다.
2. 위 표의 `8vJXnINT` 폴더로 들어갑니다. 게임 실행 파일이 있는 위치에 BepInEx와 플러그인을 설치합니다.
3. [BepInEx 다운로드](https://builds.bepinex.dev/projects/bepinex_be)에서 **Unity IL2CPP Windows x64** 배포물을 내려받아 압축 내용 전체를 이 위치에 복사합니다. 빌드 참조는 설치된 게임의 DLL을 사용하며 특정 BepInEx 빌드 번호를 고정하지 않습니다. 실제 게임에서 확인한 빌드는 `6.0.0-be.788`입니다.
4. **게임을 처음 실행하기 전에** `BepInEx/config/BepInEx.cfg`를 열고 아래 설정을 적용합니다. 폴더나 파일이 없다면 직접 만드세요. 파일 이름이 `BepInEx.cfg.txt`로 저장되지 않도록 확인하세요.

```ini
[Logging]
UnityLogListening = false

[Logging.Console]
Enabled = false

[Logging.Disk]
WriteUnityLog = false
```

기존 설정 파일이 있다면 위 세 항목만 바꾸세요. 같은 섹션과 항목을 중복으로 추가하지 않습니다. 이 설정은 아스트랄 파티의 BepInEx 초기화 문제를 피하기 위한 것입니다.

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

수동 설치할 때도 위 위치에 DLL 한 개를 넣으세요. Release에 함께 첨부된 `SHA256SUMS.txt`는 ZIP의 무결성 확인용이며 게임 폴더에 복사할 필요는 없습니다.

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

Windows x64에서 PowerShell 7, Git과 BepInEx가 초기화된 게임을 준비하고 저장소 루트에서 실행합니다.

```powershell
.\scripts\setup.ps1
```

`setup.ps1`은 게임의 참조 DLL을 `.work/refs`에 복사하고 `global.json`이 지정한 로컬 .NET SDK를 `.work/dotnet`에 준비합니다. 첫 SDK 설치에는 인터넷 연결이 필요합니다.

DLL만 필요하면 빌드 명령을, 릴리즈 파일도 필요하면 패키징 명령을 선택하세요. 기본 패키징은 항상 최신 소스를 빌드하므로 선행 빌드는 필요 없습니다.

```powershell
.\scripts\build.ps1             # DLL 한 개와 빌드 참조 기록 생성
.\scripts\package-release.ps1   # 소스 빌드 후 DLL·ZIP·체크섬 생성
```

빌드 결과는 `dist/AstralPartyChatPlugin.dll`, 릴리즈 파일은 `dist/release/vX.Y.Z/`에 생성됩니다. `VERSION`이 제품 버전의 유일한 기준이며 빌드와 패키징은 버전을 올리지 않습니다. 경로 옵션과 전체 검사 명령은 [개발 문서](docs/development.md), 기존 DLL을 사용하는 `-DllPath`와 버전 준비·GitHub Release 업로드는 [릴리즈 문서](docs/releases.md)에 있습니다.

- [개발 환경과 검증](docs/development.md)
- [로컬 빌드와 릴리즈](docs/releases.md)
- [코드 구조와 실행 흐름](docs/architecture.md)

## 라이선스

소스 코드와 문서는 [MIT 라이선스](LICENSE)로 배포합니다. 저작권 및 라이선스 고지를 유지하면 사용, 수정, 재배포와 상업적 이용이 가능합니다. 게임과 외부 의존성에는 각각의 라이선스가 적용됩니다.
