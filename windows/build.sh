#!/bin/zsh
# 윈도우용 AI 사용량을 맥에서 만든다(.NET 10 SDK 필요: brew install dotnet).
#   zsh windows/build.sh  →  windows/dist/AIUsage.exe 하나만 윈도우 PC 로 옮기면 된다
set -euo pipefail

here=${0:A:h}
export PATH=/opt/homebrew/bin:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
dist="$here/dist"
rm -rf "$dist"

[[ -f $here/AIUsage/AIUsage.ico ]] || swift "$here/../icon/make-icon.swift" --windows "$here/AIUsage/AIUsage.ico"

# 1) 상태 표시줄 프로그램 → 2) 그것을 품은 창 프로그램
dotnet publish "$here/AIUsageStatus/AIUsageStatus.csproj" -c Release -r win-x64 -o "$here/AIUsageStatus/publish" -nologo -v q
dotnet publish "$here/AIUsage/AIUsage.csproj" -c Release -o "$dist" -nologo -v q

ls -la "$dist"
echo "완료: $dist/AIUsage.exe"
