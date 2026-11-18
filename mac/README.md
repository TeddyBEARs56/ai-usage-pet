# AI 사용량 (맥)

Dock 에서 켜고 끄는 맥 앱. 창은 Codex 펫처럼 다른 창 위·모든 데스크톱·전체 화면 앱 위에 떠 있고, 눌러도 지금 쓰는 앱의 초점을 뺏지 않는다. 무엇을 보여 주고 값을 어디서 가져오는지는 [맨 위 README](../README.md).

## 켜고 끄기

- **켜기**: Dock 의 링 아이콘(또는 `~/Applications` 의 「AI 사용량」)을 누른다.
- **끄기**: Dock 아이콘 오른쪽 클릭 → 종료, 앱이 앞에 있을 때 ⌘Q, 또는 창 오른쪽 클릭 → 종료.
- **보일 칸 고르기**: 창(또는 Dock 아이콘) 오른쪽 클릭 → 「Claude 보이기」·「ChatGPT 보이기」·「세션 보이기」. 체크를 끄면 그 칸이 빠지고 창 높이가 줄어든다(아래 모서리 자리는 그대로). 고른 것은 기억하고, Claude·ChatGPT 를 둘 다 끌 수는 없다. ChatGPT 칸을 끄면 5분마다 codex 를 띄우지도 않는다.
- **칸 순서**: 같은 메뉴(창 위쪽 메뉴 막대에도 있다) → 「칸 순서」 → 세 칸을 놓는 여섯 가지 순서 중 하나(지금 것에 체크). 처음엔 「세션 → Claude → ChatGPT」. 바꿔도 창은 아래 모서리를 그대로 두고 높이만 맞춘다. 끈 칸은 빠지되 자리는 기억해서 다시 켜면 그 자리에 돌아온다. 오른쪽 클릭 메뉴 맨 위의 값 줄도 같은 순서. 고른 것은 앱 설정 `paneOrder` 에 남는다.

## 세션 목록

창 맨 위에 이 맥에서 터미널로 연 Claude Code·Codex 세션을 한 줄씩 보여 준다(3초마다). 세션이 늘면 창은 아래 모서리를 그대로 두고 위로 늘어난다. 위로 늘다가 화면 위 끝에 닿으면 넘친 만큼만 아래로 밀린다(기억한 자리는 그대로). 이름 앞 작은 점은 도구(주황 Claude, 초록 Codex).

| 표시 | 뜻 | 어떻게 아나 |
|---|---|---|
| 주황 ! 확인 기다림 | 권한 묻기 등에서 내 답을 기다림(Claude 만) | `~/.claude/sessions/<pid>.json` 의 `status: waiting` |
| 파랑 ◌ 진행 중 N분 | 일하는 중(동그라미가 돈다) | Claude `busy`·`shell`, Codex 기록 마지막이 `task_started` |
| 초록 ✓ 답 나옴 N분 전 | 답을 마치고 다음 지시를 기다림 | Claude `idle`, Codex 기록 마지막이 `task_complete` |
| 회색 ■ 멈춤 | Codex 가 끝 표시 없이 꺼졌거나 중단됨 | `turn_aborted`, 또는 진행 중인데 그 기록 파일을 연 codex 프로세스가 없음 |

- 순서: 확인 기다림 → 진행 중 → 답 나옴 → 멈춤, 같은 상태는 최근에 바뀐 것부터. 8개가 넘으면 「외 N개」.
- 이름: Claude 는 세션 이름(없으면 폴더), Codex 는 작업 폴더 이름. 길면 가운데를 줄인다. 전체 경로는 창 오른쪽 클릭 메뉴에 나온다.
- Claude 는 뒤에서 도는 명령(백그라운드 실행)이 있으면 답을 마친 뒤에도 `busy`·`shell` 로 남는다. 그래서 대화 기록(`~/.claude/projects/…/<세션>.jsonl`) 끝에 차례가 끝난 표시(`turn_duration`)가 있으면 「답 나옴」으로 본다. 오른쪽 클릭 메뉴에는 「뒤에서 명령이 아직 도는 중」이 붙는다.
- Claude 세션은 터미널을 닫을 때까지 보인다. 끝난 Codex 작업은 30분 뒤 빠지고, 같은 폴더에서 다시 돌리면 앞의 끝난 기록은 빠진다. Codex 가 스스로 띄운 하위 작업은 보이지 않는다.
- Claude 쪽 파일은 공식 문서에 없는 형식이라 Claude Code 업데이트로 바뀔 수 있다. 모르는 상태 값은 회색 「알 수 없음」으로 보인다.
- claude.ai 웹·ChatGPT 앱 안의 대화는 보이지 않는다.
- **로그인할 때 자동으로 열기**: Dock 아이콘 오른쪽 클릭(또는 창 오른쪽 클릭) → 「로그인할 때 자동으로 열기」. 켜지면 체크 표시가 붙고 시스템 설정 → 일반 → 로그인 항목에 들어간다. 다시 누르면 빠진다.

창은 끌어서 옮기고(위치 기억), 두 번 클릭하면 새로고침. 오른쪽 클릭 메뉴에 마지막 값·초기화 날짜·값의 출처, 세션별 상태와 폴더, 새로고침, 위치 초기화(메인 화면 오른쪽 위), 자동 실행, 종료가 있다.

## Claude 값이 안 나올 때

Claude 칸에 이유가 나온다(값을 어디서 읽는지는 [맨 위 README](../README.md#값의-출처두-판-공통)).

| Claude 칸 글 | 할 일 |
|---|---|
| Claude Code 에 연결되지 않았습니다 | 창(또는 Dock 아이콘) 오른쪽 클릭 → 「Claude Code 에 연결…」. 설치할 때 Claude Code 가 없었으면 이렇게 된다. 메뉴는 연결이 안 됐거나 끊겼을 때만 보인다 |
| Claude Code 연결이 끊겼습니다 | 상태 표시줄이 없는 앱 경로를 가리킨다(앱을 옮김 등). 앱이 켜질 때 묻지 않고 새 경로로 고치므로 보통은 앱을 다시 켜면 된다. 안 되면 위 메뉴 |
| Claude Code 가 한도 값을 보내지 않습니다 | 상태 표시줄은 도는데 `rate_limits` 가 안 온다. Claude 구독이 아닌 API 키·Console 로그인이거나 Claude Code 가 오래된 판 |
| Claude Code 에서 한 번 대화하면 표시됩니다 | 연결은 됐고 아직 대화 전 |

터미널에서는 `--print` 의 「출처 · Claude Code 연결」 줄과 「상태 표시줄 마지막 실행」 줄로 같은 것을 본다. `--connect` 는 메뉴의 「Claude Code 에 연결」과 같다. 프로젝트 폴더의 `.claude/settings.json` 에 다른 상태 표시줄이 있으면 그 폴더에서는 그쪽이 우선한다(이 앱은 사용자 설정만 본다).

## 파일

| 파일 | 내용 |
|---|---|
| `AIUsagePet.swift` | 앱 전체(값 읽기·화면·Dock 메뉴) |
| `Info.plist` | 앱 정보 |
| `AppIcon.icns` | 앱 아이콘(`icon/make-icons.sh` 로 만든다) |
| `build.sh` | 소스를 고쳤을 때만: 다시 빌드해 `~/Applications/AI 사용량.app` 에 두고 다시 띄운다(이 맥 전용) |
| `package.sh` | 다른 맥에 줄 설치 묶음 zip(애플 칩·인텔, macOS 14+) |
| `share/` | 묶음에 들어가는 설치·지우기 스크립트, 안내문, Claude Code 설정 도구(`claude-settings.js`, 앱 안 Resources 로 들어감) |

## 고친 뒤 다시 만들기

```bash
zsh ~/Documents/GitHub/ai-usage-pet/mac/build.sh
```

값만 확인(창 없이): `~/Applications/"AI 사용량.app"/Contents/MacOS/AIUsagePet --print`
창 모양을 그림으로: `… --snapshot ~/Desktop/ai-usage.png`
다른 홈 폴더로 시험(Claude Code·데스크톱 앱 파일 위치를 바꿔 볼 때): `AIUSAGE_HOME=/tmp/시험 … --print` — Windows 판과 같다.

## 다른 맥에 설치

```bash
zsh ~/Documents/GitHub/ai-usage-pet/mac/package.sh   # → ~/Downloads/AI사용량-mac.zip
```

zip 을 건네면 받는 쪽은 `읽어 주세요.txt` 대로 터미널에 `zsh ` + `설치.sh` 끌어다 놓기 → Enter. 설치가 하는 일:

1. 앱을 `~/Applications` 에 넣고 받은 파일 표시(quarantine)를 지운다 — 개발자 인증(공증) 없는 임시 서명이라 이게 없으면 「확인되지 않은 개발자」 창이 뜬다. `.command` 더블클릭 대신 `zsh 설치.sh` 로 돌리는 것도 같은 이유.
2. Claude Code 가 있으면 `~/.claude/settings.json` 의 `statusLine` 을 앱의 `--statusline` 으로 건다(jq 불필요, 0.01초). 이미 다른 상태 표시줄이 있으면 `~/.claude/ai-usage-statusline.sh` 로 감싸 원래 줄을 그대로 보이게 하고, 원래 설정은 `ai-usage-statusline.original.json` 에 남긴다. 고치기 전 파일은 `settings.json.bak-날짜`. JSON 이 깨져 있으면 손대지 않는다. 이미 이 앱이 걸려 있는데 경로가 다르면(다시 설치·옮김) 새 경로로 고친다. 앱 안에도 같은 `claude-settings.js` 가 들어 있어서 앱의 「Claude Code 에 연결」·켤 때 고치기가 이것을 쓴다.
3. 앱을 띄우고 지금 읽히는 값(`--print`)을 보여 준다.

`지우기.sh` 는 앱·`~/.cache/ai-usage`·앱 설정을 지우고 상태 표시줄을 설치 전으로 되돌린다.

codex 는 독립 설치·Homebrew·npm(nvm·volta·bun 포함)·ChatGPT 앱에 들어 있는 것 순서로 찾는다. npm 판은 node 스크립트라 codex 가 있는 폴더를 PATH 앞에 붙여 띄운다.

이 맥처럼 직접 만든 `~/.claude/statusline.sh` 를 쓸 때는 `input=$(cat)` 뒤에 아래 한 줄을 넣어도 된다.

```bash
printf '%s' "$input" | "$HOME/Applications/AI 사용량.app/Contents/MacOS/AIUsagePet" --statusline >/dev/null 2>&1
```

## 지우기

1. 자동 실행을 켰다면 메뉴에서 끈다. Dock 아이콘 오른쪽 클릭 → 옵션 → Dock 에서 제거.
2. 앱을 끄고 `~/Applications/AI 사용량.app` 과 `~/.cache/ai-usage` 를 휴지통으로.
3. `~/.claude/statusline.sh` 의 「AI 사용량 창」 부분(주석 포함 7줄)을 지운다. 고치기 전 원본: `~/.claude/statusline.sh.bak-20261026`.
