# AI 사용량 (Windows)

맥판과 같은 창을 Windows 에서 띄운다. 다른 창 위에 늘 떠 있고, 눌러도 쓰던 프로그램의 초점을 뺏지 않는다. 작업 표시줄 단추로 켜고 끈다.

## 처음 쓰기

1. 저장소 **Releases** 에서 `AIUsage.exe` 를 받는다(파일 하나, .NET 설치 필요 없음).
2. 더블클릭한다. 「Windows의 PC 보호」가 뜨면 **추가 정보 → 실행**(서명하지 않은 개인 프로그램이라 뜬다).
3. 「이 PC에 설치할까요?」 → **예**. `%LOCALAPPDATA%\Programs\AIUsage` 에 들어가고 시작 메뉴에 「AI 사용량」이 생긴다. 작업 표시줄 단추를 오른쪽 클릭 → **작업 표시줄에 고정**.
4. Claude Code 를 쓰는 PC 면 설치 뒤 「상태 표시줄 연결」을 묻는다 → **예**.

새 버전은 받은 `AIUsage.exe` 를 실행하면 「설치된 것을 바꿀까요?」라고 묻는다(먼저 켜져 있는 것은 끈다).

## 켜고 끄기

- **켜기**: 시작 메뉴나 작업 표시줄의 「AI 사용량」
- **끄기**: 작업 표시줄 단추 오른쪽 클릭 → 창 닫기, 또는 창 오른쪽 클릭 → 종료
- **Windows 를 켤 때 자동으로**: 창 오른쪽 클릭 → 「Windows 시작할 때 자동으로 열기」

창은 끌어서 옮기고(위치 기억), 두 번 클릭하면 새로고침. 오른쪽 클릭 메뉴에 마지막 값·초기화 날짜, 위치 초기화, 상태 표시줄 연결, 종료가 있다.

## 값이 뜨려면

| 값 | 필요한 것 |
|---|---|
| ChatGPT | Codex CLI 로그인. `codex` 를 PATH, npm(`%APPDATA%\npm`), `~\.codex\packages\standalone` 에서 찾는다 |
| Claude | Claude Code + 상태 표시줄 연결(메뉴). `~\.claude\settings.json` 의 `statusLine` 을 `AIUsageStatus.exe --statusline` 으로 바꾼다(원래 파일은 `settings.json.bak-날짜`) |

Windows 에는 jq 가 없어서 상태 표시줄은 같이 들어 있는 작은 콘솔 프로그램 `AIUsageStatus.exe` 가 맡는다(창 프로그램으로 하면 실행될 때마다 마우스 커서가 「로딩 중」으로 깜빡인다). 상태 표시줄 모양은 맥과 같은 3줄(저장소·모델·한도)이고 브랜치 표시는 없다.

값만 확인: `%LOCALAPPDATA%\Programs\AIUsage\AIUsageStatus.exe --print`

## 맥판과 다른 점

- 가상 데스크톱을 바꾸면 따라오지 않는다. 작업 보기(⊞+Tab)에서 창을 오른쪽 클릭 → 「모든 데스크톱에 이 창 표시」.
- 반투명 대신 짙은 단색 배경. Windows 10 은 모서리가 덜 매끈하다.

## 파일

| 파일 | 내용 |
|---|---|
| `Shared/Usage.cs` | 값 읽기·상태 표시줄 처리(맥판과 같은 규칙, 맥에서도 돌려 볼 수 있음) |
| `AIUsage/` | 창 프로그램(WinForms). `MainForm.cs` 화면, `Setup.cs` 설치·자동 실행·연결 |
| `AIUsageStatus/` | 상태 표시줄 콘솔 프로그램. 창 프로그램 안에 들어가 있다가 설치할 때 꺼내진다 |
| `build.sh` | 맥에서 빌드 → `dist/AIUsage.exe` |

## 다시 만들기(맥)

```bash
brew install dotnet   # 처음 한 번
zsh windows/build.sh
```

공통 코드를 맥에서 돌려 보기: `dotnet run --project windows/AIUsageStatus -- --print`

## 지우기

1. 창 오른쪽 클릭 → 자동 실행 끄기 → 종료.
2. `%LOCALAPPDATA%\Programs\AIUsage` 폴더, 시작 메뉴 `AI 사용량` 바로 가기, `%USERPROFILE%\.cache\ai-usage` 를 지운다.
3. Claude Code 를 연결했으면 `~\.claude\settings.json` 을 `.bak-날짜` 파일로 되돌린다.
4. (선택) 레지스트리 `HKCU\Software\AIUsage`.
