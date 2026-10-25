#!/bin/sh
# usage: ./batch.sh <Class.Method> [logname]  — runs a static editor method in batch mode, prints NFSMW lines and errors
# (calls Unity.exe directly: the unity CLI can mistake other projects' "-projectPath ." editors for this one)
cd "$(dirname "$0")" || exit 1
log="Logs/${2:-batch}.log"
"/c/Program Files/Unity/Hub/Editor/6000.4.2f1/Editor/Unity.exe" -batchmode -quit -projectPath "$(pwd -W)" \
  -executeMethod "NfsMw.EditorTools.$1" -logFile "$(pwd -W)/$log" $NFSMW_ARGS > /dev/null 2>&1
echo "exit $?"
grep -E "^NFSMW|error CS|Exception: |NullReference|IndexOutOfRange|KeyNotFound" "$log" | grep -v "accepting client" | head -${LINES_MAX:-40}
exit 0
