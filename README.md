# AI 사용량

Claude 와 ChatGPT(Codex) 사용 한도를 화면 위에 계속 띄워 두는 작은 창. Codex 펫처럼 다른 창 위에 늘 떠 있고, 눌러도 지금 쓰는 프로그램의 초점을 뺏지 않는다. 맥과 Windows 판이 있다.

| 폴더 | 내용 | 사용법 |
|---|---|---|
| [`mac/`](mac/) | 맥 앱(Swift). Dock 에서 켜고 끈다 | [mac/README.md](mac/README.md) |
| [`windows/`](windows/) | Windows 앱(C# .NET 10, 맥에서 빌드). 작업 표시줄에서 켜고 끈다. 실행 파일은 Releases | [windows/README.md](windows/README.md) |
| [`icon/`](icon/) | 아이콘 원본 스크립트. 그림을 고치면 `zsh icon/make-icons.sh` 로 두 판의 아이콘을 한 번에 다시 만든다 | |
| [`i18n/`](i18n/) | 화면 글자 번역(한국어·English·日本語·简体中文). 두 판이 같은 `strings.json` 을 쓴다 | [언어](#언어) |
| `release.sh` | 새 판을 Releases 에 올린다 → 깔린 앱이 알아서 받아 바꾼다 | [자동 업데이트](#자동-업데이트) |

## 받기

| | 버전 | 받기 | 설치 |
|---|---|---|---|
| 맥 (macOS 14 이상, 애플 칩·인텔) | 1.5 | [AIUsage-mac-1.5.zip](https://github.com/TeddyBEARs56/ai-usage-pet/releases/download/mac-v1.5/AIUsage-mac-1.5.zip) | 압축을 풀고 「읽어 주세요 (Read me).txt」대로: 터미널에 `zsh ` 를 치고 `설치.sh` 를 끌어다 놓은 뒤 Enter |
| Windows 10·11 (64비트) | 1.1.0 | [AIUsage.exe](https://github.com/TeddyBEARs56/ai-usage-pet/releases/download/windows-v1.1.0/AIUsage.exe) | 실행 → 「Windows의 PC 보호」가 뜨면 **추가 정보 → 실행** → 「이 PC에 설치할까요?」 **예** |

한 번 깔면 그 뒤로는 앱이 새 판을 알아서 받는다([자동 업데이트](#자동-업데이트)). 지난 판은 [Releases](https://github.com/TeddyBEARs56/ai-usage-pet/releases). 위 링크는 `release.sh` 가 새 판을 올릴 때 고친다.

## 보이는 것

| 칸 | 뜻 |
|---|---|
| Claude 5시간·주간 | 남은 비율과 초기화 시각(↺). 초록 50% 이상, 노랑 20% 이상, 빨강 20% 미만 — Claude Code 상태 표시줄과 같은 기준 |
| ChatGPT 주간 | ChatGPT 플랜의 Codex 한도. 플랜에 5시간 한도가 있으면(Plus 등) 5시간 줄이 생긴다 |
| 오른쪽 위 「방금·n분 전」 | 그 값을 마지막으로 받은 때 |
| 세션(맥 판만) | 이 맥에서 연 Claude Code·Codex 세션이 진행 중인지, 답을 마쳤는지, 내 확인을 기다리는지. 자세한 것은 [mac/README.md](mac/README.md#세션-목록) |

ChatGPT 대화(웹·앱) 자체의 한도는 밖에서 읽을 방법이 없어 Codex 한도만 보여 준다.

## 값의 출처(두 판 공통)

- **Claude**: 두 곳에서 읽고, 둘 중 나중에 받은 값을 쓴다. Claude Code 로그인 토큰은 읽지 않는다.
  - **Claude Code 상태 표시줄**: 상태 표시줄이 받는 `rate_limits` 를 세션마다 `~/.cache/ai-usage/claude/<세션>.json` 에 남기고(맥은 앱의 `--statusline` 또는 `statusline.sh` 에 넣는 7줄, Windows 는 `AIUsageStatus.exe`), 창은 가장 최근 창(초기화 시각이 가장 늦은 것)에서 가장 많이 쓴 값을 쓴다. 초기화 시각이 있다.
  - **Claude 데스크톱 앱 기록**: 데스크톱 앱이 사용량을 서버에서 받을 때마다 남기는 `plan-usage-history.json`(맥 `~/Library/Application Support/Claude/`, Windows `%APPDATA%\Claude\`). 데스크톱 앱을 쓰는 동안에만 쌓이고(4.5분에 한 번까지), 초기화 시각이 없다. Claude Code 가 로그인한 조직(`~/.claude.json` 의 `oauthAccount.organizationUuid`)과 다른 계정의 기록은 쓰지 않는다. 상태 표시줄이 본 창이 그 기록 때도 열려 있었으면 그 초기화 시각을 이어 쓴다.
  - 그래서 Claude Code 도 데스크톱 앱도 한동안 안 쓰면 값이 멈춰 있다(「n분 전」 확인). claude.ai 웹에서 쓴 양도 같은 한도에서 깎이고, 다음에 둘 중 하나가 값을 받을 때 반영된다.
  - 값이 하나도 없으면 Claude 칸에 이유를 쓴다: Claude Code 없음 / 상태 표시줄 연결 안 됨 / 연결이 끊김(앱을 옮김 등) / 상태 표시줄은 도는데 한도 값이 안 옴(구독 로그인이 아님 등) / 아직 대화 전. 상태 표시줄은 돌 때마다 `~/.cache/ai-usage/claude-statusline.json` 에 그 시각과 한도 값이 왔는지 남긴다.
  - 두 파일 모두 공식 문서에 없는 형식이라 Claude Code·데스크톱 앱 업데이트로 바뀔 수 있다.
- **ChatGPT**: 5분마다 `codex app-server` 를 잠깐 띄워 `account/rateLimits/read` 로 묻는다(1초 정도, 인증은 codex 가 처리). Codex 를 쓰는 동안은 `~/.codex/sessions` 기록에 찍히는 값을 15초마다 읽는다. 둘 중 나중에 확인한 값을 쓴다.

규칙을 바꿀 때는 두 판을 같이 고친다: 맥 `mac/AIUsagePet.swift`, Windows `windows/Shared/Usage.cs`.

## 언어

한국어·English·日本語·简体中文. 처음엔 맥·Windows 의 언어를 따르고(그 밖의 언어면 영어), 창 오른쪽 클릭 → 「언어 (Language)」 에서 고르면 기억한다. 창·메뉴·알림·Claude Code 상태 표시줄(이 앱을 상태 표시줄로 건 경우) 글자가 바뀐다. 맥은 Finder·Dock 의 앱 이름도 시스템 언어를 따른다(AI Usage · AI 使用量 · AI 用量). 맥 설치 묶음의 안내는 한국어·영어를 함께 쓴다.

- 번역은 [`i18n/strings.json`](i18n/strings.json) 하나: 코드가 쓰는 한국어 문장이 열쇠이고 `{0}` `{1}` 은 끼워 넣을 값. 맥은 앱 Resources 에, Windows 는 실행 파일 안에 들어간다.
- 문구를 고치거나 새로 넣으면 같은 열쇠로 `en`·`ja`·`zh-Hans` 를 함께 넣고 `zsh i18n/check.sh` 로 빠진 번역·`{0}` 자리를 확인한다. 번역이 없으면 영어, 그것도 없으면 한국어로 나온다.
- 고른 언어: 맥은 앱 설정 `language`, Windows 는 `%USERPROFILE%\.cache\ai-usage\language`(`auto` 또는 `ko`·`en`·`ja`·`zh-Hans`). 시험할 때는 `AIUSAGE_LANG=en` 으로 띄운다.
- 앱 파일 이름(`AI 사용량.app`)과 Windows 시작 메뉴 바로 가기 이름은 언어와 상관없이 그대로다(Claude Code 연결 경로·이미 깐 바로 가기가 어긋나지 않게).

## 자동 업데이트

앱은 켠 뒤 1분, 그 뒤 6시간마다 이 저장소의 Releases 를 본다(GitHub API, 로그인 없이). 자기 판보다 높은 판이 있으면 묻지 않고 받아서 바꾸고 다시 띄운다 — 창이 1~2초 사라졌다 같은 자리에 다시 뜬다. 메뉴 「업데이트 확인 (지금 <버전>)」을 누르면 바로 확인하고, 최신이면 그렇다고 알려 준다.

| | 맥 | Windows |
|---|---|---|
| 태그 · 올리는 파일 | `mac-v<버전>` · `AIUsage-mac-<버전>.zip`(설치 묶음 그대로) | `windows-v<버전>` · `AIUsage.exe` |
| 받은 뒤 검사 | SHA-256(GitHub 이 주는 값), 번들 id, 앱 버전 = 태그, 서명, `--version` 실행 | SHA-256, exe 파일 버전 = 태그 |
| 바꾸는 법 | 지금 앱을 같은 폴더 `.<이름>.app.old` 로 비키고 새 앱을 그 자리에 → 앱이 꺼지면 옛 것을 지우고 다시 띄움. 로그인 항목이었으면 새 앱이 다시 등록 | 받은 exe 를 `--apply-update <pid>` 로 띄움 → 지금 창이 꺼지면 받은 exe 가 설치 폴더에 자신을 복사하고 다시 띄움 |
| 바꾸지 않을 때 | `.app` 이 아니거나 쓸 수 없는 폴더·격리된 임시 위치에서 실행 중 | 설치 폴더(`%LOCALAPPDATA%\Programs\AIUsage`)가 아닌 곳에서 실행 중 |
| 끄기 | `defaults write com.local.ai-usage-pet autoUpdate -bool false` | 레지스트리 `HKCU\Software\AIUsage` 의 `AutoUpdate`(DWORD)=0 |
| 기록 | `~/.cache/ai-usage/update.log` | `%USERPROFILE%\.cache\ai-usage\update.log` |

맥 1.4·Windows 1.1.0 부터 들어 있다. 그 전 판은 한 번만 손으로 새 판을 깔면 된다.

**새 판 올리기**(맥에서): 버전을 올려 커밋·push 한 뒤

```bash
zsh release.sh mac "바뀐 점"        # mac/Info.plist 의 CFBundleShortVersionString(예 1.4 → 1.5)과 CFBundleVersion 을 먼저 올린다
zsh release.sh windows "바뀐 점"    # windows/AIUsage·AIUsageStatus 의 csproj <Version>(예 1.1.0 → 1.1.1)을 먼저 올린다
```

`release.sh` 는 커밋 안 한 변경이 있거나 push 전이거나, 같은 버전·더 낮은 버전이면 멈춘다. 앱은 버전이 높을 때만 바꾸므로 버전을 안 올리고 올리면 아무도 안 바뀐다. 시험할 때는 `AIUSAGE_UPDATE_FEED`(Releases 목록 주소)·`AIUSAGE_UPDATE_DELAY`(첫 확인까지 초)로 바꿀 수 있다. 맥은 터미널에서 `AIUsagePet --check-update`(보기만)·`--update`(바로 바꾸기)도 된다.
