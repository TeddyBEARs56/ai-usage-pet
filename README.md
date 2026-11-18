# AI 사용량

Claude 와 ChatGPT(Codex) 사용 한도를 화면 위에 계속 띄워 두는 작은 창. Codex 펫처럼 다른 창 위에 늘 떠 있고, 눌러도 지금 쓰는 프로그램의 초점을 뺏지 않는다. 맥과 Windows 판이 있다.

| 폴더 | 내용 | 사용법 |
|---|---|---|
| [`mac/`](mac/) | 맥 앱(Swift). Dock 에서 켜고 끈다 | [mac/README.md](mac/README.md) |
| [`windows/`](windows/) | Windows 앱(C# .NET 10, 맥에서 빌드). 작업 표시줄에서 켜고 끈다. 실행 파일은 Releases | [windows/README.md](windows/README.md) |
| [`icon/`](icon/) | 아이콘 원본 스크립트. 그림을 고치면 `zsh icon/make-icons.sh` 로 두 판의 아이콘을 한 번에 다시 만든다 | |

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
