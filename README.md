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
| 맥 (macOS 14 이상, 애플 칩·인텔) | 1.6 | [AIUsage-mac-1.6.zip](https://github.com/TeddyBEARs56/ai-usage-pet/releases/download/mac-v1.6/AIUsage-mac-1.6.zip) | 압축을 풀고 「읽어 주세요 (Read me).txt」대로: 터미널에 `zsh ` 를 치고 `설치.sh` 를 끌어다 놓은 뒤 Enter |
| Windows 10·11 (64비트) | 1.3.2 | [AIUsage.exe](https://github.com/TeddyBEARs56/ai-usage-pet/releases/download/windows-v1.3.2/AIUsage.exe) | 실행 → 「Windows의 PC 보호」가 뜨면 **추가 정보 → 실행** → 「이 PC에 설치할까요?」 **예** |

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

- **Claude**: 세 곳(맥 판은 아래 두 곳)에서 읽고, 가장 나중에 받은 값을 쓴다. Claude Code 로그인 토큰은 읽지 않는다.
  - **`claude /usage`(Windows 1.3.2 부터)**: Codex 처럼 Claude Code 에게 직접 묻는다 — `claude -p "/usage" --output-format json --no-session-persistence` 를 창 없이 잠깐 띄운다(5초쯤). `/usage` 는 모델을 부르지 않는 Claude Code 안의 명령이라 한도를 쓰지 않고, 인증은 Claude Code 가 한다. 대화 기록도 남기지 않는다(`~/.cache/ai-usage/claude-usage` 폴더에서 띄우므로 `~/.claude.json` 에 그 폴더 항목 하나가 생긴다). 받은 값은 상태 표시줄 기록과 같은 모양으로 `~/.cache/ai-usage/claude/usage-command.json` 에 남겨 같은 규칙으로 합치고, 초기화 시각도 있다. 모델별 주간 한도(예: 「Current week (Fable)」)가 있으면 주간 아래에 그 모델 이름으로 한 줄 더 보인다(Windows 1.3.3 부터, 1시간 안에 받은 /usage 값일 때만).
    - **언제 묻나**: 앱을 켤 때, 이 PC 에서 Claude Code 가 응답했을 때(CLI·데스크톱 앱 Code 탭 모두 — `~/.claude/projects` 의 대화 기록이 바뀌는 것을 지켜본다: 응답이 끝나고 5초 잠잠하면, 긴 작업이면 30초마다, 두 번 사이는 1분 이상), 그리고 5분마다(claude.ai 웹·모바일·데스크톱 앱 Chat 처럼 이 PC 에 흔적이 없는 사용 몫). 창을 두 번 누르거나 「지금 새로고침」 하면 바로.
    - **어느 claude 를 쓰나**: `~\.local\bin\claude.exe`·npm 의 `claude.cmd`·PATH, 없으면 Claude 데스크톱 앱이 품은 것(`Claude-Data\claude_<id>\claude-code\<버전>\…\claude.exe`, 가장 높은 버전) — 그래서 데스크톱 앱만 써도 된다.
    - **안전장치**: 답에 모델을 부른 흔적(`num_turns`·비용 > 0)이 있으면 값을 쓰지 않고 다시는 묻지 않는다(`~/.cache/ai-usage/claude-usage-disabled` 를 남긴다 — 지우면 다시 묻는다).
    - **기록**: 값이 바뀌었거나 실패했을 때만 `~/.cache/ai-usage/claude-usage.log` 에 계기(시작·활동·5분·새로고침)와 함께 한 줄. 오른쪽 클릭 메뉴에 「Claude /usage 확인 <시각>」과 실패 이유, `AIUsageStatus.exe --print` 에 결과·걸린 시간·쓴 claude 경로가 나온다.
    - **끄기**: 레지스트리 `HKCU\Software\AIUsage` 의 `ClaudeUsage`(DWORD)=0, 또는 환경 변수 `AIUSAGE_CLAUDE_USAGE=0`.
    - 결과는 사람이 읽는 영어 문장(`Current session: 8% used · resets Nov 20, 3:10pm (Asia/Seoul)`)이라 Claude Code 업데이트로 바뀔 수 있다. 못 읽으면 「/usage 결과 형식을 읽지 못했습니다」로 알리고 나머지 두 출처만 쓴다.
  - **Claude Code 상태 표시줄**: 상태 표시줄이 받는 `rate_limits` 를 세션마다 `~/.cache/ai-usage/claude/<세션>.json` 에 남기고(맥은 앱의 `--statusline` 또는 `statusline.sh` 에 넣는 7줄, Windows 는 `AIUsageStatus.exe`), 창은 가장 최근 창(초기화 시각이 가장 늦은 것)에서 가장 많이 쓴 값을 쓴다. 초기화 시각이 있다.
  - **Claude 데스크톱 앱 기록**: 데스크톱 앱이 사용량을 서버에서 받을 때마다 남기는 `plan-usage-history.json`(맥 `~/Library/Application Support/Claude/`, Windows 는 요즘 판(Microsoft Store 판 포함) `%LOCALAPPDATA%\Claude-Data\claude_<id>\`, 옛 판 `%APPDATA%\Claude\`). 데스크톱 앱을 쓰는 동안에만 쌓이고(4.5분에 한 번까지), 초기화 시각이 없다. 데스크톱 앱은 트레이(메뉴 막대) 아이콘을 오른쪽 클릭한 뒤 24시간 동안만 사용량을 계속 받고, 그 밖에는 앱을 켤 때 한 번만 받아 기록이 멈춘다 — Windows 판은 그럴 때 오른쪽 클릭 메뉴에 그렇게 알린다. Claude Code 가 로그인한 조직(`~/.claude.json` 의 `oauthAccount.organizationUuid`)과 다른 계정의 기록은 쓰지 않는다. 상태 표시줄이 본 창이 그 기록 때도 열려 있었으면 그 초기화 시각을 이어 쓴다.
  - Windows 판은 `/usage` 로 5분마다 묻기 때문에 claude.ai 웹·모바일에서 쓴 양도 5분 안에 반영된다. 맥 판(아직 `/usage` 없음)은 Claude Code 도 데스크톱 앱도 한동안 안 쓰면 값이 멈춰 있다(「n분 전」 확인).
  - 값이 하나도 없으면 Claude 칸에 이유를 쓴다: Claude Code 없음 / 상태 표시줄 연결 안 됨 / 연결이 끊김(앱을 옮김 등) / 상태 표시줄은 도는데 한도 값이 안 옴(구독 로그인이 아님 등) / 아직 대화 전. 상태 표시줄은 돌 때마다 `~/.cache/ai-usage/claude-statusline.json` 에 그 시각과 한도 값이 왔는지 남긴다.
  - `/usage` 글과 두 파일 모두 공식 문서에 없는 형식이라 Claude Code·데스크톱 앱 업데이트로 바뀔 수 있다.
- **계정을 바꿨을 때**: 한도는 계정마다 따로라 지금 로그인한 계정 값만 쓴다.
  - Claude: 상태 표시줄이 한도를 남길 때 그때 Claude Code 에 로그인한 계정(`~/.claude.json` 의 `oauthAccount` 사용자 id|조직 id)을 `account` 로 함께 적고, 창은 지금 계정과 같은 것만 쓴다. 계정이 안 적힌 옛 기록(1.5 이전, 또는 직접 만든 `statusline.sh` 의 jq 줄)은 지금 계정이 적힌 기록이 하나도 없을 때만, 그것도 계정을 바꾼 뒤 쓰인 것만 쓴다. 바뀐 때는 `~/.cache/ai-usage/accounts.json` 에 남는다.
  - ChatGPT: Codex 기록 첫 줄의 `creator_account_id` 가 지금 로그인(`~/.codex/auth.json` 의 `tokens.account_id` 만 읽는다)과 같은 기록만 쓴다. 15초마다 보다가 계정이 바뀌면 앞 계정 값을 버리고 5분을 기다리지 않고 바로 서버에 다시 묻는다.
  - 그래서 계정을 바꾼 직후 Claude 칸은 새 계정으로 Claude Code 에서 한 번 대화할 때까지 비어 있을 수 있다(앞 계정 값을 보여 주지 않는다). 로그아웃 상태면 어느 계정 값도 쓰지 않는다.
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

Windows 판은 Windows 에서도 올릴 수 있다(같은 검사·같은 순서, `gh` 필요): `powershell -ExecutionPolicy Bypass -File windows\release.ps1 "바뀐 점"`. 맥 판은 맥에서만 만든다.

`release.sh` 는 커밋 안 한 변경이 있거나 push 전이거나, 같은 버전·더 낮은 버전이면 멈춘다. 앱은 버전이 높을 때만 바꾸므로 버전을 안 올리고 올리면 아무도 안 바뀐다. 시험할 때는 `AIUSAGE_UPDATE_FEED`(Releases 목록 주소)·`AIUSAGE_UPDATE_DELAY`(첫 확인까지 초)로 바꿀 수 있다. 맥은 터미널에서 `AIUsagePet --check-update`(보기만)·`--update`(바로 바꾸기)도 된다.
