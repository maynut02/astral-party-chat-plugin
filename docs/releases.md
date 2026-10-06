# 로컬 빌드와 릴리즈

여러 커밋을 모아 릴리즈 준비 시 버전을 한 번 결정하고, 로컬에서 빌드·패키징한 파일을 `upload-release.ps1`로 GitHub Release에 업로드합니다. GitHub Actions는 `Checks`만 수행합니다.

## 버전의 기준

루트의 `VERSION` 파일이 유일한 제품 버전의 기준입니다. 초기 값은 `0.0.1`입니다. MSBuild가 이 값을 읽어 `obj`에 `PluginVersion` 상수를 생성하므로 C#에 버전 숫자를 직접 넣지 않습니다. BepInEx 플러그인 정보, 로그, HTTP User-Agent, DLL 제품·파일·어셈블리 버전이 이 값을 사용합니다.

예를 들어 `VERSION`이 `0.0.1`이면 제품·파일 버전은 `0.0.1`, .NET 어셈블리 버전은 네 자리 형식인 `0.0.1.0`입니다. 빌드와 패키징은 버전을 올리지 않습니다.

## 커밋에 따른 버전 결정

`prepare-release.ps1`은 현재 소스의 Git 기록에 포함된 가장 높은 `vX.Y.Z` 태그 이후부터 HEAD까지의 커밋을 분석합니다. 여러 변경 중 가장 큰 증가 종류를 한 번 적용합니다.

| 커밋 | 자동 증가 |
| --- | --- |
| `fix:`·`perf:`·`revert:` | patch |
| `feat:` | minor |
| `feat!:` 같은 타입 뒤 `!`, 또는 본문의 `BREAKING CHANGE:`·`BREAKING-CHANGE:` | `0.x`는 minor, `1.x` 이상은 major |
| `docs:`·`chore:`·`ci:`·`test:` 등 | 증가 없음 |

`fix:` 세 개와 `feat:` 한 개가 있으면 `0.0.1 → 0.1.0`으로 한 번 증가합니다. 마지막 릴리즈 태그가 없는 첫 배포에서는 커밋 종류와 관계없이 현재 `VERSION`을 그대로 사용합니다.

타입은 [Conventional Commits](https://www.conventionalcommits.org/en/v1.0.0/)를 따릅니다. `perf:`·`revert:`의 patch 처리와 `0.x` 호환성 변경의 minor 처리는 이 저장소의 정책입니다. 설명은 한글로 작성하며 `feat(chat): 새 메시지 알림 추가` 같은 scope도 지원합니다. 병합 커밋은 제외하고 실제 브랜치 커밋을 분석하며 squash 커밋은 일반 커밋으로 분석합니다.

## 1. 버전 준비

소스 변경을 먼저 커밋하고, GitHub의 이전 릴리즈 태그를 가져옵니다.

```powershell
git fetch origin --tags
.\scripts\prepare-release.ps1 -Preview
.\scripts\prepare-release.ps1
```

`-Preview`는 파일을 바꾸지 않고 버전·태그·제목·커밋 수를 보여줍니다. 실제 실행은 필요한 경우 `VERSION`을 갱신하고 `.work/releases/<태그>/release-notes.md`에 설명 초안을 만듭니다.

설명 본문은 `## 변경 사항`부터 시작하며 범위 안의 모든 커밋 제목과 7자리 SHA를 오래된 순서대로 나열합니다. 커밋 본문과 병합 커밋은 항목에 포함하지 않습니다. 릴리즈 제목은 본문과 별도로 `vX.Y.Z`를 사용합니다.

`scripts/release-notes.ps1`의 공통 파서와 템플릿으로 설치 안내도 자동 생성합니다. 글로벌 Steam판의 설치 폴더·실행 파일 한 행, 게임 종료부터 BepInEx 사전 설치·README 설정 안내·ZIP 다운로드·폴더 합치기까지 1~4단계, DLL 한 개의 폴더 구조, 방 입장 후 채팅 버튼과 Enter 사용법을 포함합니다. 기존 `AstralParty.Chat.dll` 제거와 새 DLL 한 개 유지 안내 뒤에는 전체 변경 내역·설치 및 문제 해결·문제 제보 링크가 붙습니다.

같은 배포를 다시 준비하거나 버전 변경을 커밋한 뒤 다시 실행해도 버전을 추가로 올리지 않습니다. 현재 `VERSION`이 마지막 태그보다 높으면 이미 준비한 버전으로 판단합니다. 준비 이후 더 큰 변경이 추가되어 버전이 부족해지면 자동 진행을 중단합니다.

문서 변경만 배포하거나 증가 종류를 직접 고를 때는 다음 옵션을 사용합니다.

```powershell
.\scripts\prepare-release.ps1 -Bump patch
.\scripts\prepare-release.ps1 -Bump minor
.\scripts\prepare-release.ps1 -Bump major    # 0.x에서 첫 정식 1.0.0으로 전환
.\scripts\prepare-release.ps1 -Version 1.0.0
```

명령은 `VERSION` 이외의 미커밋 변경, 불완전한 shallow clone, 이전 태그와 그 커밋의 `VERSION` 불일치, 버전 감소 또는 기존 태그 재사용을 거부합니다. 이미 공개한 파일을 교체할 때는 새 버전을 준비하세요. [SemVer 규칙](https://semver.org/lang/ko/)

## 2. 검증과 버전 커밋

[개발 문서의 검사와 실제 게임 확인](development.md#검증)을 수행합니다. `VERSION`이 변경되었다면 커밋하고 소스와 함께 `origin`에 push합니다. 첫 배포처럼 값이 그대로라면 버전만을 위한 커밋은 필요 없지만, 업로드할 최종 HEAD 커밋은 먼저 push해야 합니다.

```powershell
git add VERSION
git commit -m "chore: 0.0.1 릴리즈 준비"
git push origin main
```

커밋 메시지의 버전은 실제 준비한 값으로 바꾸세요. 마지막으로 준비 명령을 다시 실행하면 버전은 유지하면서 최종 커밋까지 설명 초안을 갱신합니다.

```powershell
.\scripts\prepare-release.ps1
git status --short
git rev-parse HEAD
```

미커밋 변경이 없는 상태에서 빌드하고, 위에 표시된 전체 커밋 SHA를 릴리즈의 소스 기준으로 사용합니다. 준비 명령도 `SourceCommit`을 출력합니다. 빌드부터 업로드까지 같은 HEAD를 유지하세요. 소스 커밋이 바뀌면 변경한 커밋을 push하고 패키지를 다시 생성합니다.

## 3. 로컬 빌드와 패키징

```powershell
.\scripts\package-release.ps1
```

이 명령 하나로 최신 소스를 빌드한 뒤 릴리즈 파일을 생성합니다. `build.ps1`을 먼저 실행할 필요는 없습니다. 기존 DLL이 있어도 빌드를 다시 실행하며, 빌드가 실패하면 예전 DLL로 패키징하지 않고 중단합니다. 기본 태그는 `VERSION`에서 읽은 `vX.Y.Z`입니다. 예를 들어 다음 파일들이 생성됩니다.

```text
dist/release/v0.0.1/
├─ AstralPartyChatPlugin-v0.0.1.zip
├─ AstralPartyChatPlugin.dll
└─ SHA256SUMS.txt
```

ZIP에는 다음 DLL 한 개만 들어갑니다.

```text
BepInEx/plugins/AstralPartyChatPlugin/AstralPartyChatPlugin.dll
```

게임 참조 DLL, BepInEx 본체, SDK, PDB, 문서는 패키지에 넣지 않습니다. 별도 DLL은 로컬에서 수동 교체할 때 사용하는 산출물로 유지합니다. `SHA256SUMS.txt`에는 ZIP 한 개의 SHA-256만 기록하며, GitHub Release에는 ZIP과 `SHA256SUMS.txt`만 첨부합니다.

DLL만 필요할 때는 `build.ps1`을 실행합니다. 이미 만든 DLL을 그대로 패키징하려면 `-DllPath`를 지정합니다. 이 경우에는 빌드를 실행하지 않습니다.

```powershell
.\scripts\package-release.ps1 -DllPath '.\dist\AstralPartyChatPlugin.dll'
```

`-Tag v0.0.1`로 태그를, `-OutputRoot`로 출력 폴더를 지정할 수 있습니다. 자동 빌드 모드에서는 태그가 `VERSION`과 같아야 하며, `-GameRoot`·`-RefsRoot`를 빌드에 전달할 수 있습니다. `-DllPath` 모드에서는 지정한 DLL의 제품 버전과 태그가 같아야 합니다. 태그를 생략하면 두 모드 모두 `VERSION`에서 읽은 값을 사용합니다. 이 옵션은 Git 태그를 만들지 않습니다.

## 4. GitHub Release에 업로드

### 최초 한 번: GitHub CLI 설치와 로그인

[GitHub CLI](https://cli.github.com/)를 설치한 뒤 PowerShell에서 다음 명령으로 로그인합니다. 릴리즈를 게시할 저장소에 쓰기 권한이 있는 계정을 사용하세요. [공식 로그인 안내](https://cli.github.com/manual/gh_auth_login)

```powershell
gh auth login
```

### 로컬 계획 확인과 게시

저장소 루트에서 실행합니다. `upload-release.ps1`은 빌드하지 않고 이미 생성한 패키지를 업로드합니다. 게시 대상 `owner/repo`는 Git의 `origin`에 설정된 GitHub URL에서 추출하며, 미커밋 변경이 없는 HEAD를 소스 기준으로 사용합니다. 실제 업로드 전에 해당 커밋을 `origin`에 push해야 합니다. 업로드 명령이 소스를 커밋하거나 push하지는 않습니다.

```powershell
.\scripts\upload-release.ps1 -Preview
.\scripts\upload-release.ps1
```

`-Preview`는 저장소·태그·소스 커밋·첨부 파일·설명 등 로컬 계획만 출력합니다. `Notes`는 선택한 파일 경로 또는 `Git commit history`, `NotesContent`는 실제로 사용할 전체 본문입니다. 파일이나 스냅샷을 생성하지 않고 GitHub 호출도 하지 않으므로 원격 Release와 첨부 파일의 상태는 조회하지 않습니다.

기본 태그와 제목은 `VERSION`에서 읽은 `vX.Y.Z`입니다. 첨부 파일은 `dist/release/<태그>/`의 `AstralPartyChatPlugin-<태그>.zip`과 `SHA256SUMS.txt` 두 개뿐입니다. 설명은 명시한 `-NotesFile`을 먼저 사용하고, 생략하면 `.work/releases/<태그>/release-notes.md`를 사용합니다. 명시한 파일이 없으면 중단합니다. 두 파일 선택이 모두 없으면 로컬 Git 기록과 준비 명령의 공통 템플릿으로 본문을 생성합니다.

Git 기록으로 생성할 때는 업로드 시작 시 확정한 HEAD에서 도달 가능한 정식 `vX.Y.Z` 태그 중 `VERSION`보다 낮은 최고 버전을 골라 그 태그 이후부터 확정 HEAD까지 읽습니다. 현재 버전의 태그는 제외하며, 이전 태그가 없으면 전체 기록을 사용합니다. 병합 커밋은 제외하고 모든 커밋 제목과 7자리 SHA를 오래된 순서로 표시합니다. 이 방식에는 전체 Git 기록과 릴리즈 태그가 필요합니다. shallow clone이라도 명시한 파일이나 현재 태그의 준비된 설명 파일을 사용하면 기록 생성은 필요하지 않습니다.

본문을 읽거나 생성한 뒤 그 내용을 메모리에 고정합니다. 새 Release는 UTF-8 설명 스냅샷을 항상 `--notes-file`로 전달하며, GitHub의 `--generate-notes`를 사용하지 않습니다. 게시 전에 다음 명령으로 전체 본문을 검토할 수 있습니다.

```powershell
.\scripts\upload-release.ps1 -Preview | Select-Object -ExpandProperty NotesContent
```

새 Release는 draft로 생성하고 두 파일의 업로드가 모두 완료되면 공개합니다. `-Draft`를 지정하면 새 Release나 기존 draft를 공개하지 않습니다. 이미 공개된 Release에는 공개 상태를 유지하면서 파일만 추가합니다.

```powershell
.\scripts\upload-release.ps1 -Draft
```

| 옵션 | 기본값과 동작 |
| --- | --- |
| `-Tag` | `VERSION`의 `vX.Y.Z`; 지정할 때도 `VERSION`과 같은 버전이어야 함 |
| `-AssetRoot` | `dist/release/<태그>`; 업로드할 ZIP과 체크섬이 있는 폴더 |
| `-NotesFile` | 명시한 파일을 우선 사용; 생략하면 현재 태그의 준비 파일, 없으면 Git 기록과 공통 템플릿으로 생성 |
| `-Draft` | 새 Release와 기존 draft를 공개하지 않음 |
| `-Preview` | 파일 생성·GitHub 호출·게시 없이 로컬 계획과 `NotesContent` 전체 본문 출력 |

태그·파일 경로를 직접 지정하는 예시는 다음과 같습니다.

```powershell
.\scripts\upload-release.ps1 -Tag v0.0.1 -AssetRoot '.\dist\release\v0.0.1' -NotesFile '.\.work\releases\v0.0.1\release-notes.md' -Preview
```

### 기존 Release와 재시도

Release가 이미 있으면 본문은 유지하고 누락된 첨부 파일만 추가합니다. 새 `-NotesFile`을 지정하거나 Git 기록으로 본문을 생성해도 기존 Release의 본문을 덮어쓰지 않습니다. 같은 이름의 파일이 있으면 원격 파일과 로컬 파일의 SHA-256을 비교하여 동일할 때 건너뛰고, 다르면 덮어쓰지 않고 중단합니다. 파일을 수정해 교체하려면 새 버전을 준비하세요.

업로드가 중간에 실패하면 같은 HEAD와 로컬 파일을 유지한 채 같은 명령을 다시 실행할 수 있습니다. 이미 올라간 동일 파일은 건너뛰고 나머지만 업로드하며, draft는 두 파일이 준비된 뒤 공개합니다. 계속 draft로 유지하려면 재시도에도 `-Draft`를 지정하세요. GitHub CLI의 옵션은 [Release 생성](https://cli.github.com/manual/gh_release_create)과 [첨부 파일 업로드](https://cli.github.com/manual/gh_release_upload) 공식 문서를 참고하세요.

### 수동 업로드 대안

[새 Release 작성 화면](https://github.com/maynut02/astral-party-chat-plugin/releases/new)에서도 게시할 수 있습니다. 태그와 제목은 `vX.Y.Z`, 태그 대상은 먼저 push한 빌드 소스 커밋으로 지정하고, 준비한 설명 파일이나 업로드 Preview의 `NotesContent`를 붙여넣습니다. ZIP과 `SHA256SUMS.txt`만 첨부하고 두 파일을 확인한 뒤 공개하세요. 이미 만든 태그의 대상을 바꾸지 않습니다. 일반 사용자는 ZIP을 내려받아 [README의 설치 안내](../README.md#설치)를 따르면 됩니다.

버전 준비·빌드·패키징 명령은 커밋, Git 태그 생성, push, GitHub Release 게시를 수행하지 않습니다. GitHub Release의 생성·업로드·공개는 `upload-release.ps1`이 담당합니다. 다음 릴리즈 준비 전에 `git fetch origin --tags`를 다시 실행하세요.
