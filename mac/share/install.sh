#!/bin/zsh
# 「AI 사용량」 설치 — 터미널에 zsh 와 빈칸 하나를 치고, 이 파일을 끌어다 놓은 뒤 Enter.
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
  exit 1
fi

print "AI 사용량 설치를 시작합니다."
print

# 1. 앱 넣기(떠 있으면 먼저 끈다)
[[ -z "${AI_USAGE_NO_LAUNCH:-}" ]] && pkill -x AIUsagePet 2>/dev/null && sleep 1
mkdir -p "$app_dir"
rm -rf "$app"
if ! ditto "$src" "$app"; then
  print "✗ 앱을 ~/Applications 에 넣지 못했습니다."
  exit 1
fi
xattr -dr com.apple.quarantine "$app" 2>/dev/null  # 인터넷·메신저로 받은 표시를 지워 「확인되지 않은 개발자」 창이 안 뜨게
print "✓ 앱을 넣었습니다 — ~/Applications/AI 사용량.app"

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
      print "  (원래 설정은 ~/.claude/settings.json.bak-$stamp)" ;;
    wrapped)
      chmod +x "$claude_dir/ai-usage-statusline.sh"
      print "✓ Claude Code 에 연결했습니다 — 쓰시던 상태 표시줄은 그대로 보입니다."
      print "  (원래 설정은 ~/.claude/settings.json.bak-$stamp)" ;;
    repaired)
      [[ -f "$claude_dir/ai-usage-statusline.sh" ]] && chmod +x "$claude_dir/ai-usage-statusline.sh"
      print "✓ Claude Code 연결을 새로 넣은 앱으로 고쳤습니다." ;;
    already)
      print "✓ Claude Code 는 이미 연결돼 있습니다." ;;
    *)
      print "✗ Claude Code 연결을 건너뛰었습니다 — ${result#error:}"
      print "  ~/.claude/settings.json 은 손대지 않았습니다." ;;
  esac
else
  print -- "- Claude Code 가 없어 Claude 연결은 건너뛰었습니다(나중에 깔면 이 설치를 한 번 더 실행)."
fi

# 3. 띄우기
if [[ -z "${AI_USAGE_NO_LAUNCH:-}" ]]; then
  print
  print "지금 읽히는 값:"
  "$bin" --print 2>/dev/null | sed 's/^/  /'
  open "$app"
fi

print
print "끝났습니다. 화면 오른쪽 위에 창이 뜹니다."
print -- "- 안 쓰는 칸 숨기기: 창 오른쪽 클릭 → 「Claude 보이기」 또는 「ChatGPT 보이기」"
print -- "- Claude 값은 Claude Code 에서 한 번 대화하면 나옵니다. Claude 데스크톱 앱을 쓰는 동안 받은 값도 씁니다."
print -- "- Claude Code 를 나중에 깔았다면: 창 오른쪽 클릭 → 「Claude Code 에 연결」"
