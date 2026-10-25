"""Walk EA bChunk files (NFS MW 2005): [u32 id][u32 size][payload], ids with bit 31 set are containers.

usage: python -I chunks.py tree <file> [maxdepth]
       python -I chunks.py stats <file-or-dir>...
"""
import os
import struct
import sys
from collections import Counter


def walk(data, off, end, depth=0):
    while off + 8 <= end:
        cid, size = struct.unpack_from("<II", data, off)
        body = off + 8
        if body + size > end:
            yield depth, off, cid, size, "TRUNCATED"
            return
        yield depth, off, cid, size, None
        if cid & 0x80000000 and size >= 8:
            yield from walk(data, body, body + size, depth + 1)
        off = body + size


def tree(path, maxdepth=99):
    data = open(path, "rb").read()
    for depth, off, cid, size, err in walk(data, 0, len(data)):
        if depth > maxdepth or cid == 0:
            continue
        print(f"{'  ' * depth}{cid:08X} @{off:08X} size={size}{' ' + err if err else ''}")


def stats(paths):
    ids = Counter()
    files = Counter()
    for root in paths:
        targets = [root] if os.path.isfile(root) else [
            os.path.join(d, f) for d, _, fs in os.walk(root) for f in fs
            if f.upper().endswith((".BIN", ".BUN"))]
        for p in targets:
            data = open(p, "rb").read()
            seen = set()
            for depth, off, cid, size, err in walk(data, 0, len(data)):
                if cid:
                    ids[(depth, cid)] += 1
                    seen.add((depth, cid))
            for k in seen:
                files[k] += 1
    for (depth, cid), n in sorted(ids.items()):
        print(f"d{depth} {cid:08X} count={n} files={files[(depth, cid)]}")


if __name__ == "__main__":
    if sys.argv[1] == "tree":
        tree(sys.argv[2], int(sys.argv[3]) if len(sys.argv) > 3 else 99)
    else:
        stats(sys.argv[2:])
