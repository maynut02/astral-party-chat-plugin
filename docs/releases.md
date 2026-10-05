# 버전 관리와 수동 릴리즈

여러 커밋을 모아 릴리즈 준비 시 버전을 한 번 결정하고, 로컬에서 빌드한 파일을 GitHub Release에 직접 업로드합니다. GitHub Actions는 `Checks`만 수행합니다.

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

소스 변경을 먼저 커밋하고, GitHub에서 직접 만든 이전 릴리즈 태그를 가져옵니다.

```powershell
git fetch origin --tags
.\scripts\prepare-release.ps1 -Preview
.\scripts\prepare-release.ps1
```

`-Preview`는 파일을 바꾸지 않고 버전·태그·제목·커밋 수를 보여줍니다. 실제 실행은 필요한 경우 `VERSION`을 갱신하고 `.work/releases/<태그>/release-notes.md`에 설명 초안을 만듭니다.

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

[개발 문서의 검사와 실제 게임 확인](development.md#검증)을 수행합니다. `VERSION`이 변경되었다면 커밋하고 소스와 함께 push합니다. 첫 배포처럼 값이 그대로라면 버전만을 위한 커밋은 필요 없습니다.

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

미커밋 변경이 없는 상태에서 빌드하고, 위에 표시된 전체 커밋 SHA를 릴리즈의 소스 기준으로 사용합니다. 준비 명령도 `SourceCommit`을 출력합니다.

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

게임 참조 DLL, BepInEx 본체, SDK, PDB, 문서는 패키지에 넣지 않습니다. 별도 DLL은 수동 교체용이며 `SHA256SUMS.txt`는 ZIP과 DLL의 SHA-256을 기록합니다.

DLL만 필요할 때는 `build.ps1`을 실행합니다. 이미 만든 DLL을 그대로 패키징하려면 `-DllPath`를 지정합니다. 이 경우에는 빌드를 실행하지 않습니다.

```powershell
.\scripts\package-release.ps1 -DllPath '.\dist\AstralPartyChatPlugin.dll'
```

`-Tag v0.0.1`로 태그를, `-OutputRoot`로 출력 폴더를 지정할 수 있습니다. 자동 빌드 모드에서는 태그가 `VERSION`과 같아야 하며, `-GameRoot`·`-RefsRoot`를 빌드에 전달할 수 있습니다. `-DllPath` 모드에서는 지정한 DLL의 제품 버전과 태그가 같아야 합니다. 태그를 생략하면 두 모드 모두 `VERSION`에서 읽은 값을 사용합니다. 이 옵션은 Git 태그를 만들지 않습니다.

## 4. GitHub에 직접 게시

[새 Release 작성 화면](https://github.com/maynut02/astral-party-chat-plugin/releases/new)에서 다음 정보를 입력합니다.

| 항목 | 첫 배포 예시 |
| --- | --- |
| 태그 | `v0.0.1` |
| 제목 | `v0.0.1` |
| 설명 | `.work/releases/v0.0.1/release-notes.md`를 검토하고 붙여넣기 |
| 첨부 파일 | `dist/release/v0.0.1/`의 ZIP·DLL·SHA256SUMS.txt |

태그 대상은 실제로 빌드한 소스 커밋으로 지정하세요. 태그를 만든 뒤 브랜치에 새 커밋이 추가되어도 이미 만든 태그의 대상을 바꾸지 않습니다. 일반 사용자는 ZIP만 내려받으면 됩니다. 설치 안내는 [README](../README.md#설치)에 있습니다.

버전 준비·빌드·패키징 명령은 커밋, Git 태그 생성, push, GitHub Release 게시를 수행하지 않습니다. 다음 릴리즈 준비 전에 `git fetch origin --tags`를 다시 실행하세요.
