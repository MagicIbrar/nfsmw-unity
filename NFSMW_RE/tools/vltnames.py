"""Match candidate names (vlt_names.txt) against class and field keys; print each class with resolved fields.

usage: python -I vltnames.py <attributes.bin> <names.txt>
"""
import struct
import sys

import os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import vlt

names = {}
for line in open(sys.argv[2]):
    line = line.strip()
    if line and not line.startswith("#"):
        names[vlt.jhash(line)] = line

for key, typ, extra, size, off in vlt.exports:
    if typ != vlt.T_CLASS:
        continue
    a = vlt.VLT + off
    _, _, n, _, layout, _, _ = struct.unpack_from("<7I", vlt.d, a)
    fp = vlt.ptr.get(a + 12)
    fields = []
    for i in range(n):
        fk, ft, fo, fs, fc, fl, al = struct.unpack_from("<IIHHHBB", vlt.d, fp + i * 16)
        fields.append((fo, names.get(fk, f"{fk:08X}"), names.get(ft, f"{ft:08X}"), fs, fc, fl))
    known = sum(1 for f in fields if not f[1][0].isdigit() and len(f[1]) != 8 or f[1] in names.values())
    print(f"class {names.get(key, f'{key:08X}')}: {n} fields, layout {layout:#x}")
    for f in sorted(fields):
        print(f"    +{f[0]:#05x} {f[1]:<24} {f[2]:<28} size {f[3]} count {f[4]} flags {f[5]:#x}")
