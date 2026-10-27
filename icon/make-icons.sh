#!/bin/zsh
# 아이콘 그림을 고쳤을 때: 맥(mac/AppIcon.icns)·윈도우(windows/AIUsage/AIUsage.ico) 아이콘을 한 번에 다시 만든다.
#   zsh icon/make-icons.sh
set -euo pipefail

here=${0:A:h}
root=${here:h}
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

swift "$here/make-icon.swift" "$work/icon.png"
mkdir "$work/AppIcon.iconset"
for size in 16 32 128 256 512; do
  sips -z $size $size "$work/icon.png" --out "$work/AppIcon.iconset/icon_${size}x${size}.png" >/dev/null
  sips -z $((size * 2)) $((size * 2)) "$work/icon.png" --out "$work/AppIcon.iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$work/AppIcon.iconset" -o "$root/mac/AppIcon.icns"

swift "$here/make-icon.swift" --windows "$root/windows/AIUsage/AIUsage.ico"
echo "완료: mac/AppIcon.icns, windows/AIUsage/AIUsage.ico (반영하려면 각 build.sh 를 다시 실행)"
