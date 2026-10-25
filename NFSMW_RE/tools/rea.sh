#!/bin/sh
# usage: rea.sh <tool> '<json args>'   -> calls the REA bridge on 127.0.0.1:47811
tool="$1"; args="${2:-{\}}"
curl -s -m "${REA_TIMEOUT:-7200}" -X POST http://127.0.0.1:47811/call \
  -H 'content-type: application/json' \
  --data "{\"name\":\"$tool\",\"arguments\":$args}" |
node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{try{const j=JSON.parse(s);const r=j.result??j;if(r.content)for(const c of r.content)console.log(c.text??JSON.stringify(c));else console.log(JSON.stringify(r,null,1))}catch(e){console.log(s)}})'
