#!/bin/sh
# usage: decomp.sh <hexaddr...>  -> decompiled/FUN_<addr>.c via REA procedure_pseudo_code
cd "$(dirname "$0")/.." || exit 1
tmp="${TMPDIR:-/tmp}/rea_decomp.json"
for a in "$@"; do
  n=$(printf "FUN_%08x" "$a")
  tools/rea.sh procedure_pseudo_code "{\"procedure\":\"$n\"}" > "$tmp"
  node -e 'const fs=require("fs");const j=JSON.parse(fs.readFileSync(process.argv[1]));const r=j.normalized_result??j;let c=r.pseudo_code??r.code??r.text??r;if(typeof c!="string")c=JSON.stringify(c,null,1);fs.writeFileSync(process.argv[2],c);console.log(process.argv[2],c.length)' "$tmp" "decompiled/$n.c"
done
