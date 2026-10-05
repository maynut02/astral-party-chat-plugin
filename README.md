# Astral Party Chat Plugin

Astral Party Steam판의 인게임 파티 채팅 플러그인입니다.

## 기능

- 게임 방 ID와 Steam 닉네임 자동 감지
- 플레이 중 캐릭터와 P1~P4 순서 자동 감지
- `https://astral.maynutlab.com`의 기존 Party Chat API/WebSocket 사용
- 방/캐릭터 선택/플레이 화면에 채팅 버튼 표시
- 인게임 채팅 오버레이
- 한국어 IME 입력 및 조합 확정 후 Enter 한 번으로 전송
- 메시지 스크롤/드래그
- 플레이 화면의 게임 내 캐릭터 초상화 재사용
- 연결 끊김 시 자동 재연결
- 최초 HTTP 입장 실패와 일시적인 서버 저장소 장애도 자동 재시도
- 짧은 화면 전환 중 연결 유지, 긴 전환 후 채팅 화면이 감지되면 방 연결 복구
- 재접속 시 서버의 이전 접속 정리 대기 및 만료된 채팅방 재생성
- 방 전환 시 이전 연결의 메시지·상태·전송 오류 차단
- 참가 정보 변경 확인 응답 대기 및 응답 지연 시 재연결
- 최근 300개 메시지의 UI 행 재사용과 실제 텍스트 높이 기반 배치
- 읽기 영역의 스크롤바 클릭·드래그와 새 메시지 알림의 최신 위치 이동
- 입력 포커스를 유지하는 Enter 전송과 채팅 입력 중 게임 키보드 조작 차단
- 입력칸 클릭으로 커서 이동·드래그 선택, 포커스 해제 시 예약된 Enter 전송 취소

## 구조

```text
astral-party-chat-plugin/
├─ .github/workflows/
│  └─ ci.yml          # Checks: 공개 파일·클라이언트·설치·배포·버전 검사
├─ src/
│  ├─ AstralParty.Chat.csproj
│  ├─ AstralPartyChatPlugin.cs
│  ├─ AstralPartyChatPlugin.Input.cs
│  ├─ ChatModels.cs
│  ├─ GameSessionState.cs
│  ├─ GameChatRuntime.cs
│  ├─ GameChatRuntime.Lifecycle.cs
│  ├─ ChatOverlay.cs
│  ├─ ChatOverlay.Input.cs
│  ├─ ChatOverlay.Messages.cs
│  ├─ ChatOverlay.Scroll.cs
│  ├─ ChatOverlay.Assets.cs
│  ├─ ChatInputSubmitState.cs
│  ├─ MessagesIconData.cs
│  ├─ PartyProtocol.cs
│  ├─ PartyChatClient.cs
│  ├─ PartyChatClient.Connection.cs
│  ├─ PartyChatClient.Messages.cs
│  ├─ PartyChatClient.Presence.cs
│  └─ RemotePayload.cs
├─ scripts/
│  ├─ setup.ps1
│  ├─ sync-refs.ps1
│  ├─ reference-files.ps1
│  ├─ build.ps1
│  ├─ test.ps1
│  ├─ install.ps1
│  ├─ distribution-installer.ps1
│  ├─ check-public-files.ps1
│  ├─ release-version.ps1
│  └─ package-release.ps1
├─ tests/             # 게임·운영 서버 없이 실행하는 클라이언트·UI 회귀 검사
│  └─ scripts/
│     ├─ InstallTests.ps1
│     ├─ DistributionInstallerTests.ps1
│     └─ ReleaseVersionTests.ps1
├─ docs/install.txt   # 설치·제거·데이터 안내
├─ dist/              # 빌드 산출물, git 제외
└─ .work/refs/        # 게임에서 복사한 빌드 참조 DLL, git 제외
```

## 개발환경 준비

대상 게임의 BepInEx가 한 번 이상 초기화되어 `BepInEx/interop`가 생성되어 있어야 합니다.

PowerShell에서:

```powershell
cd C:\path\to\astral-party-chat-plugin
.\scripts\setup.ps1
```

`setup.ps1`은 다음을 준비합니다.

- 컴파일에 필요한 BepInEx/IL2CPP/Unity DLL을 `.work/refs`로 복사
- 시스템에 .NET SDK가 없어도 사용할 수 있도록 .NET 6 SDK를 `.work/dotnet`에 로컬 설치

SDK는 `global.json`과 `setup.ps1`에서 6.0.428로 고정합니다. `sync-refs.ps1`은 참조 DLL의 버전과 SHA-256을 `.work/refs/versions.json`에 기록합니다.

게임의 BepInEx 런타임과 맞추기 위해 .NET 6을 사용합니다. [.NET 6의 공식 지원은 종료되었으므로](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core) 런타임 업그레이드는 게임·BepInEx와 함께 검증해야 합니다. 플러그인의 빌드 대상만 바꾸어도 게임에 포함된 런타임이 갱신되지는 않습니다.

게임 바이너리와 참조 DLL, 로컬 SDK는 저장소에 포함하지 않습니다.

참조 DLL만 다시 갱신하려면:

```powershell
.\scripts\sync-refs.ps1
```

## 빌드

```powershell
.\scripts\build.ps1
```

`build.ps1`은 `.work/dotnet`의 로컬 SDK를 우선 사용하고, 없으면 시스템 `dotnet`을 사용합니다.

결과:

```text
dist\AstralParty.Chat.dll
```

로컬 참조 대신 다른 참조 루트를 사용하려면:

```powershell
.\scripts\build.ps1 -RefsRoot C:\path\to\BepInEx
```

## 설치

설치 ZIP을 받은 사용자는 전체 압축을 푼 뒤 `Install.cmd`를 더블클릭하면 됩니다. Windows에 포함된 PowerShell을 사용하므로 별도 SDK나 PowerShell 7 설치가 필요하지 않습니다. Steam 설치 경로와 라이브러리 목록·게임 manifest에서 게임을 찾으며 다른 드라이브에 설치한 경우도 검색합니다. 자동 검색에 실패하면 창에 게임 폴더 경로를 입력합니다. 게임 폴더는 `Astral Party` 또는 실행 파일이 있는 `8vJXnINT`를 선택할 수 있습니다.

게임을 종료하고 BepInEx IL2CPP가 먼저 설치·초기화된 상태에서 실행하세요. 설치기는 기존의 중복 채팅 DLL을 `%LOCALAPPDATA%/AstralPartyChat/plugin-backups/`에 백업한 뒤 새 DLL을 설치합니다. 설치에 실패하면 이전 DLL을 복구합니다. 쓰기 권한이 없다면 표시된 경로의 권한을 확인한 후 `Install.cmd`를 관리자 권한으로 실행할 수 있습니다.

소스에서 개발용 빌드를 설치하려면 게임이 종료된 상태에서:

```powershell
.\scripts\install.ps1
```

개발용 설치도 Steam 게임 경로를 자동으로 찾습니다. 직접 경로를 지정할 수도 있습니다.

```powershell
.\scripts\install.ps1 -GameRoot 'D:\SteamLibrary\steamapps\common\Astral Party'
```

또는 빌드와 동시에 설치:

```powershell
.\scripts\build.ps1 -Deploy
```

감지한 게임 아래의 설치 위치 예시:

```text
C:\Program Files (x86)\Steam\steamapps\common\Astral Party\8vJXnINT\BepInEx\plugins\AstralPartyChat\AstralParty.Chat.dll
```

## 검증

```powershell
.\scripts\test.ps1
.\scripts\build.ps1
```

회귀 검사는 실제 클라이언트 소스를 가짜 HTTP/WebSocket과 함께 실행합니다. 최초 연결 재시도, 방 전환 중 늦은 응답, 참가 정보 변경 확인·거절·시간 초과, 메시지 확인·중복 제거, 방 상태 전환, IME 제출을 확인합니다. UI 검사는 실제 메시지 행·입력·스크롤 코드를 가짜 Unity 객체로 실행해 행 재사용·참조 정리·높이·휠 이동·초과 입력 보존을 확인합니다. 외부 payload 검사에서는 API 응답 스트림·PNG 상한·취소·오류 재시도 분류를 확인합니다. 운영 서버나 게임에는 접속하지 않습니다.

실제 게임에서는 방→캐릭터 선택 전환의 연결 유지, 영문·한글 Enter 한 번 전송, 입력 중 게임 키보드 조작 차단, 스크롤바·새 메시지 알림을 별도로 확인하세요.

## 지원 환경

- Windows x64, Astral Party Steam판의 IL2CPP 실행 파일 `8vJXnINT`
- 로컬 게임 로그에서 확인한 Unity 2022.3.62f3
- 로컬 게임 로그에서 확인한 BepInEx 6.0.0-be.788, .NET 런타임 6.0.7
- 빌드 참조: Il2CppInterop.Runtime 1.5.3, HarmonyX 어셈블리 2.10.2

위 버전은 이번 작업의 참조 환경입니다. 새 플러그인의 인게임 실행 검증은 별도로 필요합니다. 게임 업데이트 후에는 `sync-refs.ps1`로 참조를 갱신하고 회귀 검사와 빌드를 실행하세요. 화면 계층이나 네이티브 필드 변경은 인게임 확인이 필요합니다.

## 서버

플러그인은 기존 운영 서버를 직접 사용합니다.

- HTTP: `https://astral.maynutlab.com/api/party/*`
- WebSocket: `wss://astral.maynutlab.com/ws/party`

WebSocket 연결 시 기존 서버의 Origin 검사를 통과하도록 `Origin: https://astral.maynutlab.com`을 명시합니다. 별도의 서버 변경은 필요하지 않습니다.

게임 방과 Steam 표시 닉네임이 감지되면 서버에 자동 입장합니다. 닉네임·방 번호·캐릭터·P1~P4 정보와 작성한 메시지가 서버로 전송됩니다. 방 번호를 아는 사용자가 같은 채팅방에 접근할 수 있으며, Origin 헤더는 Steam 계정 인증이나 비밀 키가 아닙니다.

서버 규약의 기준은 `astral-patch-site`의 `shared/types/party.ts`, `server/routes/ws/party.ts`, `server/utils/party-chat.ts`, `server/api/party/rooms.post.ts`입니다. `SET_CHARACTER`/`SET_ORDER`는 자신의 값이 일치하는 `PARTICIPANTS`로 확인하며 한 번에 하나씩 전송합니다. `ERROR.clientMessageId`는 해당 채팅의 실패 상태를 표시합니다. `NOT_IN_ROOM`이면 메시지 ID의 유무와 관계없이 방을 다시 확인하고 재연결합니다.

HTTP 408/409/429/5xx 및 일시적 연결·저장소 오류는 재시도합니다. 일반 재시도는 5초부터 최대 15초, 요청 제한은 최소 60초 및 더 긴 `Retry-After`를 따릅니다. 입력 오류는 같은 요청을 반복하지 않습니다. 채팅 전송 확인이 지연되면 원문에 상태를 표시하며 중복 전송을 피하기 위해 자동으로 다시 보내지 않습니다.

## GitHub Actions

`Checks` 워크플로는 브랜치 push·PR 또는 수동 실행 시 공개 파일, 클라이언트·오버레이, 플러그인 설치, 배포용 CMD 설치기, 버전 검사를 실행합니다.

## 수동 로컬 패키징

현재 버전은 `0.0.1`입니다. 버전의 기준은 `src/AstralPartyChatPlugin.cs`의 `PluginVersion` 한 곳이며, BepInEx 플러그인 정보·로그·HTTP User-Agent·DLL 제품/파일/어셈블리 버전이 이 값을 사용합니다. 제품·파일 버전은 `0.0.1`, 어셈블리 버전은 .NET의 네 자리 형식에 따라 `0.0.1.0`으로 표시됩니다.

게임 참조 DLL이 준비된 개발환경에서 빌드와 ZIP 패키징을 각각 실행합니다.

```powershell
.\scripts\build.ps1
.\scripts\package-release.ps1 -Tag v0.0.1
```

`release-version.ps1`은 빌드와 패키징의 버전 검증에 사용하는 헬퍼입니다. `build.ps1 -Version 0.0.1`처럼 버전을 명시할 수도 있지만 코드의 `PluginVersion`과 같아야 합니다. `-Version`이 코드 버전을 바꾸지는 않습니다. 패키징의 `-Tag`는 로컬 패키지의 버전과 ZIP 파일 이름을 지정하며 Git 태그를 생성하지 않습니다. DLL과 패키징 버전이 다르면 생성을 중단합니다.

패키지는 `dist/release/<태그>`에 생성됩니다. 버전마다 별도 폴더를 사용합니다.

```text
dist/release/v0.0.1/
├─ AstralParty.Chat.dll
├─ AstralParty.Chat-v0.0.1.zip
└─ SHA256SUMS.txt
```

설치 ZIP에는 플러그인 DLL과 설치기 `Install.cmd`가 포함됩니다. 사용자는 ZIP 전체를 풀고 `Install.cmd`를 실행하면 됩니다. 별도 DLL은 수동 교체용이고 `SHA256SUMS.txt`는 파일의 무결성 확인용입니다. 게임·Unity·IL2CPP 참조 DLL과 로컬 SDK, PDB, 내부 문서는 패키지에 포함하지 않습니다.

## 수동 Release와 버전 관리

초기 개발의 `0.x` 단계에서는 버그 수정은 `0.0.1 → 0.0.2`, 기능 추가나 큰 변경은 `0.0.1 → 0.1.0`처럼 올리는 방식을 사용합니다. 안정적인 첫 정식 버전은 `1.0.0`으로 정하고, 이후 호환되지 않는 변경은 major, 기능 추가는 minor, 버그 수정은 patch를 올립니다. 이미 공개한 버전의 파일을 변경할 때는 새 버전을 사용합니다. [SemVer 규칙](https://semver.org/lang/ko/)

배포 순서:

1. `PluginVersion`을 배포할 버전으로 변경합니다. 첫 배포는 현재 값인 `0.0.1`입니다.
2. 회귀 검사와 실제 게임 확인을 수행하고 `chore: 플러그인 버전을 0.0.1로 설정`처럼 커밋한 뒤 push합니다.
3. 커밋한 소스를 `build.ps1`로 빌드하고, 같은 버전으로 `package-release.ps1 -Tag v0.0.1`을 실행합니다.
4. GitHub의 새 Release에서 태그를 `v0.0.1`, 대상은 해당 소스 커밋, 제목은 `ChatPlugin v0.0.1`로 선택합니다.
5. `dist/release/v0.0.1/`의 파일을 직접 첨부하고 게시합니다. 일반 사용자는 ZIP 하나만 받으면 됩니다.

커밋 메시지의 `feat:`·`fix:`·`chore:` 타입은 변경 내역을 설명하는 용도입니다. 버전과 GitHub Release는 직접 정하며 커밋 메시지가 자동으로 버전을 올리지는 않습니다.
