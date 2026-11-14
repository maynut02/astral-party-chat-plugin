# 로컬 빌드와 릴리즈

커밋을 모아 버전을 한 번 준비하고, 같은 소스 커밋에서 만든 설치 ZIP·업데이트 ZIP·서명된 업데이트 매니페스트·체크섬을 GitHub Release에 게시합니다. 공개한 Release는 설치된 플러그인이 다음 게임 시작 때 자동으로 설치하므로, 공개 전에 실제 게임에서 확인하세요. GitHub Actions는 `Checks`만 실행합니다. 환경 준비와 검사는 [개발 문서](development.md)에 있습니다.

## 버전의 기준

루트의 `VERSION`이 제품 버전의 유일한 기준입니다. MSBuild가 이 값을 검증하고 `obj`에 `AstralBuildVersion.Value`를 생성합니다. BepInEx 플러그인 정보와 DLL 제품·파일·어셈블리 버전, 패키지가 같은 값을 사용합니다. `X.Y.Z`의 어셈블리 버전은 `X.Y.Z.0`입니다.

빌드·패키징은 버전을 올리지 않습니다. `build.ps1`의 `-Version`과 MSBuild의 `-p:Version`은 `VERSION`과 같아야 합니다.

## 커밋에 따른 버전 결정

`prepare-release.ps1`은 HEAD에서 도달 가능한 가장 높은 정식 `vX.Y.Z` 태그 이후의 커밋을 분석합니다. 여러 변경 중 가장 큰 증가 종류를 한 번 적용합니다.

| 커밋 | 자동 증가 |
| --- | --- |
| `fix:`·`perf:`·`revert:` | patch |
| `feat:` | minor |
| 타입 뒤 `!`, 본문의 `BREAKING CHANGE:`·`BREAKING-CHANGE:` | `0.x`는 minor, `1.x` 이상은 major |
| `docs:`·`chore:`·`ci:`·`test:`·`refactor:` 등 | 증가 없음 |

예를 들어 `fix:` 세 개와 `feat:` 한 개는 minor를 한 번 증가시킵니다. 이전 릴리즈 태그가 없는 첫 배포는 현재 `VERSION`을 그대로 사용합니다.

커밋은 [Conventional Commits](https://www.conventionalcommits.org/en/v1.0.0/) 형식으로 작성하며 scope도 지원합니다. `perf:`·`revert:`의 patch 처리와 `0.x` 호환성 변경의 minor 처리는 두 플러그인의 공통 정책입니다. 병합 커밋은 제외하고 squash 커밋은 일반 커밋으로 분석합니다.

## 1. 버전 준비

소스 변경을 먼저 커밋하고 이전 릴리즈 태그를 가져옵니다. 저장소 루트에서 실행합니다.

```powershell
git fetch origin --tags
.\scripts\prepare-release.ps1 -Preview
.\scripts\prepare-release.ps1
```

`-Preview`는 파일을 바꾸지 않고 버전·태그·소스 커밋·커밋 수를 보여줍니다. 실제 실행은 필요한 경우 `VERSION`을 갱신하고 `.work/releases/vX.Y.Z/release-notes.md`에 설명 초안을 만듭니다.

공통 `scripts/release-notes.ps1`은 변경 사항에 커밋 제목과 7자리 SHA를 오래된 순서로 나열하고, 해당 플러그인의 설치·사용·업데이트 안내와 관련 링크를 덧붙입니다. 커밋 본문과 병합 커밋은 목록에서 제외합니다. 준비 명령과 업로더의 이력 자동 생성은 같은 생성기를 사용합니다.

같은 배포를 다시 준비해도 버전을 추가로 올리지 않습니다. 현재 `VERSION`이 마지막 태그보다 높으면 준비한 버전으로 판단하고, 추가 변경으로 더 큰 버전이 필요해지면 중단합니다. 문서만 새 버전으로 배포하거나 증가 종류·버전을 직접 선택할 때는 다음 중 하나를 사용합니다.

```powershell
.\scripts\prepare-release.ps1 -Bump patch
.\scripts\prepare-release.ps1 -Bump minor
.\scripts\prepare-release.ps1 -Bump major
.\scripts\prepare-release.ps1 -Version 1.0.0
```

`0.x`에서 `-Bump major`는 첫 정식 `1.0.0`으로 전환합니다. 준비 명령은 `VERSION` 이외의 미커밋 변경, shallow clone, 이전 태그와 커밋된 `VERSION`의 불일치, 버전 감소와 기존 태그 재사용을 거부합니다. 이미 공개한 파일을 교체하려면 새 버전을 준비하세요. 버전 형식은 [SemVer](https://semver.org/lang/ko/)를 따릅니다.

## 2. 검증과 버전 커밋

[개발 문서의 검사와 실제 게임 확인](development.md#검증)을 수행합니다. `VERSION`이 바뀌었다면 커밋하고 최종 소스 커밋을 `origin`에 push합니다.

```powershell
git add VERSION
git commit -m "chore: 릴리즈 버전 준비"
git push origin main
```

버전이 그대로라면 버전만을 위한 커밋은 생략합니다. 업로드에 사용할 최종 HEAD는 먼저 push해야 합니다. 준비 명령을 다시 실행하면 같은 버전으로 최종 커밋까지 설명 초안을 갱신합니다.

```powershell
.\scripts\prepare-release.ps1
git status --short
git rev-parse HEAD
```

미커밋 변경이 없는 상태에서 빌드하고 업로드까지 같은 HEAD를 유지하세요. 소스 커밋이 바뀌면 해당 커밋을 push하고 패키지를 다시 생성합니다.

## 3. 로컬 빌드와 패키징

```powershell
.\scripts\package-release.ps1
```

기본 패키징은 기존 DLL이 있어도 최신 소스를 빌드한 뒤 릴리즈 파일을 생성합니다. 선행 빌드는 필요 없으며 빌드 실패 시 예전 DLL로 패키징하지 않고 중단합니다. 기본 태그는 `VERSION`의 `vX.Y.Z`이고 기본 릴리즈 폴더는 `dist/release/<태그>/`입니다.

패키징은 서명 키와 설치 프로그램 소스를 먼저 확인하고, DLL의 어셈블리 이름과 제품 버전을 검사하며, 예상한 산출물 이외의 파일이 있는 출력 폴더를 거부합니다. 릴리즈 파일 전체를 작업 폴더에서 만들어 검증한 뒤 출력 폴더에 복사하므로, 빌드·서명·검증이 실패하면 이전 릴리즈 파일을 건드리지 않습니다. 출력 폴더의 파일이 다른 프로그램에 열려 있어 복사가 중간에 실패하면 파일이 서로 맞지 않을 수 있으니 닫고 다시 패키징하세요. 업로더는 맞지 않는 파일을 거부합니다.

`VERSION`이 `0.0.1`인 예시는 다음과 같습니다. 내부 빌드는 `dist/`의 DLL 한 개와 `build-references.json`을 갱신합니다.

```text
dist/release/v0.0.1/
├─ AstralPartyChatPlugin.dll
├─ AstralPartyChatPlugin-v0.0.1.zip
├─ update-v0.0.1.zip
├─ update.json
├─ update.json.sig
└─ SHA256SUMS.txt
```

| 파일 | 내용 |
| --- | --- |
| `AstralPartyChatPlugin-vX.Y.Z.zip` | 사용자가 내려받는 설치 ZIP. 고정한 BepInEx `6.0.0-be.788` 배포물, `BepInEx/config/BepInEx.cfg`, 플러그인 DLL, `install.bat`·`install.ps1`, `LICENSE-BepInEx.txt` |
| `update-vX.Y.Z.zip` | 설치된 플러그인이 내려받는 업데이트 ZIP. `BepInEx/plugins/AstralPartyChatPlugin/AstralPartyChatPlugin.dll` 한 개 |
| `update.json` | 버전, 업데이트 ZIP의 주소·크기·SHA-256, DLL의 크기·SHA-256, 최소 BepInEx 버전 |
| `update.json.sig` | `update.json` 바이트의 ECDSA P-256/SHA-256 서명(IEEE P1363, base64) |
| `SHA256SUMS.txt` | 두 ZIP과 `update.json`의 SHA-256 |
| `AstralPartyChatPlugin.dll` | 로컬 교체용. Release에는 첨부하지 않음 |

BepInEx 배포물은 처음 패키징할 때 `builds.bepinex.dev`에서 내려받아 크기와 SHA-256을 확인한 뒤 `.work/bepinex/`에 보관합니다. 게임 참조 DLL, SDK, PDB는 어느 ZIP에도 넣지 않습니다. 다른 버전을 패키징해도 이전 버전 폴더는 유지됩니다.

### 업데이트 서명 키

설치된 플러그인은 `src/UpdateManifest.cs`의 `PublicKey`로 `update.json`의 서명을 검증하고, 이 저장소의 해당 버전 Release 주소와 플러그인 DLL 한 개만 가리키는 매니페스트만 받아들입니다. 릴리즈 스크립트는 같은 상수를 읽어, 그 공개 키와 짝이 맞는 개인 키가 없으면 패키징을 시작하지 않습니다. 공개 키는 `astral-party-korean-plugin`과 같은 키입니다.

개인 키(암호화하지 않은 PKCS#8 PEM)는 `-SigningKeyPath`, 환경 변수 `ASTRAL_UPDATE_SIGNING_KEY`, `%USERPROFILE%\.astral-party\plugin-update-signing.pem` 순으로 찾습니다. 키는 저장소·릴리즈·로그에 넣지 않습니다. 키를 잃거나 교체하면 기존 설치본은 새 릴리즈를 검증하지 못하므로, 사용자가 설치 ZIP으로 한 번 직접 업데이트해야 합니다.

업데이트 ZIP의 이름(`update-vX.Y.Z.zip`), 매니페스트 형식과 Release 첨부 파일 이름은 설치된 플러그인이 그대로 확인합니다. 바꾸면 이전 버전의 자동 업데이트가 끊깁니다.

DLL만 필요하면 `build.ps1`을 실행합니다. 패키징의 `-GameRoot`·`-RefsRoot`·`-DotNetPath`는 내부 빌드에 전달하고, `-WorkRoot`는 내부 빌드와 BepInEx 보관·임시 작업 폴더에 함께 사용합니다. `-OutputRoot`는 릴리즈 폴더, `-Repository`는 매니페스트에 기록할 `owner/name`(기본 `maynut02/astral-party-chat-plugin`)을 지정합니다. 내부 빌드 결과는 기본 `dist/`에 보관합니다. 경로 옵션은 [개발 문서](development.md#dll-빌드)에 있습니다.

`-Tag v0.0.1`로 태그를 선택할 수 있으며 자동 빌드에서는 `VERSION`과 같아야 합니다. 이미 만든 DLL을 빌드 없이 패키징하려면 `-DllPath`를 지정합니다.

```powershell
.\scripts\package-release.ps1 -DllPath '.\dist\AstralPartyChatPlugin.dll'
```

기존 DLL 모드에서는 DLL의 제품 버전과 태그가 같아야 하며 `-GameRoot`·`-RefsRoot`·`-DotNetPath`를 함께 사용할 수 없습니다. 태그를 생략하면 `VERSION`을 사용합니다. 이 옵션은 Git 태그를 만들지 않습니다. 사용자 지정 릴리즈 폴더에서 업로드할 때는 업로더의 `-AssetRoot`에 같은 경로를 지정하세요.

## 4. GitHub Release에 업로드

### 준비와 계획 확인

[GitHub CLI](https://cli.github.com/)를 설치하고 저장소에 쓰기 권한이 있는 계정으로 최초 한 번 `gh auth login`을 실행합니다. 게시 대상 저장소는 Git의 `origin`에서 읽습니다. 미커밋 변경이 없는 최종 HEAD를 먼저 push하고 패키징한 뒤 실행하세요.

```powershell
.\scripts\upload-release.ps1 -Preview | Format-List
.\scripts\upload-release.ps1
```

업로더는 PowerShell 7.2 이상에서 실행하며 이미 만든 릴리즈 파일을 사용합니다. 시작할 때 설치된 플러그인과 같은 기준으로 서명, 매니페스트의 버전·주소·크기·SHA-256, 업데이트 ZIP의 내용, 체크섬 파일을 검증하고, 설치 ZIP에 설치 프로그램과 같은 DLL이 들어 있고 `BepInEx/plugins` 아래에 다른 파일이 없는지 확인합니다. 하나라도 맞지 않으면 GitHub를 호출하기 전에 중단합니다. `-Preview`는 GitHub 호출과 파일 변경 없이 로컬 계획을 출력하므로 원격 Release 상태는 실제 업로드에서 확인합니다. `Notes`는 본문의 출처, `NotesContent`는 전체 본문입니다.

```powershell
.\scripts\upload-release.ps1 -Preview | Select-Object -ExpandProperty NotesContent
```

| 옵션 | 동작 |
| --- | --- |
| `-Tag` | 기본 `VERSION`의 `vX.Y.Z`; 지정할 때도 같은 버전이어야 함 |
| `-AssetRoot` | 릴리즈 파일이 있는 폴더; 기본 `dist/release/<태그>` |
| `-NotesFile` | 지정한 설명 파일; 없으면 중단 |
| `-Draft` | 새 Release와 기존 draft를 공개하지 않음 |
| `-Preview` | 로컬 계획과 전체 본문 출력 |

설명은 명시한 `-NotesFile`, 현재 태그의 준비 파일 `.work/releases/<태그>/release-notes.md`, Git 이력 자동 생성 순으로 선택합니다. 이력 생성은 시작 시 확정한 HEAD에서 도달 가능한 태그 중 현재 버전보다 낮은 최고 `vX.Y.Z` 이후의 커밋을 사용합니다. 이전 태그가 없으면 전체 이력을 사용하고, 현재 버전의 태그와 병합 커밋은 제외합니다.

이력 생성에는 전체 Git 기록과 릴리즈 태그가 필요합니다. shallow clone에서도 명시한 파일이나 준비된 파일을 사용하면 이력 생성은 필요하지 않습니다. 본문은 읽거나 생성한 뒤 고정하며 새 Release에는 UTF-8 스냅샷을 `--notes-file`로 전달합니다. GitHub의 `--generate-notes`는 사용하지 않습니다.

### 게시와 재시도

새 Release는 draft로 만들고 다섯 첨부 파일이 모두 준비되면 공개합니다. 설치된 플러그인은 공개된 최신 Release의 `update.json`만 읽으므로 draft는 자동 업데이트에 영향을 주지 않습니다. draft로 유지하려면 `-Draft`를 지정하세요. 이미 공개된 Release의 공개 상태는 유지합니다. GitHub에서 prerelease로 표시한 Release도 최신 Release가 아니므로 자동 업데이트 대상이 아닙니다.

기존 Release에는 누락된 파일만 추가하며 설명은 덮어쓰지 않습니다. 같은 이름의 원격·로컬 파일은 SHA-256이 같으면 건너뛰고 다르면 중단합니다. 기존 태그가 다른 소스 커밋을 가리켜도 중단합니다.

중간에 실패하면 같은 HEAD와 로컬 파일을 유지한 채 같은 명령을 다시 실행하세요. 동일 파일은 건너뛰고 나머지만 업로드합니다. 계속 draft로 유지하려면 재시도에도 `-Draft`를 지정합니다.

### 수동 업로드

[새 Release 작성 화면](https://github.com/maynut02/astral-party-chat-plugin/releases/new)에서도 게시할 수 있습니다. 태그와 제목은 `vX.Y.Z`, 태그 대상은 먼저 push한 빌드 소스 커밋으로 지정합니다. 준비한 설명이나 Preview의 `NotesContent`를 붙여넣고 두 ZIP·`update.json`·`update.json.sig`·`SHA256SUMS.txt`를 이름을 바꾸지 않고 첨부한 뒤 공개하세요. 이미 만든 태그의 대상은 바꾸지 않습니다.

버전 준비·빌드·패키징은 커밋·Git 태그 생성·push·GitHub 게시를 수행하지 않습니다. GitHub Release의 생성·업로드·공개는 `upload-release.ps1`이 담당합니다. 다음 릴리즈 전에는 `git fetch origin --tags`로 태그를 갱신하세요.
