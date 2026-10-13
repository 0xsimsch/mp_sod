"""Find methods that load a class global and use a given field displacement (rough field xref).

usage: python xref.py CityData 0x180          # methods touching CityData.<0x180>
       python xref.py --call 0x5f0540         # direct callers of an RVA
"""
import os
import struct
import sys

import capstone

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from sodis import CS, DLL, META, load  # noqa: E402
from meta import Binary, Metadata  # noqa: E402


def main():
    args = sys.argv[1:]
    methods, _, _ = load()
    bn = Binary(DLL, Metadata(META))
    cs = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    cs.detail = True
    call_mode = args[0] == "--call"
    target = int(args[1], 16)
    want_cls = None if call_mode else "class " + args[0]
    for rva, length, name in methods:
        if not length:
            continue
        code = bn.img[rva:rva + length]
        if call_mode:
            if any(ins.mnemonic in ("call", "jmp") and ins.op_str == hex(target) for ins in cs.disasm(code, rva)):
                print(f"{rva:#x} {name}")
            continue
        has_cls, hits = False, 0
        for ins in cs.disasm(code, rva):
            for op in ins.operands:
                if op.type != capstone.x86.X86_OP_MEM:
                    continue
                if op.mem.base == capstone.x86.X86_REG_RIP:
                    a = ins.address + ins.size + op.mem.disp
                    if bn.decode(struct.unpack_from("<Q", bn.img, a)[0]) == want_cls:
                        has_cls = True
                elif op.mem.disp == target:
                    hits += 1
        if has_cls and hits:
            print(f"{rva:#x} {hits:3} {name}")


if __name__ == "__main__":
    main()
