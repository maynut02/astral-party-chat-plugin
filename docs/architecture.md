# 코드 구조와 실행 흐름

채팅은 게임 상태 감지, 네트워크 클라이언트, 오버레이로 나뉩니다. 설치는 [README](../README.md), 빌드·검증은 [개발 문서](development.md), 배포는 [릴리즈 문서](releases.md)를 참고하세요.

## 주요 구성

| 파일 | 역할 |
| --- | --- |
| `src/AstralPartyChatPlugin.cs` | BepInEx 진입점, Harmony 패치, 프레임별 상태·메시지·UI 연결, 종료 정리 |
| `src/AstralPartyChatPlugin.Input.cs` | 채팅 입력 중 게임의 키보드 입력을 차단하는 패치 |
| `src/GameChatRuntime.cs`, `.Lifecycle.cs` | 게임 UI·IL2CPP 필드에서 방·닉네임·캐릭터·순서 감지, 초상화 캐시와 수명 관리 |
| `src/GameSessionState.cs` | Unity와 분리된 방·화면 전환 상태 |
| `src/PartyChatClient.cs`, `.Connection.cs`, `.Messages.cs`, `.Presence.cs` | HTTP 입장 확인, WebSocket 연결·재시도, 메시지·참가 정보 처리 |
| `src/PartyProtocol.cs`, `ChatModels.cs` | 서버 규약의 입력 제한, 연결 옵션, 게임·UI 데이터 모델 |
| `src/ChatOverlay.cs`, `.Input.cs`, `.Messages.cs`, `.Scroll.cs`, `.Assets.cs` | 채팅 UI, 입력, 메시지 행, 스크롤, 초상화 자원 |
| `src/ChatInputSubmitState.cs` | IME 조합 확정과 Enter 제출 상태 |
| `src/RemotePayload.cs` | 외부 응답 크기와 PNG 크기 제한 |
| `Directory.Build.props`, `Directory.Build.targets` | 참조 경로 선택, `VERSION` 검증과 `obj`의 버전 상수 생성 |
| `scripts/reference-files.ps1`, `scripts/dotnet-sdk.ps1` | 환경 준비와 빌드가 공유하는 참조·SDK 처리 |
| `scripts/build.ps1`, `scripts/package-release.ps1`, `scripts/package-files.ps1` | DLL 빌드, 공통 ZIP·체크섬 처리와 버전별 패키징 |
| `scripts/project-version.ps1`, `scripts/release-notes.ps1` | 버전 정책과 릴리즈 설명 생성 |
| `scripts/check.ps1`, `scripts/test.ps1`, `tests/` | 전체 검사와 런타임 회귀 검사 |

같은 이름의 점으로 구분된 C# 파일들은 partial 타입의 기능을 나눈 것입니다. 런타임 상태 감지, 네트워크 세션, UI를 분리하고 `ChatGameState`와 UI 스냅샷으로 연결합니다.

## 실행 흐름

플러그인은 `EventSystem.Update`의 Harmony prefix에서 프레임당 한 번 실행합니다. 게임 상태를 감지해 클라이언트에 전달하고, UI의 전송 대기 메시지를 클라이언트로 넘긴 다음 상태·메시지 스냅샷을 UI에 반영합니다. 메시지 revision이 바뀌었을 때 메시지 UI를 갱신합니다.

`GameSessionState`의 채팅 가능 화면은 방, 캐릭터 선택, 플레이입니다. 전환 중 화면 계층이 잠깐 사라지면 최대 15초간 확인된 방을 유지합니다. 더 긴 전환에서는 연결을 중단하되, 채팅 화면이 다시 나타나면 보관한 방 번호를 복구할 수 있습니다. 명시적인 방 이탈이나 방 변경은 이전 상태와 캐시를 정리합니다.

네트워크 작업은 세션과 연결의 취소 토큰 및 현재 세션 확인을 사용합니다. 이전 방의 늦은 HTTP/WebSocket 응답이 새 방의 메시지나 상태를 바꾸지 않도록 유지해야 합니다. 종료 시 네트워크, Harmony 패치, UI, 초상화 캐시를 함께 정리합니다.

## 외부 연동과 검증

기본 서버 주소는 `PartyChatOptions`, 입력 제한은 `PartyProtocol`에 있습니다.

| 용도 | 주소 |
| --- | --- |
| 방 생성·확인 | `POST https://astral.maynutlab.com/api/party/rooms` |
| 캐릭터 이름 목록 | `GET https://astral.maynutlab.com/api/party/characters` |
| 채팅 | `wss://astral.maynutlab.com/ws/party` |

서버 구현 저장소는 `astral-patch-site`입니다. 규약과 방·WebSocket 처리는 `shared/types/party.ts`, `server/api/party/rooms.post.ts`, `server/routes/ws/party.ts`, `server/utils/party-chat.ts`에서 확인합니다.

HTTP로 방 존재를 확인한 뒤 WebSocket으로 `JOIN`을 보냅니다. `JOINED`가 도착해야 전송 가능한 상태가 됩니다. 닉네임, 방 번호, 캐릭터와 순서를 서버에 전달하며, WebSocket에는 `Origin: https://astral.maynutlab.com`과 클라이언트 식별 헤더를 설정합니다. 이 헤더는 Steam 계정 인증이나 비밀 키가 아닙니다.

현재 클라이언트는 6자리 숫자 방 번호, 닉네임 최대 20자, 메시지 최대 1000자를 사용합니다. 문자 수는 Unicode 코드 포인트 기준입니다. 캐릭터 ID는 `101~129`, `301~306` 또는 `spectator`, 참가 순서는 `P1~P4`로 정규화합니다. 서버의 캐릭터 목록이 늘어나면 `PartyProtocol`의 허용 목록도 갱신해야 합니다.

`SET_CHARACTER`와 `SET_ORDER`는 한 번에 하나씩 보내고, 자신의 값과 일치하는 `PARTICIPANTS` 응답으로 확인합니다. 관련 없는 참가자 변경을 자신의 갱신 성공으로 처리하지 않습니다. 확인 응답이 지연되면 연결을 복구합니다.

`ERROR.clientMessageId`가 있으면 해당 메시지의 실패 상태에 반영합니다. `NOT_IN_ROOM`은 메시지 ID 유무와 관계없이 방을 다시 확인하고 재연결합니다. 전송 확인 시간 초과는 원문에 상태를 표시하며, 중복 전송을 피하기 위해 메시지를 자동 재전송하지 않습니다.

HTTP 408/409/429/5xx와 일시적인 연결·저장소 실패는 재시도합니다. 일반 지연은 5초부터 최대 15초이며 요청 제한은 최소 60초와 더 긴 `Retry-After`를 따릅니다. 입력 오류는 같은 요청을 반복하지 않습니다.

전송 정보와 방 접근에 관한 사용자 안내는 [README의 사용 방법](../README.md#사용-방법)에 있습니다. 클라이언트 식별 헤더를 계정 인증으로 취급하지 않습니다.

## 입력과 메시지 UI

Enter 처리는 일반 `InputField`의 편집 종료 동작을 가로채고, `ChatInputSubmitState`에서 IME 확정과 전송을 구분합니다. 포커스를 잃으면 예약된 제출을 취소합니다. 채팅 입력을 읽는 동안에는 입력 차단을 우회하고, 게임이 읽는 키보드 입력에는 차단을 적용합니다. 이 구분을 유지해야 한글 입력이 되면서 게임 버튼과의 Enter 상호작용을 막을 수 있습니다.

메시지는 최근 300개를 유지합니다. UI 행을 재사용하고 실제 텍스트 높이로 배치합니다. 최신 위치를 읽고 있을 때는 새 메시지를 따라 내려가며, 위로 스크롤한 경우 읽는 위치를 유지하고 **새로운 메시지** 버튼으로 이동합니다. 방 변경이나 재접속의 초기 히스토리를 새 메시지 알림과 혼동하지 않도록 스크롤 상태를 구분합니다.

캐릭터 초상화는 플레이 화면의 게임 자원을 재사용하거나 서버 자원을 가져옵니다. 외부 이미지와 응답은 `RemotePayload`의 제한을 통과해야 합니다. 게임 UI 자원은 방·화면 전환과 종료 시 정리해야 합니다.

## 변경 시 확인할 범위

게임의 화면 계층이나 네이티브 필드에 의존하는 감지는 업데이트에 영향을 받을 수 있습니다. 관련 변경은 [개발 문서의 실제 게임 검사](development.md#검증)까지 확인하세요. 서버 규약 변경은 가짜 서버 회귀 검사, 입력·레이아웃·스크롤 변경은 오버레이 검사, 외부 응답 처리는 payload 검사로 확인합니다.
