#!/bin/zsh
# 번역 점검 — 코드의 T("…") 열쇠가 모두 strings.json 에 있는지, 언어마다 번역과 {0} 자리가 맞는지 본다.
#   zsh i18n/check.sh        (문제가 있으면 끝 상태 1)
# 문구를 고치거나 새로 넣으면 strings.json 에 같은 한국어 열쇠로 en·ja·zh-Hans 를 함께 넣는다.
set -euo pipefail
root=${0:A:h:h}
exec /usr/bin/python3 -I - "$root" <<'PY'
import json, re, sys, pathlib
root = pathlib.Path(sys.argv[1])
table = json.loads((root / "i18n/strings.json").read_text(encoding="utf-8"))
languages = ["en", "ja", "zh-Hans"]
problems = []

def holes(text):
    return sorted(set(re.findall(r"\{\d+\}", text)))

for key, value in table.items():
    if key.startswith("_"):
        continue
    for language in languages:
        text = value.get(language)
        if not text:
            problems.append(f"번역 없음 [{language}] {key!r}")
        elif holes(text) != holes(key):
            problems.append(f"{{n}} 자리가 다름 [{language}] {key!r} → {text!r}")

literal = re.compile(r'\bT\("((?:[^"\\]|\\.)*)"')
used = set()
for path in [root / "mac/AIUsagePet.swift", *sorted((root / "windows").glob("*/*.cs"))]:
    for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        for raw in literal.findall(line):
            key = raw.replace('\\n', '\n').replace('\\"', '"').replace('\\\\', '\\')
            used.add(key)
            if key not in table:
                problems.append(f"strings.json 에 없음 {path.relative_to(root)}:{number} {key!r}")

unused = [k for k in table if not k.startswith("_") and k not in used]
for key in unused:
    print(f"(참고) 코드에서 안 쓰는 열쇠: {key!r}")
for problem in problems:
    print(problem)
print(f"열쇠 {len(used)}개 사용 · 번역 {len(table)}개 · 문제 {len(problems)}개")
sys.exit(1 if problems else 0)
PY
