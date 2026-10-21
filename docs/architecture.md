# 코드 구조와 실행 흐름

채팅은 게임 상태 감지, 네트워크 클라이언트, 오버레이로 나뉩니다. 설치는 [README](../README.md), 빌드·검증은 [개발 문서](development.md), 배포는 [릴리즈 문서](releases.md)를 참고하세요.

## 주요 구성

| 파일 | 역할 |
| --- | --- |
| `src/AstralPartyChatPlugin.cs` | BepInEx 진입점, Harmony 패치, 프레임별 상태·메시지·UI 연결, 종료 정리 |
| `src/AstralPartyChatPlugin.Input.cs` | 채팅 입력 중 게임이 Unity Input으로 직접 읽는 키보드 조작을 막는 패치 |
| `src/GameStateReader.cs`, `NativeGameData.cs` | 실제 게임 로직의 현재 방·로컬 플레이어·인게임 닉네임·캐릭터·순서 읽기 |
| `src/GameChatRuntime.cs`, `.Screen.cs`, `.Lifecycle.cs` | 게임 데이터 읽기, 현재 게임 패널과 채팅 버튼 기준 위치 감지, 런타임 수명 관리 |
| `src/GameSessionState.cs` | Unity와 분리된 방·화면 전환 상태 |
| `src/PartyChatClient.cs`, `.Connection.cs`, `.Messages.cs`, `.Presence.cs` | HTTP 입장 확인, WebSocket 연결·재시도, 메시지·참가 정보 처리 |
| `src/PartyProtocol.cs`, `ChatModels.cs` | 서버 규약의 입력 제한, 연결 옵션, 게임·UI 데이터 모델 |
| `src/FairyGui.cs`, `FguiNode.cs` | 게임의 FairyGUI를 IL2CPP 메타데이터로 호출하는 계층과 UI 객체 래퍼 |
| `src/ChatOverlay.cs`, `.Input.cs`, `.Messages.cs`, `.Scroll.cs`, `.Settings.cs`, `.Assets.cs` | 채팅 UI 구성, 포인터·입력, 메시지 행, 스크롤, 창·표시 설정, 초상화·글꼴 자원 |
| `src/ChatLayout.cs` | Unity와 분리된 창 위치·크기 제한과 설정값 계산 |
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

게임 정보는 Unity 스레드에서 0.25초마다 읽습니다. `AstralParty.Runtime`은 HybridCLR로 나중에 로드되므로 초기 참조 DLL의 CLR 타입 대신 IL2CPP 메타데이터와 BepInEx의 필드 읽기·메서드 호출 API를 사용합니다. 누락된 타입은 다시 탐색하며 메타데이터만 캐시합니다. 게임 객체 포인터는 저장하지 않습니다.

확인한 데이터 경로는 `GameLogicManager`의 싱글턴 → `room.roomController._LocalRoom`입니다. 방 ID는 `RoomInfo.Id`, 로컬 플레이어는 `RoomInfo.GetSelfInfo()`로 얻습니다. 이 메서드는 `AccountLogic.GetPlayerID()`로 플레이어를 찾습니다. 인게임 닉네임은 게임 UI와 동일한 `RoomPlayer.GetNick(false)`를 사용합니다. 선택 화면의 캐릭터는 `RoomInfo.GetHeroBarById(playerId).HeroId`, 플레이 중에는 `RoomPlayer.Hero.HeroId`를 읽습니다. 선택 응답은 HeroBar만 갱신하므로 두 경로를 구분합니다. 대기방은 이전 판의 캐릭터가 남아 있을 수 있어 미선택으로 표시합니다. 픽순은 `RoomPlayer.Slot`의 0~3을 P1~P4로 변환하며, 전투 중 순위나 변경된 행동 순서와 혼동하지 않습니다.

방·선택·플레이 여부는 `RoomController.roomStateType`을 기준으로 결정합니다. 게임의 `SyncRoomS2C` 처리도 이 방과 플레이어 데이터를 재구성하므로, 플러그인을 새로 시작해도 게임이 복원한 방에 연결할 수 있습니다. UI 이름·Steam 표시 이름 비교나 저장해 둔 방 번호 추정은 사용하지 않습니다. 게임의 명시적 방 이탈은 연결과 방 캐시를 정리합니다. 데이터가 준비되지 않았거나 읽기에 실패하면 확인 중 상태로 처리하며, 플레이어를 찾지 못했다는 이유만으로 관전으로 분류하지 않습니다. 관전은 `WatchLogic.PlayerIsWatcher()`로 확인합니다.

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

현재 클라이언트는 6자리 숫자 방 번호, 닉네임 최대 20자, 메시지 최대 1000자를 사용합니다. 문자 수는 Unicode 코드 포인트 기준입니다. 캐릭터 ID는 `101~129`, `301~306`, `unselected`(미선택) 또는 `spectator`(관전), 참가 순서는 `P1~P4`로 정규화합니다. 미선택 플레이어도 픽순을 전달하며 실제 관전만 순서가 없습니다. 이 규약은 서버에도 함께 적용해야 합니다. 게임의 캐릭터 목록이 늘어나면 양쪽 허용 목록을 갱신해야 합니다.

`SET_CHARACTER`와 `SET_ORDER`는 한 번에 하나씩 보내고, 자신의 값과 일치하는 `PARTICIPANTS` 응답으로 확인합니다. 관련 없는 참가자 변경을 자신의 갱신 성공으로 처리하지 않습니다. 확인 응답이 지연되면 연결을 복구합니다.

`ERROR.clientMessageId`가 있으면 해당 메시지의 실패 상태에 반영합니다. `NOT_IN_ROOM`은 메시지 ID 유무와 관계없이 방을 다시 확인하고 재연결합니다. 전송 확인 시간 초과는 원문에 상태를 표시하며, 중복 전송을 피하기 위해 메시지를 자동 재전송하지 않습니다.

HTTP 408/409/429/5xx와 일시적인 연결·저장소 실패는 재시도합니다. 일반 지연은 5초부터 최대 15초이며 요청 제한은 최소 60초와 더 긴 `Retry-After`를 따릅니다. 입력 오류는 같은 요청을 반복하지 않습니다.

전송 정보와 방 접근에 관한 사용자 안내는 [README의 사용 방법](../README.md#사용-방법)에 있습니다. 클라이언트 식별 헤더를 계정 인증으로 취급하지 않습니다.

## FairyGUI UI와 배치

채팅 UI는 별도 Unity Canvas가 아니라 게임이 쓰는 FairyGUI 객체로 만듭니다. FairyGUI도 HybridCLR의 `AstralParty.Runtime`에 있어 생성된 interop 래퍼가 없으므로, `FairyGui`가 IL2CPP 메타데이터에서 메서드를 **정확한 매개변수 타입**으로 찾아 호출합니다. 게임 업데이트로 시그니처가 바뀌면 잘못된 인자를 넘기지 않고 `MissingMethodException`으로 실패합니다. 플러그인이 만든 객체는 `FguiNode`가 IL2CPP GC handle로 유지합니다. 게임 객체는 프레임마다 다시 찾으며, 여러 프레임에 걸쳐 재사용하는 `ExpressionWindow`만 GC handle로 붙잡고 GRoot에서 빠지면 놓습니다.

게임은 `GRoot.SetContentScaleFactor(1920, 1080, MatchWidthOrHeight)`로 `scale = min(W/1920, H/1080)`을 적용합니다. GRoot의 좌표는 한 변이 1920 또는 1080으로 고정되고 다른 변은 화면 비율에 따라 늘어납니다. 채팅 UI의 크기는 이 GRoot 좌표로 정하므로 해상도·화면 비율·창 크기가 바뀌어도 게임 UI와 같은 배율을 따릅니다.

채팅 버튼은 `UIManager.currentPanel`의 FairyGUI 컴포넌트 안에 게임 버튼과 같은 부모로 넣습니다. 매 프레임 기준 버튼의 위치를 읽으므로 패널의 relation, 전환 애니메이션, 표시 순서와 가림을 그대로 따릅니다.

| 패널 | 기준 | 배치 |
| --- | --- | --- |
| `RoomWaitPanel` | `btn_Start` | 왼쪽 16 간격, 세로 중앙. 오른쪽은 방장 교체 카운트다운(`progress_KickMaster`) 자리입니다. |
| `RoomHeroPanel` | `btn_SureHero` | 버튼 영역 기준 (+154, -18). 이전 화면 비율 배치(0.953, 0.725)와 같은 위치입니다. |
| `BattleInfoPanel`, `HandCardPanel`, `BattleSettlementPanel` | `ExpressionWindow`의 `com_Interact_PC` | 보이는 버튼 열(`btn_OpenExpr`, `btn_OpenChat`, `btn_MapTool`, `btn_MapChat`)의 마지막 두 버튼 중심 간격으로 다음 칸 중심에 배치. 크기는 버튼 영역의 85%로, 투명 여백을 뺀 게임 버튼 그림 크기에 맞춥니다. |

기준 버튼 영역은 FairyGUI가 pivot을 중심으로 크기를 바꾸는 점을 반영해 계산합니다. 표정 버튼 열은 124×124 버튼을 pivot (1, 0.5), 배율 0.65로 쓰므로 `xMin`·`yMin`을 그대로 쓰면 실제 그림보다 왼쪽 위로 어긋납니다.

대전 중에는 `BattleLogic`이 `BattleInfoPanel` 다음에 `HandCardPanel`을 열어 현재 패널이 손패 패널이 됩니다. 플레이 기준은 패널이 아닌 `ExpressionWindow`이므로, 게임 데이터가 플레이 상태(`roomStateType` 3~5)이면 현재 패널 종류와 관계없이 플레이 화면으로 처리합니다.

기준 버튼을 찾지 못하면 GRoot에 이전 화면 비율 위치로 표시하고, 로그 `Chat button anchor:`에 `screen ratio`를 남깁니다. 단, 플레이 중에는 `ExpressionWindow`가 GRoot에 있고 `com_Interact_PC` 열이 화면에 표시된 상태에서 버튼 이름·배치를 해석하지 못한 경우에만 비율 위치를 사용합니다. 창이 사라지거나 열이 숨겨지면 채팅 버튼만 숨기며, 이미 열린 채팅창과 플레이 화면 판정은 유지합니다. 기준 버튼 이름과 위치는 게임 FairyGUI 패키지(`RoomWait_fui`, `RoomHero_fui`, `Expression_fui`)의 레이아웃과 실제 방 화면에서 읽은 값으로 확인했습니다. 버튼은 이름(`GetChild`)으로 찾으므로 패키지에 버튼이 추가되어 자식 순서가 바뀌어도 영향을 받지 않습니다.

채팅창은 GRoot의 최상위 자식(sortingOrder 900000)입니다. 창은 FairyGUI 히트 테스트에 잡히는 opaque 컴포넌트이므로 창 위의 포인터 대상은 아래 게임 UI가 아닌 채팅창이 됩니다. 확인한 게임 코드의 카메라 드래그·랜드 클릭 경로는 `Stage.isTouchOnUI`를 검사합니다. 실제 게임에서의 클릭·휠·월드 입력 확인은 [개발 문서의 검증 항목](development.md#검증)에 남아 있습니다. 별도 원시 마우스 입력 차단은 하지 않습니다. 포인터 처리는 FairyGUI의 터치 대상에서 부모를 따라 올라가 플러그인 컨트롤을 찾습니다. 위치는 GRoot 오른쪽 가장자리와의 거리와 세로 중앙 기준으로 `PlayerPrefs`의 v4 키에 저장하며, 이전 uGUI 좌표 키는 삭제합니다. 크기 조절 중에는 메시지 영역을 자르기만 하고, 놓았을 때 새 너비로 행을 다시 측정합니다. 화면 전환으로 크기 조절 도중 창이 닫혀도 변경한 너비의 행 측정을 마치므로 다시 열 때 이전 너비가 남지 않습니다.

## 입력과 메시지 UI

입력칸은 FairyGUI `GTextInput`입니다. FairyGUI는 포커스를 가진 입력칸에 키 이벤트를 보내고, 입력칸이 처리한 키는 전파를 멈추므로 게임 패널의 `Stage.onKeyDown` 단축키가 반응하지 않습니다. 게임 코드가 Unity `Input`을 직접 읽는 경로(카메라 이동, 표식 단축키 등)는 입력칸에 포커스가 있는 동안 Harmony 패치로 차단합니다. FairyGUI가 Ctrl·Shift 편집에 쓰는 `Input.GetKey`의 수정 키만 통과시킵니다. 포커스는 프레임마다 한 번 샘플링합니다.

FairyGUI의 한 줄 입력칸은 Enter를 처리하면서 포커스를 해제하고, IME 조합을 확정하는 Enter는 이벤트에서만 보일 수 있습니다. 그래서 `Input.GetKeyDown`과 `Event.current` 관찰로 Enter를 기록하고, `ChatInputSubmitState`에서 조합 확정 다음 프레임에 확정된 텍스트를 한 번 전송한 뒤 입력칸에 다시 포커스를 줍니다. Enter와 한 프레임 이내에 발생한 포커스 해제는 해당 Enter의 결과로 기록해 조합 확정이 늦어져도 제출을 취소하지 않습니다. 이후 별도의 포커스 상실 전환이나 입력칸·전송 버튼 밖의 클릭(왼쪽·오른쪽·가운데 버튼)은 예약된 Enter를 취소합니다. 전송 버튼 요청은 포커스 상실과 외부 클릭 뒤에도 유지됩니다. 창을 닫으면 숨겨진 입력칸의 포커스도 해제합니다.

메시지는 최근 300개를 유지합니다. UI 행을 재사용하고 실제 텍스트 높이로 배치하며, 스크롤 영역과 완전히 겹치지 않는 행은 숨깁니다. 숨긴 행도 너비 변경 때 다시 측정합니다. 한 줄 레이블은 주어진 영역 안에서 글자를 축소하고, 본문은 줄바꿈으로 높이를 늘립니다. 최신 위치를 읽고 있을 때는 새 메시지와 창 높이 변경을 따라 내려가며, 위로 스크롤한 경우 읽는 위치를 유지하고 **새로운 메시지** 버튼으로 이동합니다. 방 변경이나 재접속의 초기 히스토리를 새 메시지 알림과 혼동하지 않도록 스크롤 상태를 구분합니다.

메시지 식별자는 목록을 받을 때 한 번 계산해 읽지 않은 메시지 판정과 행 재사용에 공유합니다. 글자 배율 문자열도 렌더링 단위로 한 번만 만듭니다. 스크롤은 이전·새 행의 대응과 행 내부의 읽던 위치를 기준으로 보정하므로 참가자 정보 변경으로 행이 교체되거나 앞선 행의 높이가 바뀌어도 읽던 내용을 유지합니다. 중복 식별자는 기존처럼 입력 순서대로 대응합니다.

교체·제거된 행은 새 행의 생성·측정·배치가 모두 성공한 뒤 해제합니다. 도중에 네이티브 호출이 실패하면 기존 행과 새 행을 빠짐없이 정리하고 원래 예외를 전달합니다. 메시지 목록과 식별자 캐시는 유지하므로 다음 갱신에서 다시 그릴 수 있습니다.

캐릭터 초상화는 확인된 캐릭터 ID로 서버 자원을 가져옵니다. 닉네임이 같은 게임 UI 패널을 찾는 방식은 다른 플레이어의 이미지를 연결할 수 있어 사용하지 않습니다. 미선택·관전은 캐릭터 이미지가 없습니다. 외부 이미지와 응답은 `RemotePayload`의 제한을 통과해야 합니다. 표시용 텍스처를 직접 캐시하고 오버레이가 소유한 자원에 `DontUnloadUnusedAsset`을 지정해 화면 전환 때 유지합니다. 네이티브 텍스처가 삭제된 캐시 항목은 제거하고 다시 요청하며, 보호한 자원도 플러그인 종료 시 직접 정리합니다.

`FguiNode`는 Unity 텍스처 포인터마다 GC handle로 유지한 `NTexture` 하나를 여러 로더에서 재사용합니다. `destroyMethod=None`으로 설정해 FairyGUI는 재질만 정리하며 Unity 텍스처는 파괴하지 않습니다. 초상화가 제거될 때는 이를 표시하던 로더를 먼저 비우고 `NTexture`를 Dispose한 뒤 소유한 Unity 자원을 정리합니다. 종료 시에도 UI를 먼저 해제한 뒤 같은 순서로 정리합니다. 등록한 `DynamicFont`도 GC handle로 유지하며, 교체 글꼴을 등록한 뒤 이전 네이티브 글꼴을 Dispose합니다. 종료 시 현재 글꼴을 UnregisterFont·Dispose하되 게임 소유 Unity 글꼴은 파괴하지 않습니다.

패널 클래스 이름은 네이티브 클래스 포인터별로 캐시하고, 앵커 로그 문자열은 화면이나 기준이 바뀔 때만 만듭니다. 버튼 호스트는 매 프레임 게임에서 다시 조회한 주소가 같을 때만 기존 래퍼를 재사용합니다. 메타데이터 캐시와 로그 상태는 런타임 종료 시 초기화합니다.

## 변경 시 확인할 범위

게임의 화면 계층이나 네이티브 필드에 의존하는 감지는 업데이트에 영향을 받을 수 있습니다. 관련 변경은 [개발 문서의 실제 게임 검사](development.md#검증)까지 확인하세요. 서버 규약 변경은 가짜 서버 회귀 검사, 입력·레이아웃·스크롤 변경은 오버레이 검사, 외부 응답 처리는 payload 검사로 확인합니다.
