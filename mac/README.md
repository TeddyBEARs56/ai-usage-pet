# AI 사용량 (맥)

Dock 에서 켜고 끄는 맥 앱. 창은 Codex 펫처럼 다른 창 위·모든 데스크톱·전체 화면 앱 위에 떠 있고, 눌러도 지금 쓰는 앱의 초점을 뺏지 않는다. 무엇을 보여 주고 값을 어디서 가져오는지는 [맨 위 README](../README.md).

## 켜고 끄기

- **켜기**: Dock 의 링 아이콘(또는 `~/Applications` 의 「AI 사용량」)을 누른다.
- **끄기**: Dock 아이콘 오른쪽 클릭 → 종료, 앱이 앞에 있을 때 ⌘Q, 또는 창 오른쪽 클릭 → 종료.
- **로그인할 때 자동으로 열기**: Dock 아이콘 오른쪽 클릭(또는 창 오른쪽 클릭) → 「로그인할 때 자동으로 열기」. 켜지면 체크 표시가 붙고 시스템 설정 → 일반 → 로그인 항목에 들어간다. 다시 누르면 빠진다.

창은 끌어서 옮기고(위치 기억), 두 번 클릭하면 새로고침. 오른쪽 클릭 메뉴에 마지막 값·초기화 날짜, 새로고침, 위치 초기화(메인 화면 오른쪽 위), 자동 실행, 종료가 있다.

## 파일

| 파일 | 내용 |
|---|---|
| `AIUsagePet.swift` | 앱 전체(값 읽기·화면·Dock 메뉴) |
| `Info.plist` | 앱 정보 |
| `AppIcon.icns` | 앱 아이콘(`icon/make-icons.sh` 로 만든다) |
| `build.sh` | 소스를 고쳤을 때만: 다시 빌드해 `~/Applications/AI 사용량.app` 에 두고 다시 띄운다 |

## 고친 뒤 다시 만들기

```bash
zsh ~/Documents/GitHub/ai-usage-pet/mac/build.sh
```

값만 확인(창 없이): `~/Applications/"AI 사용량.app"/Contents/MacOS/AIUsagePet --print`
창 모양을 그림으로: `… --snapshot ~/Desktop/ai-usage.png`

## 다른 맥에 설치

1. 이 저장소를 받아 `zsh mac/build.sh` (Xcode 또는 `xcode-select --install` 필요).
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
