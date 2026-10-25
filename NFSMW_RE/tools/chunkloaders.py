"""Find bChunkLoader registrations in speed.exe: static initializers doing
push unloader; push loader; push chunk_id; mov ecx, obj; call bChunkLoader::bChunkLoader.

usage: python -I chunkloaders.py <speed.exe>
"""
import sys

import capstone
import pefile

pe = pefile.PE(sys.argv[1])
base = pe.OPTIONAL_HEADER.ImageBase
text = next(s for s in pe.sections if s.Name.startswith(b".text"))
code = text.get_data()
va0 = base + text.VirtualAddress
md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)

# the ctor is whichever call target follows "push 0x80134000" (solid list loader)
needle = b"\x68\x00\x40\x13\x80"
pos = code.find(needle)
win = list(md.disasm(code[pos - 32:pos + 48], va0 + pos - 32))
ctor = next(int(i.op_str, 16) for i in win if i.mnemonic == "call" and i.address > va0 + pos)
print(f"bChunkLoader ctor = {ctor:#x}")

rows = []
for i in range(len(code) - 5):
    if code[i] != 0xE8:
        continue
    tgt = va0 + i + 5 + int.from_bytes(code[i + 1:i + 5], "little", signed=True)
    if tgt != ctor:
        continue
    pushes = []
    for ins in md.disasm(code[i - 24:i + 5], va0 + i - 24):
        if ins.mnemonic == "push" and ins.op_str.startswith("0x"):
            pushes.append(int(ins.op_str, 16))
    if len(pushes) >= 3:
        unload, load, cid = pushes[-3:]
        rows.append((cid, load, unload, va0 + i))
for cid, load, unload, at in sorted(rows):
    print(f"{cid:08X} loader={load:#x} unloader={unload:#x} reg@{at:#x}")
