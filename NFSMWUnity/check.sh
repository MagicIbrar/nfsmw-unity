#!/bin/sh
# usage: ./check.sh  — compile-checks NFSMW.Runtime (+ Editor + Tests) with dotnet, without Unity.
# Every .cs under Assets/NFSMW is included (new files too); each run uses its own project copy and obj/bin folders,
# so several runs can go at once. Prints the errors (if any) and "N Error(s)".
cd "$(dirname "$0")" || exit 1
id="check_$$"
python -I - "$id" <<'PY'
import sys, re, os, glob
tag = sys.argv[1]
for proj, roots in (("NFSMW.Runtime.csproj", ["Assets/NFSMW/Runtime", "Assets/NFSMW/Core"]),
                    ("NFSMW.Editor.csproj", ["Assets/NFSMW/Editor"]),
                    ("NFSMW.Tests.csproj", ["Assets/NFSMW/Tests"])):
    if not os.path.exists(proj):
        continue
    s = open(proj, encoding="utf-8").read()
    listed = set(m.replace("\\", "/") for m in re.findall(r'<Compile Include="([^"]+)"', s))
    files = [f.replace("\\", "/") for r in roots for f in glob.glob(r + "/**/*.cs", recursive=True)]
    add = "".join(f'<Compile Include="{f}" />\n    ' for f in sorted(files) if f not in listed)
    i = s.find("<Compile Include=")
    if i >= 0 and add:
        s = s[:i] + add + s[i:]
    # project references point at the original projects; give this copy its own obj/bin
    s = s.replace("<PropertyGroup>", f"<PropertyGroup><BaseIntermediateOutputPath>obj/{tag}/</BaseIntermediateOutputPath><OutputPath>bin/{tag}/</OutputPath>", 1)
    for other in ("NFSMW.Runtime", "NFSMW.Editor"):
        s = s.replace(f'"{other}.csproj"', f'"{other}.{tag}.csproj"')
    open(proj.replace(".csproj", f".{tag}.csproj"), "w", encoding="utf-8").write(s)
PY
status=0
for p in NFSMW.Runtime NFSMW.Editor NFSMW.Tests; do
  [ -f "$p.$id.csproj" ] || continue
  out=$(dotnet build "$p.$id.csproj" -nologo 2>&1)
  echo "$out" | grep -E " error " | sed -E 's/ \[[^]]*\]$//' | sort -u
  n=$(echo "$out" | grep -oE "[0-9]+ Error\(s\)" | tail -1)
  echo "$p: ${n:-build failed}"
  echo "$out" | grep -qE "^ *0 Error\(s\)" || status=1
done
rm -f NFSMW.*."$id".csproj
rm -rf "obj/$id" "bin/$id"
exit $status
