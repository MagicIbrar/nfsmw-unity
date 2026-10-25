"""JDLZ decompressor (EA Black Box LZ variant used by NFS MW 2005 for .lzc files and texture blocks).

Header (16 bytes): "JDLZ" 02 10 00 00, u32 uncompressed size, u32 compressed size.
Two interleaved flag bytes: flags1 bit=1 -> back-reference, flags2 picks the reference encoding.
"""
import struct


def decompress(src, off=0):
    if src[off:off + 4] != b"JDLZ":
        raise ValueError("not a JDLZ block")
    out_len, in_len = struct.unpack_from("<II", src, off + 8)
    out = bytearray(out_len)
    ip, op, end = off + 16, 0, off + in_len
    f1 = f2 = 1
    while ip < end and op < out_len:
        if f1 == 1:
            f1 = src[ip] | 0x100
            ip += 1
        if f2 == 1:
            f2 = src[ip] | 0x100
            ip += 1
        if f1 & 1:
            b0, b1 = src[ip], src[ip + 1]
            ip += 2
            if f2 & 1:
                length = (b1 | ((b0 & 0xF0) << 4)) + 3
                dist = (b0 & 0x0F) + 1
            else:
                dist = (b1 | ((b0 & 0xE0) << 3)) + 17
                length = (b0 & 0x1F) + 3
            for _ in range(min(length, out_len - op)):
                out[op] = out[op - dist]
                op += 1
            f2 >>= 1
        else:
            out[op] = src[ip]
            op += 1
            ip += 1
        f1 >>= 1
    if op != out_len:
        raise ValueError(f"JDLZ short output {op}/{out_len}")
    return bytes(out)
