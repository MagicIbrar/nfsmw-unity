#!/bin/sh
# Run Ghidra headless against a persistent NFSMW project.
# usage: ghidra.sh import            (first-time import + full auto-analysis)
#        ghidra.sh decompile <addrfile> <outdir>
# Set these for your machine (defaults are examples):
#   JAVA_HOME   a JDK 21
#   GHIDRA_HOME the Ghidra install folder
#   NFSMW_EXE   your own copy of speed.exe (v1.3)
#   GHIDRA_PROJ a folder for the Ghidra project
export JAVA_HOME="${JAVA_HOME:-C:/tools/jdk-21}"
G="${GHIDRA_HOME:-C:/tools/ghidra_12.1.4_PUBLIC}/support/analyzeHeadless.bat"
P="${GHIDRA_PROJ:-C:/tools/work/ghidra_nfsmw}"
EXE="${NFSMW_EXE:-C:/tools/work/speed.exe}"
S="$(cd "$(dirname "$0")" && pwd -W)/ghidra_scripts"
case "$1" in
  import) cmd //c "$G" "$P" nfsmw -import "$EXE" -overwrite -analysisTimeoutPerFile 7200 ;;
  decompile) cmd //c "$G" "$P" nfsmw -process speed.exe -noanalysis -scriptPath "$S" -postScript DecompileAt.java "$3" "$2" ${4:-0} ;;
esac
