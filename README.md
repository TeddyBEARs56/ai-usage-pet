# AI 사용량

Claude 와 ChatGPT(Codex) 사용 한도를 화면 위에 계속 띄워 두는 맥 앱. **Windows 판은 [`windows/`](windows/README.md)**. Codex 펫처럼 다른 창 위·모든 데스크톱·전체 화면 앱 위에 떠 있고, 눌러도 지금 쓰는 앱의 초점을 뺏지 않는다.

## 켜고 끄기

- **켜기**: Dock 의 링 아이콘(또는 `~/Applications` 의 「AI 사용량」)을 누른다.
- **끄기**: Dock 아이콘 오른쪽 클릭 → 종료, 앱이 앞에 있을 때 ⌘Q, 또는 창 오른쪽 클릭 → 종료.
- **로그인할 때 자동으로 열기**: Dock 아이콘 오른쪽 클릭(또는 창 오른쪽 클릭) → 「로그인할 때 자동으로 열기」. 켜지면 체크 표시가 붙고 시스템 설정 → 일반 → 로그인 항목에 들어간다. 다시 누르면 빠진다.

## 보이는 것

| 칸 | 뜻 |
|---|---|
| Claude 5시간·주간 | 남은 비율과 초기화 시각(↺). 초록 50% 이상, 노랑 20% 이상, 빨강 20% 미만 — Claude Code 상태 표시줄과 같은 기준 |
| ChatGPT 주간 | ChatGPT 플랜의 Codex 한도. 플랜에 5시간 한도가 있으면(Plus 등) 5시간 줄이 생긴다 |
| 오른쪽 위 「방금·n분 전」 | 그 값을 마지막으로 받은 때 |

ChatGPT 대화(웹·앱) 자체의 한도는 밖에서 읽을 방법이 없어 Codex 한도만 보여 준다.

창: 끌어서 옮긴다(위치는 기억한다). 두 번 클릭 새로고침. 오른쪽 클릭 메뉴에 마지막 값·초기화 날짜, 새로고침, 위치 초기화(메인 화면 오른쪽 위), 자동 실행, 종료.

## 값의 출처

- **Claude**: Claude Code 상태 표시줄(`~/.claude/statusline.sh`)이 받는 `rate_limits` 를 세션마다 `~/.cache/ai-usage/claude/<세션>.json` 에 남기고, 앱은 가장 최근 창(초기화 시각이 가장 늦은 것)에서 가장 많이 쓴 값을 쓴다. 그래서 Claude Code 를 한동안 안 쓰면 값이 멈춰 있다(「n분 전」 확인). claude.ai·데스크톱 앱에서 쓴 양도 같은 한도에서 깎이고, 다음에 Claude Code 가 응답할 때 반영된다.
- **ChatGPT**: 5분마다 `codex app-server` 를 잠깐 띄워 `account/rateLimits/read` 로 묻는다(1초 정도, 인증은 codex 가 처리). Codex 를 쓰는 동안은 `~/.codex/sessions` 기록에 찍히는 값을 15초마다 읽는다. 둘 중 나중에 확인한 값을 쓴다.

## 파일

| 파일 | 내용 |
|---|---|
| `AIUsagePet.swift` | 앱 전체(값 읽기·화면·Dock 메뉴) |
| `Info.plist` | 앱 정보 |
| `AppIcon.icns` | 앱 아이콘(바깥 고리 Claude 주황, 안쪽 고리 ChatGPT 초록) |
| `make-icon.swift` | 아이콘 원본 그림(1024px PNG)을 그리는 스크립트 |
| `windows/` | Windows 판(같은 규칙, 맥에서 빌드) |
| `build.sh` | 소스를 고쳤을 때만: 다시 빌드해 `~/Applications/AI 사용량.app` 에 두고 다시 띄운다 |

## 고친 뒤 다시 만들기

```bash
zsh ~/Documents/GitHub/ai-usage-pet/build.sh
```

값만 확인(창 없이): `~/Applications/"AI 사용량.app"/Contents/MacOS/AIUsagePet --print`
창 모양을 그림으로: `… --snapshot ~/Desktop/ai-usage.png`

## 다른 맥에 설치

1. 이 저장소를 받아 `zsh build.sh` (Xcode 또는 `xcode-select --install` 필요).
2. Claude 값을 받으려면 `~/.claude/statusline.sh` 에서 `input=$(cat)` 뒤 아무 데나 아래를 넣는다(jq 필요). 상태 표시줄이 없으면 이 7줄만 있는 스크립트를 `settings.json` 의 `statusLine` 으로 걸어도 된다.

   ```bash
   # AI 사용량 창(~/Applications/AI 사용량.app)이 읽도록 세션별 사용 한도를 남긴다
   session_id=$(jq -r '.session_id // empty' <<<"$input")
   rate_limits=$(jq -c '.rate_limits // empty' <<<"$input")
   if [ -n "$session_id" ] && [ -n "$rate_limits" ]; then
     usage_dir="$HOME/.cache/ai-usage/claude"
     mkdir -p "$usage_dir" && printf '%s' "$rate_limits" > "$usage_dir/.$session_id.tmp" && mv -f "$usage_dir/.$session_id.tmp" "$usage_dir/$session_id.json"
   fi
   ```
3. ChatGPT 값은 Codex CLI 에 로그인만 돼 있으면 된다.

`build.sh` 로 만든 앱은 그 맥(CPU·macOS 버전) 전용이다. 다른 맥에 앱만 복사하려면 `-target arm64-apple-macos14.0`·`-target x86_64-apple-macos14.0` 으로 각각 빌드해 `lipo -create` 로 합친다.

## 지우기

1. 자동 실행을 켰다면 메뉴에서 끈다. Dock 아이콘 오른쪽 클릭 → 옵션 → Dock 에서 제거.
2. 앱을 끄고 `~/Applications/AI 사용량.app` 과 `~/.cache/ai-usage` 를 휴지통으로.
3. `~/.claude/statusline.sh` 의 「AI 사용량 창」 부분(주석 포함 7줄)을 지운다. 고치기 전 원본: `~/.claude/statusline.sh.bak-20261026`.
