#!/bin/zsh
# 「AI 사용량」 설치 — 터미널에 zsh 와 빈칸 하나를 치고, 이 파일을 끌어다 놓은 뒤 Enter.
# Install "AI Usage" — type zsh and a space in Terminal, drag this file in, press Enter.
# 안내는 한국어·영어를 함께 낸다(앱 화면은 시스템 언어를 따르고 메뉴 「언어」로 바꾼다).
#   1) 앱을 ~/Applications 에 넣는다
#   2) Claude Code 를 쓰면 상태 표시줄에 연결한다(쓰던 상태 표시줄은 그대로 보인다)
#   3) 앱을 띄운다
set -u

here=${0:A:h}
src="$here/AI 사용량.app"
app_dir="$HOME/Applications"
app="$app_dir/AI 사용량.app"
bin="$app/Contents/MacOS/AIUsagePet"
claude_dir="$HOME/.claude"
stamp=$(date +%Y%m%d-%H%M%S)

if [[ ! -d "$src" ]]; then
  print "「AI 사용량.app」을 찾지 못했습니다. 압축을 푼 폴더 안의 설치.sh 를 끌어다 놓아 주세요."
  print "Couldn't find “AI 사용량.app”. Drag in the 설치.sh that is inside the unzipped folder."
  exit 1
fi

print "AI 사용량 설치를 시작합니다. / Installing AI Usage."
print

# 1. 앱 넣기(떠 있으면 먼저 끈다)
[[ -z "${AI_USAGE_NO_LAUNCH:-}" ]] && pkill -x AIUsagePet 2>/dev/null && sleep 1
mkdir -p "$app_dir"
rm -rf "$app"
if ! ditto "$src" "$app"; then
  print "✗ 앱을 ~/Applications 에 넣지 못했습니다. / Couldn't copy the app to ~/Applications."
  exit 1
fi
xattr -dr com.apple.quarantine "$app" 2>/dev/null  # 인터넷·메신저로 받은 표시를 지워 「확인되지 않은 개발자」 창이 안 뜨게
print "✓ 앱을 넣었습니다 / App installed — ~/Applications/AI 사용량.app"

# 2. Claude Code 상태 표시줄 연결
# 터미널 설정(.zshrc)을 읽지 않고 도는 스크립트라 PATH 에 ~/.local/bin 이 없을 수 있어 따로 본다
if [[ -d "$claude_dir" ]] || command -v claude >/dev/null 2>&1 || [[ -x "$HOME/.local/bin/claude" ]]; then
  mkdir -p "$claude_dir"
  result=$(osascript -l JavaScript "$app/Contents/Resources/claude-settings.js" install \
    "$claude_dir/settings.json" "$bin" "$claude_dir/ai-usage-statusline.sh" \
    "$claude_dir/ai-usage-statusline.original.json" "$claude_dir/settings.json.bak-$stamp" 2>&1)
  case "$result" in
    new)
      print "✓ Claude Code 에 연결했습니다 — 아래쪽에 모델·컨텍스트·사용 한도 줄이 새로 보입니다."
      print "  Connected to Claude Code — a model · context · usage line now shows at the bottom."
      print "  (원래 설정 / original settings: ~/.claude/settings.json.bak-$stamp)" ;;
    wrapped)
      chmod +x "$claude_dir/ai-usage-statusline.sh"
      print "✓ Claude Code 에 연결했습니다 — 쓰시던 상태 표시줄은 그대로 보입니다."
      print "  Connected to Claude Code — your existing status line keeps showing."
      print "  (원래 설정 / original settings: ~/.claude/settings.json.bak-$stamp)" ;;
    repaired)
      [[ -f "$claude_dir/ai-usage-statusline.sh" ]] && chmod +x "$claude_dir/ai-usage-statusline.sh"
      print "✓ Claude Code 연결을 새로 넣은 앱으로 고쳤습니다. / Claude Code now points to the new app." ;;
    already)
      print "✓ Claude Code 는 이미 연결돼 있습니다. / Claude Code is already connected." ;;
    *)
      print "✗ Claude Code 연결을 건너뛰었습니다 / Skipped connecting Claude Code — ${result#error:}"
      print "  ~/.claude/settings.json 은 손대지 않았습니다. / ~/.claude/settings.json was not changed." ;;
  esac
else
  print -- "- Claude Code 가 없어 Claude 연결은 건너뛰었습니다(나중에 깔면 이 설치를 한 번 더 실행)."
  print -- "  No Claude Code found, so the Claude connection was skipped (run this installer again after installing it)."
fi

# 3. 띄우기
if [[ -z "${AI_USAGE_NO_LAUNCH:-}" ]]; then
  print
  print "지금 읽히는 값 / Current values:"
  "$bin" --print 2>/dev/null | sed 's/^/  /'
  open "$app"
fi

print
print "끝났습니다. 화면 오른쪽 위에 창이 뜹니다. / Done. The window appears at the top right of the screen."
print -- "- 안 쓰는 칸 숨기기: 창 오른쪽 클릭 → 「Claude 보이기」 또는 「ChatGPT 보이기」"
print -- "  Hide a section you don't use: right-click the window → “Show Claude” or “Show ChatGPT”"
print -- "- 언어 바꾸기 / Change language: 창 오른쪽 클릭 / right-click the window → 「언어 (Language)」"
print -- "- Claude 값은 Claude Code 에서 한 번 대화하면 나옵니다. Claude 데스크톱 앱을 쓰는 동안 받은 값도 씁니다."
print -- "  Claude values appear after one conversation in Claude Code (values from the Claude desktop app are used too)."
print -- "- Claude Code 를 나중에 깔았다면: 창 오른쪽 클릭 → 「Claude Code 에 연결」"
print -- "  Installed Claude Code later? Right-click the window → “Connect to Claude Code”"
