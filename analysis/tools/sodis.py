"""Annotated disassembly of a game method.

usage: python sodis.py 'Actor::SightingCheck' [--fields Actor,Human,...] [--raw]
       python dis.py 0x5E76F0

Annotates: direct call targets (method names), RIP-relative metadata globals
(class/method/field/string literal), and [reg+disp] field offsets for the given classes.
"""
import argparse
import json
import os
import re
import struct
import sys

import capstone
import pefile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
sys.path.insert(0, HERE)
sys.path.insert(0, os.path.join(ROOT, "analysis"))
from meta import Binary, Metadata  # noqa: E402

DLL = os.path.join(ROOT, "game", "GameAssembly.dll")
META = os.path.join(ROOT, "game", "global-metadata.dat")
CS = os.path.join(ROOT, "decompiled", "cs", "Assembly-CSharp")
CACHE = os.path.join(HERE, "cache.json")


def build_cache():
    from player_refs import method_table
    methods = method_table(CS)
    # field offsets per class: class -> {offset: name}
    fields = {}
    parents = {}
    cls_re = re.compile(r'^(\t*)(?:public |private |internal |protected |sealed |abstract |static |partial |new )*(?:class|struct) (\w+)(?:\s*:\s*([\w.]+))?')
    off_re = re.compile(r'\[FieldOffset\(Offset = "0x([0-9A-F]+)"\)\]')
    decl_re = re.compile(r'^\t*(?:public |private |internal |protected |readonly |new )*([\w<>\[\],. ]+?) (\w+);')
    for dp, _, fs in os.walk(os.path.join(ROOT, "decompiled", "cs")):
        for fn in fs:
            if not fn.endswith(".cs"):
                continue
            lines = open(os.path.join(dp, fn), encoding="utf-8").read().split("\n")
            stack = []
            for i, line in enumerate(lines):
                m = cls_re.match(line)
                if m:
                    depth = len(m.group(1))
                    stack = [s for s in stack if s[0] < depth] + [(depth, m.group(2))]
                    cname = ".".join(s[1] for s in stack)
                    if m.group(3):
                        parents[cname] = m.group(3).split(".")[-1]
                    continue
                m = off_re.search(line)
                if m and stack:
                    j = i + 1
                    while j < len(lines) and lines[j].strip().startswith("["):
                        j += 1
                    dm = decl_re.match(lines[j]) if j < len(lines) else None
                    if dm:
                        cname = ".".join(s[1] for s in stack)
                        fields.setdefault(cname, {})[int(m.group(1), 16)] = f"{dm.group(1).split('.')[-1]} {dm.group(2)}"
    json.dump({"methods": methods, "fields": fields, "parents": parents}, open(CACHE, "w"))


def load():
    if not os.path.exists(CACHE):
        build_cache()
    c = json.load(open(CACHE))
    return c["methods"], c["fields"], c["parents"]


def class_chain(cls, parents):
    out = []
    while cls and cls not in out:
        out.append(cls)
        cls = parents.get(cls)
    return out


ICALLS = {0x306200: "il2cpp_resolve_icall", 0x34fe40: "il2cpp_init_metadata"}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("target")
    ap.add_argument("--fields", default="")
    ap.add_argument("--raw", action="store_true")
    ap.add_argument("--list", action="store_true", help="only list matching methods")
    a = ap.parse_args()
    methods, fields, parents = load()
    by_rva = {m[0]: m for m in methods}
    if a.target.startswith("0x"):
        targets = [by_rva[int(a.target, 16)]]
    else:
        targets = [m for m in methods if m[2] == a.target or (a.target.endswith("*") and m[2].startswith(a.target[:-1]))]
    if a.list or len(targets) > 1:
        for m in targets:
            print(f"{m[0]:#x} {m[1]:#x} {m[2]}")
        if a.list or not targets:
            return
    if not targets:
        sys.exit("no such method")
    rva, length, name = targets[0]
    cls = name.split("::")[0]
    hint = []
    for c in ([cls] + [x for x in a.fields.split(",") if x]):
        for cc in class_chain(c, parents):
            if cc not in hint:
                hint.append(cc)
    bn = Binary(DLL, Metadata(META))
    pe = pefile.PE(DLL, fast_load=True)
    names = bn.names
    code = pe.get_data(rva, length)
    cs = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    cs.detail = True
    print(f"; {name} rva={rva:#x} len={length:#x}  field hints: {','.join(hint)}")
    for ins in cs.disasm(code, rva):
        note = []
        if ins.mnemonic in ("call", "jmp") and ins.operands and ins.operands[0].type == capstone.x86.X86_OP_IMM:
            t = ins.operands[0].imm
            if t in by_rva:
                note.append(by_rva[t][2])
            elif t in names:
                note.append(names[t])
            elif t in ICALLS:
                note.append(ICALLS[t])
            elif not (rva <= t < rva + length):
                note.append(f"sub_{t:x}")
        for op in ins.operands:
            if op.type != capstone.x86.X86_OP_MEM:
                continue
            if op.mem.base == capstone.x86.X86_REG_RIP:
                addr = ins.address + ins.size + op.mem.disp
                try:
                    v = struct.unpack("<Q", pe.get_data(addr, 8))[0]
                except Exception:
                    v = None
                dec = bn.decode(v) if v is not None else None
                if not dec and 0x3330000 <= addr < 0x3C7C000:
                    raw = bn.img[addr:addr + 120].split(b"\0")[0]
                    if len(raw) >= 4 and all(32 <= c < 127 for c in raw):
                        dec = '"' + raw.decode() + '"'
                note.append(dec or f"g_{addr:x}")
            elif op.mem.disp and not a.raw:
                hits = [f"{c}.{fields[c][op.mem.disp].split(' ')[-1]}" for c in hint if op.mem.disp in fields.get(c, {})]
                if hits:
                    note.append("|".join(hits[:3]))
        print(f"{ins.address:#x}: {ins.mnemonic:6} {ins.op_str}" + (f"   ; {'; '.join(note)}" if note else ""))


if __name__ == "__main__":
    main()
