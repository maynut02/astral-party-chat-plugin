# 개발 환경과 검증

사용자 설치 방법은 [README](../README.md), 배포 절차는 [로컬 빌드와 릴리즈](releases.md)를 참고하세요.

## 준비 사항

- Windows x64와 PowerShell, Git
- Astral Party Steam판
- [README의 설치 안내](../README.md#설치)에 따라 설치하고 `BepInEx/config/BepInEx.cfg`를 설정한 BepInEx 6 Unity IL2CPP Windows x64
- 게임을 한 번 실행해 생성된 `BepInEx/interop` 참조 DLL

프로젝트는 게임의 BepInEx 런타임에 맞춰 `net6.0`을 대상으로 빌드합니다. 참조 환경은 Unity 2022.3.62f3, BepInEx 6.0.0-be.788, .NET 런타임 6.0.7, Il2CppInterop.Runtime 1.5.3입니다. 다른 버전이나 게임 업데이트 이후의 호환성은 실제 게임에서 확인해야 합니다.

## 개발 환경 초기화

저장소 루트에서 실행합니다.

```powershell
.\scripts\setup.ps1
```

이 명령은 게임의 BepInEx/Unity/IL2CPP 참조 DLL을 `.work/refs`로 복사하고, 필요한 .NET SDK를 `.work/dotnet`에 설치합니다. SDK 설치에는 인터넷 연결이 필요합니다. 참조 목록은 `scripts/reference-files.ps1`, 복사한 파일의 버전과 SHA-256 기록은 `.work/refs/versions.json`에 있습니다.

기본 게임 폴더는 다음 위치입니다.

```text
C:\Program Files (x86)\Steam\steamapps\common\Astral Party\8vJXnINT
```

다른 Steam 라이브러리에 설치했다면 실행 파일이 있는 폴더를 지정하세요.

```powershell
.\scripts\setup.ps1 -GameRoot 'D:\SteamLibrary\steamapps\common\Astral Party\8vJXnINT'
```

`GameRoot`는 `AstralParty_INT.exe`와 `BepInEx` 폴더가 있는 위치입니다. Steam에서 연 `Astral Party`의 상위 폴더와 혼동하지 마세요.

### global.json을 유지하는 이유

`global.json`은 로컬 개발과 CI가 사용하는 .NET SDK의 기준을 지정합니다. 현재 기준은 `6.0.428`이며 `latestPatch` 정책을 사용합니다. `setup.ps1`은 이 파일의 버전을 읽어 로컬 SDK를 준비하고, GitHub의 `Checks`도 같은 파일로 SDK를 선택합니다.

이 파일을 제거하면 PC마다 설치된 다른 SDK가 선택될 수 있습니다. 프로젝트의 대상 런타임인 `net6.0`과 SDK 선택은 별개이며, SDK나 대상 런타임을 바꿀 때는 테스트와 게임 실행을 함께 확인하세요. [Microsoft의 global.json 설명](https://learn.microsoft.com/en-us/dotnet/core/tools/global-json)

## 빌드와 참조 갱신

```powershell
.\scripts\build.ps1
```

결과는 `dist/AstralPartyChatPlugin.dll`입니다. 스크립트는 `.work/dotnet`의 SDK를 우선 사용하고, 없으면 시스템의 `dotnet`을 사용합니다. 빌드는 게임에 플러그인을 설치하지 않습니다. 테스트할 DLL은 [README의 설치 위치](../README.md#설치)에 직접 복사하세요.

게임 업데이트 후 참조만 갱신하려면 실행합니다.

```powershell
.\scripts\sync-refs.ps1 -GameRoot 'D:\SteamLibrary\steamapps\common\Astral Party\8vJXnINT'
```

`.work/refs` 대신 다른 참조 폴더를 사용하려면 `core`와 `interop`를 포함한 BepInEx 폴더를 지정합니다.

```powershell
.\scripts\build.ps1 -RefsRoot 'D:\SteamLibrary\steamapps\common\Astral Party\8vJXnINT\BepInEx'
```

로컬 참조가 없을 때 게임 경로로 빌드할 수도 있습니다.

```powershell
.\scripts\build.ps1 -GameRoot 'D:\SteamLibrary\steamapps\common\Astral Party\8vJXnINT'
```

`RefsRoot` 지정이나 기존 `.work/refs`가 게임 경로보다 우선합니다. 게임 업데이트 후에는 이전 로컬 참조를 갱신하세요.

## 릴리즈 파일 생성

```powershell
.\scripts\package-release.ps1
```

최신 소스를 빌드해 `dist/AstralPartyChatPlugin.dll`을 갱신하고, `dist/release/v버전/`에 DLL·ZIP·체크섬을 생성합니다. `build.ps1`을 먼저 실행할 필요는 없습니다. 기존 DLL이 있어도 빌드를 다시 실행하며, 빌드가 실패하면 패키징을 중단합니다.

별도 DLL은 로컬 산출물로 유지하고, `SHA256SUMS.txt`에는 ZIP 한 개의 SHA-256만 기록합니다. GitHub Release에는 ZIP과 `SHA256SUMS.txt`만 첨부합니다.

`build.ps1`과 같은 `-GameRoot`, `-RefsRoot` 옵션을 사용할 수 있습니다.

```powershell
.\scripts\package-release.ps1 -RefsRoot 'D:\SteamLibrary\steamapps\common\Astral Party\8vJXnINT\BepInEx'
```

이미 만든 DLL을 다시 빌드하지 않고 패키징하려면 경로를 명시합니다.

```powershell
.\scripts\package-release.ps1 -DllPath '.\dist\AstralPartyChatPlugin.dll'
```

`-DllPath`는 `-GameRoot`, `-RefsRoot`와 함께 사용할 수 없습니다. 버전 검증과 패키징 옵션은 [릴리즈 문서](releases.md#3-로컬-빌드와-패키징)를 참고하세요.

패키지를 생성한 뒤에는 `upload-release.ps1`로 업로드합니다. 이 명령은 빌드하지 않습니다. 실제 업로드 전에 GitHub CLI 설치·최초 로그인과 소스 커밋의 push를 마치고, 미커밋 변경이 없는 HEAD를 유지하세요.

```powershell
.\scripts\upload-release.ps1 -Preview
.\scripts\upload-release.ps1
```

`-Preview`는 파일 생성과 GitHub 호출 없이 로컬 계획과 `NotesContent`의 전체 본문을 확인하며, `-Draft`는 업로드 후 draft로 유지합니다. 본문은 명시한 설명 파일, 현재 태그의 준비된 파일, 로컬 Git 기록 순서로 선택합니다. 준비와 Git 기록 생성은 `scripts/release-notes.ps1`의 공통 파서·설치 템플릿을 사용합니다. Git 기록 생성에는 전체 이력이 필요합니다. 기본값과 기존 파일 비교·재시도·수동 업로드 대안은 [GitHub Release 업로드 절차](releases.md#4-github-release에-업로드)에 있습니다.

## 검증

전체 CI 검사를 로컬에서 실행하려면 PowerShell 7을 사용하세요. 릴리즈 준비·패키징·업로드 검사 스크립트는 PowerShell 7 이상을 요구합니다.

```powershell
.\scripts\check-public-files.ps1
.\scripts\test.ps1
.\tests\scripts\ProjectVersionTests.ps1
.\tests\scripts\ReleasePreparationTests.ps1
.\tests\scripts\PackageReleaseTests.ps1
.\tests\scripts\ReleaseUploadTests.ps1
```

위 명령은 CI의 `Checks`와 같은 검사입니다. `test.ps1`은 실제 클라이언트·UI·외부 응답 처리 코드를 가짜 HTTP/WebSocket과 Unity 객체로 검사하며, 운영 서버와 게임에는 접속하지 않습니다. 나머지 검사는 버전 형식과 증가, 커밋 기반 릴리즈 준비, 패키징의 자동 빌드 호출과 실패 처리, 기존 DLL 사용, ZIP 내용과 체크섬, 버전 불일치 거부와 재패키징을 확인합니다. 패키징 검사에서 빌드 호출은 게임 참조가 필요 없는 테스트용 스크립트로 대체합니다. 배포 전에 CI가 성공했는지 확인하고 로컬에서 실제 플러그인을 빌드하세요.

`ReleasePreparationTests.ps1`은 기존 버전 결정 검사와 함께 실제 커밋 제목·7자리 SHA, 글로벌 Steam판 설치 템플릿, Windows PowerShell 5.1의 한글 본문을 확인합니다. 한글과 폴더 구조가 들어 있는 공통 helper는 UTF-8 BOM과 LF로 저장하고, 준비 스크립트의 BOM도 유지하세요.

`ReleaseUploadTests.ps1`은 명령 조회가 fixture의 `gh.ps1`만 선택하는지 먼저 검사하며 GitHub에 실제 요청을 보내거나 릴리즈를 게시하지 않습니다. 로컬 계획, draft 생성 후 업로드·공개 순서, 동일 파일 건너뛰기, SHA-256 불일치 시 중단과 재시도를 확인합니다. 준비·Git 기록 생성의 본문 일치, 이전·현재·미병합 태그와 병합 커밋 제외, 파일 우선순위와 Preview 전체 본문, 새 본문의 스냅샷 전달과 기존 본문 유지도 검사합니다.

자동 검사만으로 게임 UI와 네이티브 입력 호환성을 보장할 수는 없습니다. 실제 게임에서는 다음을 확인합니다.

- 방 → 캐릭터 선택 → 플레이 전환과 같은 방 복귀 중 연결 유지
- 다른 방으로 이동한 뒤 이전 방 메시지가 섞이지 않는지
- 영어·한국어 Enter 한 번 전송과 입력 중 게임 버튼·키보드 조작 차단
- 입력칸의 커서 이동, 선택, 포커스 해제 후 게임 입력 복구
- 스크롤바 이동, 위로 스크롤한 상태의 새 메시지 알림과 최신 위치 이동
- 연결 실패 후 재연결과 플러그인 종료 시 UI·패치 정리

## 공개 파일 범위

소스·테스트·스크립트·이 문서들은 공개 저장소에 포함합니다. 게임·Unity·IL2CPP·BepInEx 참조 DLL과 SDK는 저장소에 올리지 않습니다. `.work`, `dist`, `bin`, `obj`는 로컬 산출물입니다. 로그·자격 증명·개인 개발 기록도 공개하지 않습니다.

`check-public-files.ps1`은 게시 후보에서 비공개 파일과 민감 정보의 흔적을 확인하는 보조 검사입니다. 새 외부 의존성이나 게임 바이너리를 추가할 때는 라이선스와 공개 범위를 직접 확인하세요.

## 커밋 메시지

`타입: 한글 메시지` 형식을 사용합니다. 예: `fix: 방 전환 후 채팅 연결 복구`. 버전 증가에 영향을 주는 타입과 배포 준비 방법은 [릴리즈 문서](releases.md)에 정리되어 있습니다.
