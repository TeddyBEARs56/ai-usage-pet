# Windows 용 AI 사용량을 Windows 에서 만든다(.NET 10 SDK 필요: winget install Microsoft.DotNet.SDK.10). build.sh(맥)와 같은 일을 한다.
#   powershell -ExecutionPolicy Bypass -File windows\build.ps1  →  windows\dist\AIUsage.exe 하나면 된다
# Windows PowerShell 5.1 에서도 돌게 쓴다(한국어 때문에 이 파일은 UTF-8 BOM 으로 저장한다).
$ErrorActionPreference = 'Stop'

$here = $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$dist = Join-Path $here 'dist'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET SDK 가 없습니다. winget install Microsoft.DotNet.SDK.10 으로 까세요.'
}
if (-not (Test-Path (Join-Path $here 'AIUsage\AIUsage.ico'))) {
    throw 'AIUsage\AIUsage.ico 가 없습니다. 아이콘은 맥에서 zsh icon/make-icons.sh 로 만듭니다.'
}
if (Test-Path $dist) { Remove-Item -Recurse -Force $dist }

# 1) 상태 표시줄 프로그램 → 2) 그것을 품은 창 프로그램
& dotnet publish (Join-Path $here 'AIUsageStatus\AIUsageStatus.csproj') -c Release -r win-x64 -o (Join-Path $here 'AIUsageStatus\publish') -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "AIUsageStatus 를 만들지 못했습니다(끝 상태 $LASTEXITCODE)." }
& dotnet publish (Join-Path $here 'AIUsage\AIUsage.csproj') -c Release -o $dist -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "AIUsage 를 만들지 못했습니다(끝 상태 $LASTEXITCODE)." }

Get-ChildItem $dist | Format-Table Name, Length -AutoSize | Out-String | Write-Host
Write-Host "완료: $(Join-Path $dist 'AIUsage.exe')"
