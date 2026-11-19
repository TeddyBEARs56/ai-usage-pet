#!/bin/zsh
# 다른 맥에 줄 설치 묶음을 만든다 — 애플 칩·인텔 공용, macOS 14 이상.
#   zsh mac/package.sh [zip 경로]      (기본: ~/Downloads/AI사용량-mac-<버전>.zip)
# 묶음: 「AI 사용량 설치」 폴더 = AI 사용량.app + 설치.sh + 지우기.sh + 읽어 주세요 (Read me).txt(한국어·영어)
# 받는 쪽은 읽어 주세요 대로 터미널에서 zsh 설치.sh 만 하면 된다(앱은 개발자 인증 없이 임시 서명).
# 버전은 Info.plist 의 CFBundleShortVersionString. 새로 건넬 묶음을 만들 땐 먼저 그것과 CFBundleVersion 을 올린다 —
# 같은 버전 zip 이 이미 있으면 덮어쓰지 않고 멈춘다(경로를 직접 주면 덮어쓴다).
# release.sh mac 도 이 스크립트로 같은 묶음을 만들어 Releases 에 올린다(자동 업데이트가 그 zip 을 받는다).
set -euo pipefail

here=${0:A:h}
version=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$here/Info.plist")
build=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleVersion' "$here/Info.plist")
out=${1:-$HOME/Downloads/AI사용량-mac-$version.zip}
if [[ -z ${1:-} && -e $out ]]; then
  print -u2 "이미 있음: $out"
  print -u2 "mac/Info.plist 의 버전(CFBundleShortVersionString $version, CFBundleVersion $build)을 올리고 다시 만드세요."
  exit 1
fi
stage=$(mktemp -d)
trap 'rm -rf "$stage"' EXIT
pkg="$stage/AI 사용량 설치"
app="$pkg/AI 사용량.app"

mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp "$here/Info.plist" "$app/Contents/Info.plist"
cp "$here/AppIcon.icns" "$app/Contents/Resources/AppIcon.icns"
cp "$here/share/claude-settings.js" "$app/Contents/Resources/claude-settings.js"
cp "$here/../i18n/strings.json" "$app/Contents/Resources/strings.json"
cp -R "$here/lproj/" "$app/Contents/Resources/"
for arch in arm64 x86_64; do
  swiftc -O -swift-version 5 -parse-as-library -target "$arch-apple-macos14.0" \
    "$here/AIUsagePet.swift" -o "$stage/AIUsagePet-$arch"
done
lipo -create "$stage/AIUsagePet-arm64" "$stage/AIUsagePet-x86_64" -output "$app/Contents/MacOS/AIUsagePet"
codesign --force --sign - "$app"

cp "$here/share/install.sh" "$pkg/설치.sh"
cp "$here/share/uninstall.sh" "$pkg/지우기.sh"
cp "$here/share/README.txt" "$pkg/읽어 주세요 (Read me).txt"
chmod +x "$pkg/설치.sh" "$pkg/지우기.sh"

rm -f "$out"
ditto -c -k --sequesterRsrc --keepParent "$pkg" "$out"
print "완료: $out (버전 $version($build), $(lipo -archs "$app/Contents/MacOS/AIUsagePet"), $(du -h "$out" | cut -f1))"
