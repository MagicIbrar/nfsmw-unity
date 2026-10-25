#!/bin/sh
# usage: ./runtests.sh [filter]  — play-mode tests through Unity.exe (results in Logs/playmode-results.xml)
cd "$(dirname "$0")" || exit 1
filter=""
[ -n "$1" ] && filter="-testFilter $1"
"/c/Program Files/Unity/Hub/Editor/6000.4.2f1/Editor/Unity.exe" -batchmode -projectPath "$(pwd -W)" -runTests -testPlatform PlayMode \
  $filter -testResults "$(pwd -W)/Logs/playmode-results.xml" -logFile "$(pwd -W)/Logs/playmode.log" > /dev/null 2>&1
echo "exit $?"
grep -o 'result="[A-Za-z]*"' Logs/playmode-results.xml | sort | uniq -c
grep -o '<test-case [^>]*name="[A-Za-z]*"[^>]*result="[A-Za-z]*"' Logs/playmode-results.xml | sed -E 's/.* name="([A-Za-z]+)".*result="([A-Za-z]+)"/\1 \2/'
python - <<'PY'
import re
x=open("Logs/playmode-results.xml",encoding="utf-8").read()
for m in re.finditer(r"<message><!\[CDATA\[(.*?)\]\]></message>",x,re.S):
    if "child tests" not in m.group(1): print("FAIL:",m.group(1)[:600])
for l in re.findall(r"NFSMW[^\n<]*(?:test|nis|police|race|barriers)[^\n<]*",x): print(l[:220])
PY
