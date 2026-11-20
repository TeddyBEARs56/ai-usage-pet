# Windows 판 새 판을 GitHub Releases 에 올린다 — Windows 에서. release.sh windows(맥)와 같은 규칙.
#   powershell -ExecutionPolicy Bypass -File windows\release.ps1 "바뀐 점"
# 깔려 있는 앱(Windows 1.1.0 부터)이 6시간 안에 알아서 받아 바꾼다.
# 먼저 두 csproj(windows\AIUsage·AIUsageStatus)의 <Version> 을 올려(예 1.3.0 → 1.3.1) 커밋·push 한다.
# 앱은 태그의 버전이 지금보다 높을 때만 바꾸므로, 버전을 안 올리면 올려도 아무도 안 바뀐다.
# 올린 뒤 README.md 「받기」 표의 Windows 줄 버전·링크를 새 판으로 고쳐 커밋·push 한다.
# 필요한 것: git, .NET 10 SDK, GitHub CLI(winget install GitHub.cli → gh auth login).
# Windows PowerShell 5.1 에서도 돌게 쓴다(한국어 때문에 이 파일은 UTF-8 BOM 으로 저장한다). 맥 판은 맥에서 zsh release.sh mac.
param([string]$Notes = '')
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$repo = 'TeddyBEARs56/ai-usage-pet'
$target = 'windows'
Set-Location $root

function Invoke-Checked {
    param([string]$Exe, [string[]]$Arguments)
    $output = & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Exe $($Arguments -join ' ') 실패(끝 상태 $LASTEXITCODE)." }
    return $output
}

function Get-CsprojVersion([string]$Path) {
    $match = [regex]::Match([IO.File]::ReadAllText((Join-Path $root $Path)), '<Version>([0-9.]+)</Version>')
    if (-not $match.Success) { throw "$Path 에 <Version> 이 없습니다." }
    return $match.Groups[1].Value
}

foreach ($tool in 'git', 'gh', 'dotnet') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "$tool 이 없습니다. 맨 위 설명을 보세요." }
}

$version = Get-CsprojVersion 'windows\AIUsage\AIUsage.csproj'
$statusVersion = Get-CsprojVersion 'windows\AIUsageStatus\AIUsageStatus.csproj'
if ($version -ne $statusVersion) { throw "두 csproj 의 <Version> 이 다릅니다($version / $statusVersion)." }
$tag = "$target-v$version"
$installNote = '처음 설치: 아래 AIUsage.exe 를 받아 실행 → 「이 PC에 설치할까요?」 예. Windows 1.1.0 이상이 설치돼 있으면 앱이 알아서 바꾼다.'

# 올린 판과 저장소 소스가 어긋나지 않게: 커밋 안 한 변경이 없고, main 이 GitHub 과 같아야 한다
if ((Invoke-Checked git @('branch', '--show-current')) -ne 'main') { throw 'main 에서 하세요.' }
if (Invoke-Checked git @('status', '--porcelain')) { throw '커밋하지 않은 변경이 있습니다.' }
Invoke-Checked git @('fetch', '-q', 'origin') | Out-Null
if ((Invoke-Checked git @('rev-parse', 'HEAD')) -ne (Invoke-Checked git @('rev-parse', 'origin/main'))) {
    throw 'main 을 먼저 push 하세요(HEAD 와 origin/main 이 다름).'
}

# 같은 버전이 이미 있거나, 올라온 것보다 낮으면 멈춘다
$existing = @(Invoke-Checked gh @('release', 'list', '-R', $repo, '--limit', '100', '--json', 'tagName', '-q', '.[].tagName') |
              Where-Object { $_ -like "$target-v*" })
if ($existing -contains $tag) { throw "$tag 는 이미 올라가 있습니다. 버전을 올리고 다시 하세요." }
$highest = $existing | ForEach-Object { $_.Substring("$target-v".Length) } | Where-Object { $_ -match '^[0-9]+(\.[0-9]+)+$' } |
           Sort-Object { [version]$_ } | Select-Object -Last 1
if ($highest -and [version]$highest -gt [version]$version) {
    throw "올라온 $target 최신이 $highest 입니다. 그보다 높은 버전으로 올리세요(지금 $version)."
}

$stage = Join-Path ([IO.Path]::GetTempPath()) ("aiusage-release-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
try {
    & (Join-Path $PSScriptRoot 'build.ps1')
    $asset = Join-Path $stage 'AIUsage.exe'
    Copy-Item (Join-Path $PSScriptRoot 'dist\AIUsage.exe') $asset

    # 줄바꿈·따옴표가 든 글을 그대로 넘기려고 파일로 준다
    $body = if ($Notes) { "$Notes`n`n$installNote" } else { $installNote }
    $notesFile = Join-Path $stage 'notes.md'
    [IO.File]::WriteAllText($notesFile, $body, (New-Object Text.UTF8Encoding $false))

    $head = Invoke-Checked git @('rev-parse', 'HEAD')
    Invoke-Checked gh @('release', 'create', $tag, $asset, '-R', $repo, '--target', $head,
                        '--title', "AI 사용량 Windows $version", '--notes-file', $notesFile) | Out-Null
    $assets = Invoke-Checked gh @('release', 'view', $tag, '-R', $repo, '--json', 'assets',
                                  '-q', '.assets[] | "\(.name) \(.size)바이트 \(.digest // "digest 없음")"')
    Write-Host "올림: $tag — $assets"
}
finally {
    Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
}

# README 「받기」 표: Windows 줄(링크에 releases/download/windows-v 가 든 줄)의 버전 칸과 링크를 새 판으로
$name = 'AIUsage.exe'
$url = "https://github.com/$repo/releases/download/$tag/$name"
$readmePath = Join-Path $root 'README.md'
$utf8 = New-Object Text.UTF8Encoding $false
$text = [IO.File]::ReadAllText($readmePath, $utf8)
$lines = $text -split "`n", -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -notmatch "releases/download/$target-v") { continue }
    $lines[$i] = [regex]::Replace($lines[$i], '^(\|[^|]*\|) [0-9.]+ \|', { param($m) "$($m.Groups[1].Value) $version |" })
    $lines[$i] = [regex]::Replace($lines[$i], "\[[^\]]+\]\(https://github\.com/[^)]*/releases/download/$target-v[^)]*\)", { param($m) "[$name]($url)" })
}
$updated = $lines -join "`n"
if ($updated -ne $text) {
    [IO.File]::WriteAllText($readmePath, $updated, $utf8)
    Invoke-Checked git @('commit', '-q', '-m', "README 받기 링크: $tag", 'README.md') | Out-Null
    Invoke-Checked git @('push', '-q', 'origin', 'main') | Out-Null
    Write-Host "README 받기 링크를 $tag 로 고쳐 push 함"
}
