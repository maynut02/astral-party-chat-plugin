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
│  ├─ build.ps1
│  ├─ test.ps1
│  └─ install.ps1
├─ tests/             # 게임·운영 서버 없이 실행하는 회귀 검사
├─ docs/install.txt   # 설치 ZIP에 포함되는 설치 안내
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

게임이 종료된 상태에서:

```powershell
.\scripts\install.ps1
```

또는 빌드와 동시에 설치:

```powershell
.\scripts\build.ps1 -Deploy
```

기본 설치 위치:

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

## GitHub Actions와 Release

- 브랜치 push·PR: 공개 파일 검사와 게임 참조 없이 실행하는 클라이언트/UI 회귀 검사
- **Actions → Release → Run workflow**: 기본 브랜치에서 `patch / minor / major`를 선택하면 검사 → 빌드 → 태그 생성 → Release 게시
- 첫 Release는 `v1.0.7`, 이후 최신 `vX.Y.Z` 태그를 기준으로 올립니다. 제목은 `ChatPlugin v1.0.7` 형식이며 설치 안내·체크섬·빌드 링크·변경 내역을 자동으로 작성합니다.
- 직접 `vX.Y.Z` 태그를 push하는 배포도 지원합니다. DLL에는 태그 버전이 자동 반영됩니다.

GitHub-hosted Windows 러너를 사용합니다. 게임·Unity·IL2CPP 참조 DLL은 소스, Actions artifact, Release에 포함하지 않습니다. 최초 배포 전에 다음 설정을 준비합니다.

1. 게임에서 BepInEx를 한 번 초기화한 뒤 비공개 빌드 참조 ZIP을 만듭니다.

   ```powershell
   .\scripts\sync-refs.ps1
   .\scripts\export-refs.ps1
   ```

   생성된 `.work/astral-build-refs.zip`은 컴파일 참조 DLL 12개를 포함하므로 공개 저장소·Release·Actions artifact에 올리지 않습니다. 기존 ZIP을 갱신하려면 `export-refs.ps1 -Force`를 사용합니다.

2. ZIP을 접근 제한된 저장소에 보관하고 다운로드용 HTTPS 서명 URL을 발급합니다. URL은 리다이렉트 없이 ZIP을 직접 응답해야 합니다.
3. **Settings → Environments → release-build**에 다음 값을 설정합니다.

   | 종류 | 이름 | 값 |
   | --- | --- | --- |
   | Environment secret | `ASTRAL_REFS_URL` | ZIP의 HTTPS 다운로드 URL |
   | Environment variable | `ASTRAL_REFS_SHA256` | ZIP 생성 시 표시된 SHA-256 |

   허용하는 배포 브랜치·태그에 기본 브랜치(예: `main`)와 태그 `v*`를 추가합니다. 저장소의 Actions·태그 ruleset은 게시 job의 `GITHUB_TOKEN`으로 태그 생성과 Release 게시를 허용해야 합니다. 별도 PAT는 사용하지 않습니다.
4. 변경사항을 기본 브랜치에 커밋·push한 뒤 **Actions → Release → Run workflow**를 실행합니다. 참조 ZIP을 갱신하거나 URL이 만료되면 위 값도 갱신합니다.

이미 공개된 Release는 덮어쓰지 않습니다. 업로드 실패 시 **Re-run failed jobs**로 실패한 build/publish job을 재실행하여 같은 태그의 draft를 복구할 수 있습니다. 수동 배포의 **Re-run all jobs**는 버전이 다시 증가하지 않도록 차단합니다. checks job이 실패했다면 수정 후 새 수동 실행을 시작하세요. 기존 draft에 DLL·설치 ZIP·체크섬 외 첨부파일이 있으면 게시를 중단합니다.

로컬 패키징:

```powershell
.\scripts\build.ps1
.\scripts\package-release.ps1 -Tag v1.0.7
```

패키지는 `dist/release`에 생성됩니다. 다른 Release 버전으로 빌드하려면 `build.ps1 -Version 1.0.8`처럼 버전을 지정하고, 패키징 태그도 `v1.0.8`로 맞춥니다.
