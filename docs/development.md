# 개발 환경과 검증

사용자 설치는 [README](../README.md), 배포는 [로컬 빌드와 릴리즈](releases.md), 런타임 동작은 [코드 구조와 실행 흐름](architecture.md)을 참고하세요.

## 준비 사항

- Windows x64, PowerShell 7, Git
- Astral Party 글로벌 Steam판
- [README의 설치 안내](../README.md#설치)에 따라 설정하고 한 번 실행한 BepInEx 6 Unity IL2CPP Windows x64
- 게임 실행으로 생성된 `BepInEx/interop` 참조 DLL

프로젝트는 `net6.0`을 대상으로 빌드합니다. SDK는 `global.json`의 `6.0.428`과 `latestPatch` 정책을 사용합니다. BepInEx 참조의 특정 버전은 고정하지 않고 설치된 게임의 `core`·`interop` DLL을 사용합니다.

환경 준비·빌드·검사는 PowerShell 7에서 실행합니다. 릴리즈 준비·업로드와 공통 릴리즈 설명 helper는 Windows PowerShell 5.1 호환성을 유지합니다.

## 개발 환경 초기화

저장소 루트에서 실행합니다.

```powershell
.\scripts\setup.ps1
```

`setup.ps1`은 게임의 필수 참조 DLL을 `.work/refs`에 복사하고 `global.json`을 검증한 뒤 로컬 SDK를 `.work/dotnet`에 준비합니다. 호환되는 로컬 SDK는 재사용하며 첫 설치에는 인터넷 연결이 필요합니다. 시스템 SDK와 PATH는 변경하지 않습니다.

기본 게임 경로는 다음과 같습니다. `GameRoot`는 게임 실행 파일과 `BepInEx` 폴더가 있는 위치입니다.

```text
C:\Program Files (x86)\Steam\steamapps\common\Astral Party\8vJXnINT
```

다른 Steam 라이브러리를 사용하면 게임 경로를 지정합니다. 작업 폴더도 `-WorkRoot`로 바꿀 수 있으며 이후 빌드·패키징에 같은 값을 전달하세요.

```powershell
.\scripts\setup.ps1 -GameRoot 'D:\SteamLibrary\steamapps\common\Astral Party\8vJXnINT' -WorkRoot '.\.work'
```

게임이나 BepInEx가 바뀐 뒤 참조만 갱신하려면 다음 명령을 사용합니다. SDK를 다시 설치하지 않습니다.

```powershell
.\scripts\sync-refs.ps1 -GameRoot 'D:\SteamLibrary\steamapps\common\Astral Party\8vJXnINT' -WorkRoot '.\.work'
```

참조 목록·검증·복사는 `scripts/reference-files.ps1`, SDK 선택·설치는 `scripts/dotnet-sdk.ps1`에서 공유합니다. 필수 참조 전체를 검사한 뒤 복사하며, 실제 버전과 SHA-256은 `.work/refs/versions.json`에 기록합니다.

## DLL 빌드

```powershell
.\scripts\build.ps1
```

Release 설정으로 DLL 한 개와 참조 기록을 생성합니다.

```text
dist/
├─ AstralPartyChatPlugin.dll
└─ build-references.json
```

게임에서 확인할 때는 [README의 설치 위치](../README.md#설치)에 DLL을 복사하세요.

| 옵션 | 동작 |
| --- | --- |
| `-Version` | 생략하면 `VERSION`; 지정할 때도 같은 값이어야 함 |
| `-GameRoot` | 지정한 게임에서 참조 캐시 준비·갱신 |
| `-RefsRoot` | `core`와 `interop`를 바로 아래에 둔 참조 폴더 사용 |
| `-WorkRoot` | 로컬 SDK와 참조 캐시의 작업 폴더; 기본 `.work` |
| `-OutputRoot` | 빌드 산출물 폴더; 기본 `dist` |
| `-DotNetPath` | 사용할 `dotnet.exe` 지정 |

`-RefsRoot`가 가장 우선합니다. 생략하면 작업 폴더의 참조 캐시를 사용하며, `-GameRoot`를 직접 지정하거나 캐시가 없으면 게임에서 참조를 준비합니다. 실제 게임의 BepInEx 폴더도 직접 참조할 수 있습니다.

```powershell
.\scripts\build.ps1 -RefsRoot 'D:\SteamLibrary\steamapps\common\Astral Party\8vJXnINT\BepInEx'
```

`-DotNetPath`를 지정하면 해당 실행 파일만 검사합니다. 생략하면 작업 폴더의 `dotnet/dotnet.exe`, PATH의 `dotnet` 순으로 `global.json`을 만족하는 SDK를 선택합니다. 호환 SDK가 없다면 `setup.ps1`을 다시 실행하거나 경로를 지정하세요.

```powershell
.\scripts\build.ps1 -WorkRoot '.\.work' -DotNetPath 'D:\Tools\dotnet\dotnet.exe'
```

### 공통 MSBuild 설정

| 파일 | 역할 |
| --- | --- |
| `global.json` | 로컬 개발과 CI의 SDK 선택 기준 |
| `Directory.Build.props` | 명시한 `AstralRefsRoot`, 로컬 `.work/refs`, 게임의 `BepInEx` 순으로 참조 경로 선택 |
| `Directory.Build.targets` | `VERSION` 검증, DLL 버전 설정, `obj`에 `AstralBuildVersion.Value` 생성 |
| `VERSION` | 제품 버전의 유일한 기준 |

직접 `dotnet build`를 실행해도 버전 상수가 자동 생성됩니다. `-p:Version`이 `VERSION`과 다르면 빌드를 거부합니다. C#에 제품 버전을 직접 넣거나 생성된 코드를 수정하지 않습니다. 참조 DLL은 제품 빌드 출력에 복사하지 않습니다.

## 릴리즈 파일 생성

```powershell
.\scripts\package-release.ps1
```

기본 패키징은 최신 소스를 빌드하고 `dist/release/vX.Y.Z/`에 릴리즈 파일을 생성합니다. 선행 빌드는 필요 없으며 빌드가 실패하면 패키징도 중단합니다. `-GameRoot`·`-RefsRoot`·`-WorkRoot`·`-DotNetPath`를 내부 빌드에 전달합니다. 패키징의 `-OutputRoot`는 릴리즈 폴더이며 내부 빌드 결과는 기본 `dist/`에 보관합니다.

채팅 플러그인은 `-DllPath`를 지정하면 기존 DLL을 빌드 없이 패키징할 수 있습니다. 파일 구성·버전 선택과 업로드는 [릴리즈 문서](releases.md#3-로컬-빌드와-패키징)에 정리되어 있습니다.

## 검증

저장소 루트에서 CI의 `Checks`와 같은 전체 검사를 실행합니다.

```powershell
.\scripts\check.ps1
```

전체 실행기는 공개 파일, SDK·참조, 프로젝트 버전·MSBuild 버전, 패키징, 릴리즈 준비·업로드 검사와 `scripts/test.ps1`의 런타임 회귀 검사를 실행합니다. 런타임 검사만 필요하면 다음 명령을 사용합니다.

```powershell
.\scripts\test.ps1
```

채팅의 런타임 검사는 실제 클라이언트·상태·오버레이·외부 응답 처리 코드를 가짜 HTTP/WebSocket과 Unity 객체로 확인합니다.

검사는 게임이나 운영 서버에 접속하지 않으며 GitHub 업로드는 테스트용 CLI로 대체합니다. CI에는 게임 참조 DLL을 넣지 않습니다. 배포 전에는 초기화된 실제 게임의 참조로 DLL을 빌드하고 다음을 확인하세요.

- 방 → 캐릭터 선택 → 플레이 전환과 같은 방 복귀 중 연결 유지
- 방 변경 후 이전 방의 메시지·응답이 섞이지 않는지
- 영어·한국어 Enter 한 번 전송, 커서 이동·선택과 입력 중 게임 조작 차단
- 포커스 해제 후 게임 입력 복구, 스크롤바와 새 메시지 알림
- 연결 실패 후 재연결과 종료 시 UI·패치 정리

## 공개 파일과 서식

소스·테스트·스크립트·문서는 공개 저장소에 포함합니다. 게임·Unity·IL2CPP·BepInEx 참조 DLL과 SDK, 로그·자격 증명·개인 개발 기록은 제외합니다. `.work`·`dist`·`bin`·`obj`는 로컬 산출물입니다. `check-public-files.ps1`은 Git의 추적 파일과 무시되지 않은 새 파일을 검사합니다.

텍스트는 LF와 UTF-8을 사용합니다. C#·PowerShell·Python은 4칸, Markdown·YAML·XML·JSON은 2칸 들여씁니다. Windows PowerShell 5.1에서 한글 릴리즈 설명을 읽도록 `prepare-release.ps1`·`release-notes.ps1`만 UTF-8 BOM을 유지합니다.

## 커밋 메시지

`타입: 한글 메시지` 형식을 사용합니다. 예: `fix: 방 전환 후 채팅 연결 복구`. 버전 증가 규칙은 [릴리즈 문서](releases.md#커밋에-따른-버전-결정)에 있습니다.
