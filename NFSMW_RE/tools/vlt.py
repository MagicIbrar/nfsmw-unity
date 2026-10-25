"""NFS MW attribute database (VPAK: db.vlt + db.bin) explorer.

usage: python -I vlt.py <attributes.bin> [dump]
"""
import struct
import sys

d = open(sys.argv[1], "rb").read()
magic, count, names_off, _, _, vlt_size, bin_size, bin_off, vlt_off, _ = struct.unpack_from("<10I", d, 0)
VLT, BIN = vlt_off, bin_off  # dependency 0 = db.vlt part, 1 = db.bin part


def chunks(o, end):
    while o + 8 <= end:
        tag = d[o:o + 4][::-1].decode("latin1")
        sz = struct.unpack_from("<I", d, o + 4)[0]
        if sz < 8:
            break
        yield tag, o, sz
        o += sz


ch = {t: (o, s) for t, o, s in chunks(VLT, VLT + bin_size if False else VLT + 10**9) if t in ("DepN", "StrN", "DatN", "ExpN", "PtrN")}

# pointer fixups: absolute file offset of pointer -> absolute file offset of target
ptr = {}
o, s = ch["PtrN"]
bases = [VLT, BIN]
cur = VLT
for i in range((s - 8) // 12):
    f, t, idx, dst = struct.unpack_from("<IHHI", d, o + 8 + i * 12)
    if t == 0:
        break
    if t == 2:
        cur = bases[idx]
    elif t == 3:
        ptr[cur + f] = bases[idx] + dst
    elif t in (1, 5):
        ptr[cur + f] = None


def cstr(a):
    e = d.index(b"\0", a)
    return d[a:e].decode("latin1")


def u32(a):
    return struct.unpack_from("<I", d, a)[0]


o, s = ch["ExpN"]
n = u32(o + 8)
exports = [struct.unpack_from("<5I", d, o + 12 + i * 20) for i in range(n)]
T_DB, T_CLASS, T_COLL = 0xCBBC628F, 0x5E970CBC, 0x8E112EB7


def show(a, size):
    words = []
    for k in range(0, size, 4):
        p = ptr.get(a + k, "x")
        if p == "x":
            words.append(f"{u32(a + k):08X}")
        elif p is None:
            words.append("null")
        else:
            words.append(f"->{p:X}")
    return " ".join(words)


if __name__ == "__main__":
    for key, typ, extra, size, off in exports:
        if typ == T_DB:
            a = VLT + off
            print("DB", show(a, size))
    shown = 0
    for key, typ, extra, size, off in exports:
        if typ == T_CLASS and shown < 3:
            a = VLT + off
            print(f"CLASS {key:08X}", show(a, size))
            shown += 1
    shown = 0
    for key, typ, extra, size, off in exports:
        if typ == T_COLL and shown < 4:
            a = VLT + off
            print(f"COLL {key:08X}", show(a, size))
            shown += 1


def jhash(s, init=0xABCDEF00):
    """Bob Jenkins lookup2, as used for VLT keys."""
    k = s.encode() if isinstance(s, str) else s
    M = 0xFFFFFFFF
    a = b = 0x9E3779B9
    c = init
    n = len(k)
    i = 0

    def mix(a, b, c):
        a = (a - b - c) & M; a ^= c >> 13
        b = (b - c - a) & M; b ^= (a << 8) & M
        c = (c - a - b) & M; c ^= b >> 13
        a = (a - b - c) & M; a ^= c >> 12
        b = (b - c - a) & M; b ^= (a << 16) & M
        c = (c - a - b) & M; c ^= b >> 5
        a = (a - b - c) & M; a ^= c >> 3
        b = (b - c - a) & M; b ^= (a << 10) & M
        c = (c - a - b) & M; c ^= b >> 15
        return a, b, c

    while n - i >= 12:
        a = (a + int.from_bytes(k[i:i + 4], "little")) & M
        b = (b + int.from_bytes(k[i + 4:i + 8], "little")) & M
        c = (c + int.from_bytes(k[i + 8:i + 12], "little")) & M
        a, b, c = mix(a, b, c)
        i += 12
    c = (c + len(k)) & M
    rest = k[i:]
    for j, shift in ((10, 24), (9, 16), (8, 8)):
        if len(rest) > j:
            c = (c + (rest[j] << shift)) & M
    for j, shift in ((7, 24), (6, 16), (5, 8), (4, 0)):
        if len(rest) > j:
            b = (b + (rest[j] << shift)) & M
    for j, shift in ((3, 24), (2, 16), (1, 8), (0, 0)):
        if len(rest) > j:
            a = (a + (rest[j] << shift)) & M
    a, b, c = mix(a, b, c)
    return c
