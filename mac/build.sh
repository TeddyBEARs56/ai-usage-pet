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
swiftc -O -swift-version 5 -parse-as-library "$here/AIUsagePet.swift" -o "$app/Contents/MacOS/AIUsagePet"
codesign --force --sign - "$app"
# Finder·Dock 이 바뀐 아이콘을 다시 읽게 한다
/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -f "$app"

pkill -x AIUsagePet || true  # 새 앱은 앞의 것이 꺼질 때까지 3초까지 기다렸다가 뜬다
open "$app"
echo "완료: $app"
