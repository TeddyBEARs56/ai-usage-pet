#!/bin/zsh
# 「AI 사용량」 지우기 — 터미널에 zsh 와 빈칸 하나를 치고, 이 파일을 끌어다 놓은 뒤 Enter.
# 앱·기록을 지우고, Claude Code 상태 표시줄을 설치 전으로 되돌린다.
set -u

here=${0:A:h}
app="$HOME/Applications/AI 사용량.app"
claude_dir="$HOME/.claude"
stamp=$(date +%Y%m%d-%H%M%S)

# 설정 되돌리는 도구는 설치된 앱(없으면 이 폴더의 앱) 안에 있다
helper="$app/Contents/Resources/claude-settings.js"
[[ -f "$helper" ]] || helper="$here/AI 사용량.app/Contents/Resources/claude-settings.js"

[[ -z "${AI_USAGE_NO_LAUNCH:-}" ]] && pkill -x AIUsagePet 2>/dev/null && sleep 1

if [[ -f "$claude_dir/settings.json" && -f "$helper" ]]; then
  result=$(osascript -l JavaScript "$helper" uninstall \
    "$claude_dir/settings.json" "$app/Contents/MacOS/AIUsagePet" "$claude_dir/ai-usage-statusline.sh" \
    "$claude_dir/ai-usage-statusline.original.json" "$claude_dir/settings.json.bak-$stamp" 2>&1)
  case "$result" in
    restored)  print "✓ Claude Code 상태 표시줄을 쓰시던 것으로 되돌렸습니다." ;;
    removed)   print "✓ Claude Code 상태 표시줄 연결을 뺐습니다." ;;
    untouched) print -- "- Claude Code 설정에는 연결이 없어 그대로 두었습니다." ;;
    *)         print "✗ Claude Code 설정을 되돌리지 못했습니다 — ${result#error:}" ;;
  esac
  case "$result" in
    restored|removed) rm -f "$claude_dir/ai-usage-statusline.sh" "$claude_dir/ai-usage-statusline.original.json" ;;
  esac
fi

rm -rf "$app" "$HOME/.cache/ai-usage"
[[ -z "${AI_USAGE_NO_LAUNCH:-}" ]] && defaults delete com.local.ai-usage-pet >/dev/null 2>&1
print "✓ 앱과 기록을 지웠습니다."
print "  「로그인할 때 자동으로 열기」를 켜 두셨다면 시스템 설정 → 일반 → 로그인 항목에서 「AI 사용량」을 빼 주세요."
