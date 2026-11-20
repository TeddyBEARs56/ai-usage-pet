# AI 사용량 (Windows)

Windows 1.4.0은 한도 알림, 사용 추이, 모양 설정과 새 아이콘을 제공한다. 맥판과 같은 창을 Windows 에서 띄운다. 다른 창 위에 늘 떠 있고, 눌러도 쓰던 프로그램의 초점을 뺏지 않는다. 작업 표시줄 단추로 켜고 끈다. 무엇을 보여 주고 값을 어디서 가져오는지는 [맨 위 README](../README.md).

## 처음 쓰기

1. 저장소 **Releases** 에서 `AIUsage.exe` 를 받는다(파일 하나, .NET 설치 필요 없음).
2. 더블클릭한다. 「Windows의 PC 보호」가 뜨면 **추가 정보 → 실행**(서명하지 않은 개인 프로그램이라 뜬다).
3. 「이 PC에 설치할까요?」 → **예**. `%LOCALAPPDATA%\Programs\AIUsage` 에 들어가고 시작 메뉴에 「AI 사용량」이 생긴다. 작업 표시줄 단추를 오른쪽 클릭 → **작업 표시줄에 고정**.
4. Claude Code 를 쓰는 PC 면 설치 뒤 「상태 표시줄 연결」을 묻는다 → **예**.

**새 버전은 알아서 바뀐다(1.1.0 부터)**: 켠 뒤 1분, 그 뒤 6시간마다 Releases 에 더 높은 Windows 판(`windows-v<버전>`)이 있으면 받아서 바꾸고 다시 띄운다(창이 1~2초 사라졌다 같은 자리에). 설치 폴더에서 켰을 때만 바꾸고, 받은 파일은 SHA-256 과 exe 파일 버전을 확인한다. 바로 확인: 창 오른쪽 클릭 → 「업데이트 확인 (지금 <버전>)」. 끄기: 레지스트리 `HKCU\Software\AIUsage` 에 DWORD `AutoUpdate`=0. 기록: `%USERPROFILE%\.cache\ai-usage\update.log`. 자세한 것은 [맨 위 README](../README.md#자동-업데이트).

1.0.0 을 쓰고 있으면 한 번만 손으로: Releases 에서 새 `AIUsage.exe` 를 받아 실행 → 「설치된 것을 바꿀까요?」 예(먼저 켜져 있는 것은 끈다).

## 켜고 끄기

- **켜기**: 시작 메뉴나 작업 표시줄의 「AI 사용량」
- **끄기**: 작업 표시줄 단추 오른쪽 클릭 → 창 닫기, 또는 창 오른쪽 클릭 → 종료
- **Windows 를 켤 때 자동으로**: 창 오른쪽 클릭 → 「Windows 시작할 때 자동으로 열기」
- **칸 순서**: 창 오른쪽 클릭 → 「칸 순서」 → 「Claude → ChatGPT」 또는 「ChatGPT → Claude」. 고른 순서는 기억한다(레지스트리 `HKCU\Software\AIUsage` 의 `Order`).
- 창을 오른쪽 클릭해 「모양」에서 배경 투명도, 창 크기, 글자 크기를 각각 고른다. 선택은 다음 실행에도 유지된다.

언어: 창 오른쪽 클릭 → 「언어 (Language)」(처음엔 Windows 표시 언어). 자세한 것은 [맨 위 README](../README.md#언어).

창은 끌어서 옮기고(위치 기억), 두 번 클릭하면 새로고침. 오른쪽 클릭 메뉴에 마지막 값·초기화 날짜, 위치 초기화, 칸 순서, 상태 표시줄 연결, 언어, 업데이트 확인, 종료가 있다.

## 한도 알림과 사용 추이

창을 오른쪽 클릭해 「한도 알림」을 켜거나 끈다. 기본값은 켜짐이다. 5시간·주간 등 각 한도에서 처음 10% 이하로 남으면 한 번 알린다. 초기화 시각을 알면 그 시각부터 60분 이내에 확인한 초기화도 한 번 알린다. 초기화 시각을 모르면 첫 알림의 관측 시각에서 창 길이가 지난 뒤 새 관측부터 남은 한도를 다시 판단한다.

Windows 1.4.0에서 「모델별 한도 보이기 (Fable 등)」는 처음에 켜져 있어 작은 창과 메뉴 상세에 모델별 주간 한도가 표시된다. 「모델별 한도도 알림」은 처음에 꺼져 있다. 「한도 알림」이 켜져 있으면 모델 줄을 숨긴 상태에서도 모델별 알림을 받을 수 있다. 사용 추이와 35일 기록에는 전체 주간 한도만 들어간다.

「사용 추이…」를 열면 현재 계정의 주간 한도를 볼 수 있다. 「이번 주」는 지금까지의 사용률과 예상 소진 시각을, 「지난 4주」는 하루에 쓴 비율을 보여 준다. 흐린 막대는 대략적인 값이며 ↺는 초기화된 날이다. 주간 기록은 `%USERPROFILE%\.cache\ai-usage\history.jsonl`에 35일간 보관한다.

## 값이 뜨려면

| 값 | 필요한 것 |
|---|---|
| ChatGPT | Codex CLI 로그인. `codex` 를 PATH, npm(`%APPDATA%\npm`), `~\.codex\packages\standalone` 에서 찾는다 |
| Claude | `claude /usage`: Claude Code(또는 Claude 데스크톱 앱이 품은 claude.exe)에게 5분마다, 그리고 Claude Code 가 응답할 때마다 묻는다(모델을 부르지 않음, 자세한 것은 [값의 출처](../README.md#값의-출처두-판-공통)). 그 밖에 Claude Code + 상태 표시줄 연결(메뉴). `~\.claude\settings.json` 의 `statusLine` 을 `AIUsageStatus.exe --statusline` 으로 바꾼다(원래 파일은 `settings.json.bak-날짜`). 연결이 가리키는 `AIUsageStatus.exe` 가 없으면 켤 때 묻지 않고 다시 건다. 또는 Claude 데스크톱 앱의 `plan-usage-history.json` — 요즘 판(Microsoft Store 판 포함)은 `%LOCALAPPDATA%\Claude-Data\claude_<id>\`, 옛 판은 `%APPDATA%\Claude\`(`Claude-Data`·`Claude`·`Claude-3p` 를 `%LOCALAPPDATA%`·`%APPDATA%` 밑에서 모두 찾아 가장 새 기록을 쓴다). 앱을 쓰는 동안 받은 값만, 초기화 시각 없이. 기록이 1시간 넘게 멈췄고 Claude 값도 오래됐으면 오른쪽 클릭 메뉴에 「…Claude 아이콘을 오른쪽 클릭하면 그 뒤 하루 동안 다시 받습니다」가 뜬다(데스크톱 앱은 트레이 아이콘을 오른쪽 클릭한 뒤 24시간 동안만 사용량을 계속 받고, 그 밖에는 앱을 켤 때 한 번만 받는다. 왼쪽 클릭은 창만 연다). 어느 파일을 읽는지는 `AIUsageStatus.exe --print` 로 본다 |

Windows 에는 jq 가 없어서 상태 표시줄은 같이 들어 있는 작은 콘솔 프로그램 `AIUsageStatus.exe` 가 맡는다(창 프로그램으로 하면 실행될 때마다 마우스 커서가 「로딩 중」으로 깜빡인다). 상태 표시줄 모양은 맥과 같은 3줄(저장소·모델·한도)이고 브랜치 표시는 없다.

값만 확인: `%LOCALAPPDATA%\Programs\AIUsage\AIUsageStatus.exe --print`

## 맥판과 다른 점

- 가상 데스크톱을 바꾸면 따라오지 않는다. 작업 보기(⊞+Tab)에서 창을 오른쪽 클릭 → 「모든 데스크톱에 이 창 표시」.
- 배경 투명도를 높이면 창 뒤가 흐림 효과 없이 비친다. 설정을 바꾼 뒤 창이 보이지 않으면 앱을 종료하고 레지스트리 `HKCU\Software\AIUsage`의 `petBackgroundLevel` 값을 지운 뒤 다시 실행한다.

## 파일

| 파일 | 내용 |
|---|---|
| `Shared/Usage.cs` | 값 읽기·상태 표시줄 처리(맥판과 같은 규칙, 맥에서도 돌려 볼 수 있음) |
| `AIUsage/` | 창 프로그램(WinForms). `MainForm.cs` 화면, `Setup.cs` 설치·자동 실행·연결, `Updater.cs` 자동 업데이트 |
| `AIUsageStatus/` | 상태 표시줄 콘솔 프로그램. 창 프로그램 안에 들어가 있다가 설치할 때 꺼내진다 |
| `build.sh` | 맥에서 빌드 → `dist/AIUsage.exe` |
| `build.ps1` · `release.ps1` | Windows 에서 빌드 · 새 판 올리기(`build.sh`·`release.sh windows` 와 같은 일) |
| `AIUsage/AIUsage.ico` | 아이콘(`icon/make-icons.sh` 로 만든다) |

## 다시 만들기(맥)

```bash
brew install dotnet   # 처음 한 번
zsh windows/build.sh
```

공통 코드를 맥에서 돌려 보기: `dotnet run --project windows/AIUsageStatus -- --print`

`AIUsageStatus --trend-json --now <ISO 시각>`으로 현재 계정의 주간·28일 계산을 출력한다. 임시 `AIUSAGE_HOME`에서 `AIUsageStatus --record-once`는 로컬 값으로 추가한 주간 기록 줄 수를 출력한다. `AIUsageStatus --test-alerts 입력.json`을 실행하면 실제 알림 없이 재시작과 초기화를 시험할 수 있다. [공통 시험 입력 예시](../mac/README.md#추이와-알림-시험)

임시 `AIUSAGE_HOME`에서 `AIUsageStatus --alerts-once --now <ISO 시각> --limit-alerts 1 --model-alerts 0`을 실행하면 로컬 원천을 한 번 읽고 실제로 보낼 후보를 JSON 배열로 출력한다. 두 옵션은 각각 0 또는 1이며 기본값은 전체 켬·모델 끔이다. 같은 홈을 다시 쓰면 저장된 알림 상태에서 이어진다. 외부 CLI 호출이나 풍선 전송은 없다.

저장소 루트에서 `python3 windows/tests/model_limits.py <AIUsageStatus 실행 파일 또는 DLL 경로>`로 모델별 알림과 기록 분리 시험을 실행한다. 시험마다 임시 홈을 만들고 종료 후 지운다.

`history.jsonl` v1의 `source`는 Claude 상태 표시줄 `claude-session`, 데스크톱 기록 `claude-desktop`, 직접 조회 `claude-usage`, Codex 기록 `codex-rollout`, Codex 서버 `codex-server` 중 하나다. 모델별 값은 `usage-command.json`과 변경 로그 `claude-usage.log`에서 확인할 수 있다.

## 다시 만들기·올리기(Windows)

```powershell
winget install Microsoft.DotNet.SDK.10   # 처음 한 번
powershell -ExecutionPolicy Bypass -File windows\build.ps1
```

새 판 올리기는 [자동 업데이트](../README.md#자동-업데이트) 규칙 그대로다(두 csproj 의 `<Version>` 을 올려 커밋·push 한 뒤). `gh`(GitHub CLI)가 필요하다: `winget install GitHub.cli` → `gh auth login`.

```powershell
powershell -ExecutionPolicy Bypass -File windows\release.ps1 "바뀐 점"
```

## 지우기

1. 창 오른쪽 클릭 → 자동 실행 끄기 → 종료.
2. `%LOCALAPPDATA%\Programs\AIUsage` 폴더(받아 둔 새 판 `update\` 포함), 시작 메뉴 `AI 사용량` 바로 가기, `%USERPROFILE%\.cache\ai-usage` 를 지운다.
3. Claude Code 를 연결했으면 `~\.claude\settings.json` 을 `.bak-날짜` 파일로 되돌린다.
4. (선택) 레지스트리 `HKCU\Software\AIUsage`.
