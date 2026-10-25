#!/bin/sh
# usage: rq.sh <tool> '<json args>'  -> prints only REA's normalized_result (compact), for scripting and reading.
# examples: rq.sh search_strings '{"pattern":"HUD_SingleRace"}'
#           rq.sh xrefs '{"address":"0x89fbec"}'
#           rq.sh procedure_pseudo_code '{"procedure":"0x5075a0"}'
tool="$1"; args="${2:-{\}}"
curl -s -m "${REA_TIMEOUT:-1800}" -X POST http://127.0.0.1:47811/call -H 'content-type: application/json' \
  --data "{\"name\":\"$tool\",\"arguments\":$args}" |
node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{try{const j=JSON.parse(s);const r=j.result??j;let t=r.content?r.content.map(c=>c.text??"").join("\n"):JSON.stringify(r);try{const e=JSON.parse(t);const n=e.normalized_result??e.raw_result??e;if(typeof n==="string")console.log(n);else if(n&&n.pseudo_code)console.log(n.pseudo_code);else console.log(JSON.stringify(n,null,1))}catch(_){console.log(t)}}catch(e){console.log(s)}})'
