#!/bin/zsh
# 소스를 고쳤을 때만 쓴다: AI 사용량 앱을 다시 만들어 ~/Applications 에 두고 (다시) 띄운다.
#   zsh mac/build.sh
# 평소에는 Dock 이나 ~/Applications 의 「AI 사용량」으로 열고, Dock 아이콘 오른쪽 클릭 → 종료로 끈다.
set -euo pipefail

here=${0:A:h}
app="$HOME/Applications/AI 사용량.app"

mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp "$here/Info.plist" "$app/Contents/Info.plist"
cp "$here/AppIcon.icns" "$app/Contents/Resources/AppIcon.icns"
cp "$here/share/claude-settings.js" "$app/Contents/Resources/claude-settings.js"
cp "$here/../i18n/strings.json" "$app/Contents/Resources/strings.json"  # 화면 글자 번역(윈도우 판과 같은 파일)
cp -R "$here/lproj/" "$app/Contents/Resources/"                         # Finder·Dock 의 앱 이름(시스템 언어별)
swiftc -O -swift-version 5 -parse-as-library "$here/AIUsagePet.swift" -o "$app/Contents/MacOS/AIUsagePet"
codesign --force --sign - "$app"
# Finder·Dock 이 바뀐 아이콘을 다시 읽게 한다
/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -f "$app"

pkill -x AIUsagePet || true  # 새 앱은 앞의 것이 꺼질 때까지 3초까지 기다렸다가 뜬다
open "$app"
echo "완료: $app"
