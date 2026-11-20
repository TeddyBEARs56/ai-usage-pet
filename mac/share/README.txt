AI 사용량 (AI Usage) — 한국어 안내 아래에 English 가 있습니다. English instructions are below the Korean.

AI 사용량 — Claude·ChatGPT(Codex)의 남은 사용 한도를 화면 위에 띄워 두는 작은 창

━━ 이런 경우에 값이 나옵니다 ━━
• macOS 14 이상(애플 칩·인텔 모두)
• Claude 칸: Claude Code(터미널)를 Claude 구독(Pro·Max·Team)으로 로그인해 쓸 때,
  또는 Claude 데스크톱 앱을 쓸 때(앱을 쓰는 동안 받은 값만, 초기화 시각 없이)
  — claude.ai 웹만 쓰면 값이 나오지 않습니다.
  — 값이 안 나오면 Claude 칸에 이유가 나옵니다. 「연결되지 않았습니다」면
    창 오른쪽 클릭 → 「Claude Code 에 연결」.
• ChatGPT 칸: Codex 를 ChatGPT 계정으로 로그인해 쓸 때
• 하나만 쓰신다면 설치 뒤 안 쓰는 칸을 숨기면 됩니다(아래 「쓰는 법」).

━━ 설치 ━━
1. 이 폴더는 그대로 둡니다(다운로드 폴더 안이어도 괜찮습니다).
2. 터미널을 엽니다(⌘+스페이스 → 「터미널」 입력 → Enter).
3. zsh 를 치고 빈칸 하나를 띄운 뒤, 이 폴더의 「설치.sh」를 터미널 창으로 끌어다 놓고 Enter.
4. 화면 오른쪽 위에 창이 뜨면 끝입니다. Dock 에도 아이콘이 생깁니다.

━━ 쓰는 법 ━━
• 「한도 알림」은 처음에 켜져 있습니다. 한도가 10% 이하로 남으면 알립니다. 초기화 시각을 알면 그 시각부터 60분 안에 확인한 초기화도 알립니다. 창 오른쪽 클릭 메뉴에서 켜고 끕니다.
• 같은 메뉴의 「사용 추이…」에서 이번 주 사용 속도와 지난 4주 일별 사용량을 봅니다.
  현재 계정의 주간 한도를 보여 주며 기록은 ~/.cache/ai-usage/history.jsonl 에 35일간 남습니다.
• 창은 끌어서 옮기고, 두 번 클릭하면 새로고침합니다.
• 안 쓰는 칸 숨기기: 창(또는 Dock 아이콘) 오른쪽 클릭 → 「Claude 보이기」·「ChatGPT 보이기」를 눌러 체크를 끕니다.
  다시 누르면 다시 보입니다. 둘 다 끌 수는 없습니다.
• 언어 바꾸기: 창 오른쪽 클릭 → 「언어 (Language)」. 처음엔 맥의 언어를 따릅니다(한국어·영어·일본어·중국어 간체).
• 창을 오른쪽 클릭하고 「모양」에서 배경 투명도, 창 크기, 글자 크기를 고릅니다. 다음에 열 때도 그대로 적용됩니다.
• 칸 순서 바꾸기: 같은 메뉴의 「칸 순서」에서 고릅니다(세션·Claude·ChatGPT 를 놓는 순서).
• 컴퓨터를 켤 때마다 자동으로 열기: 같은 메뉴의 「로그인할 때 자동으로 열기」.
• 끄기: Dock 아이콘 오른쪽 클릭 → 종료.

━━ 새 버전 ━━
• 앱이 6시간마다 새 버전이 있는지 보고, 있으면 알아서 받아 바꿉니다.
  창이 1~2초 사라졌다가 같은 자리에 다시 뜨면 바뀐 것입니다. 다시 설치할 필요는 없습니다.
• 바로 확인하려면 같은 메뉴의 「업데이트 확인」을 누릅니다.

━━ 설치가 Claude Code 에 하는 일 ━━
• ~/.claude/settings.json 의 상태 표시줄(statusLine)에 이 앱을 연결합니다. 고치기 전 파일은 settings.json.bak-날짜 로 남깁니다.
• 쓰시던 상태 표시줄이 있으면 그대로 보이고, 사용 한도만 뒤에서 같이 남깁니다.
• 없었다면 Claude Code 아래쪽에 모델·컨텍스트·사용 한도 줄이 새로 보입니다.
• Claude Code 를 나중에 깔았다면 창 오른쪽 클릭 → 「Claude Code 에 연결」(설치를 한 번 더 해도 됩니다).
• 앱을 다른 폴더로 옮기면 앱이 다음에 켜질 때 연결을 새 자리로 고칩니다.

━━ 지우기 ━━
1. 「로그인할 때 자동으로 열기」를 켜 두셨다면 먼저 끕니다.
2. 터미널에 zsh 와 빈칸 하나를 치고, 「지우기.sh」를 끌어다 놓고 Enter.
   앱과 기록을 지우고 Claude Code 상태 표시줄을 설치 전으로 되돌립니다.


━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
AI Usage (AI 사용량) — a small window that keeps your remaining Claude and
ChatGPT (Codex) usage limits on screen.

━━ When values appear ━━
• macOS 14 or later (Apple silicon and Intel)
• Claude: when you use Claude Code (terminal) signed in with a Claude
  subscription (Pro·Max·Team), or the Claude desktop app (values received
  while the app is in use, without reset times).
  — claude.ai on the web alone does not provide values.
  — If no value shows, the Claude section explains why. If it says
    "Not connected", right-click the window → "Connect to Claude Code".
• ChatGPT: when you use Codex signed in with a ChatGPT account.
• Use only one of them? Hide the other section after installing (see below).

━━ Install ━━
1. Keep this folder as it is (it can stay in Downloads).
2. Open Terminal (⌘+Space → type "Terminal" → Enter).
3. Type zsh and a space, drag "설치.sh" (install) from this folder into the
   Terminal window, and press Enter.
4. When the window appears at the top right of the screen, you're done.
   An icon also appears in the Dock.

━━ How to use ━━
• Right-click the window to toggle “Limit Alerts” (on by default). Each limit alerts once at 10% remaining or less. You also get a reset alert if the app detects a known reset within one hour.
• Open “Usage Trends…” to see your current account’s weekly pace and the past four weeks of daily usage.
  The app keeps 35 days of weekly-limit history in ~/.cache/ai-usage/history.jsonl.
• Drag the window to move it; double-click to refresh.
• Language: right-click the window → "Language". It follows the macOS
  language at first (Korean, English, Japanese, Simplified Chinese).
• Hide a section: right-click the window (or the Dock icon) → uncheck
  "Show Claude" or "Show ChatGPT". You can't hide both.
• Right-click the window and choose "Appearance" to set background transparency, window size, and text size. Your choices are saved.
• Section order: "Section Order" in the same menu.
• Open at every login: "Open at Login" in the same menu.
• Quit: right-click the Dock icon → Quit.

━━ New versions ━━
• Every 6 hours the app checks for a new version and replaces itself.
  If the window disappears for a second and comes back in the same place,
  it was updated. No need to reinstall.
• To check right away: "Check for Updates" in the same menu.

━━ What the installer does to Claude Code ━━
• Hooks this app into the status line (statusLine) in
  ~/.claude/settings.json. The file before the change is kept as
  settings.json.bak-<date>.
• If you already had a status line, it keeps showing; usage limits are
  just saved in the background.
• If you had none, a model · context · usage line appears at the bottom
  of Claude Code.

━━ Uninstall ━━
1. If you turned on "Open at Login", turn it off first.
2. In Terminal, type zsh and a space, drag "지우기.sh" (uninstall) in, and
   press Enter. It removes the app and its data and restores the Claude
   Code status line to how it was before installing.
