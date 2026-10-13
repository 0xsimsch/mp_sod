"""Find every game method that references Player.Instance (directly or inlined).

usage: python -I player_refs.py GameAssembly.dll decompiled/cs/Assembly-CSharp out_dir
"""
import bisect
import json
import os
import re
import struct
import sys
from collections import Counter, defaultdict

import pefile

GET_INSTANCE_RVA = 0x44C390      # Player.get_Instance
PLAYER_TYPEINFO_RVA = 0x3D97150  # global read by get_Instance (Player class pointer)


def method_table(cs_root):
    """[(rva, length, 'Class.Method')] from Cpp2IL's [Address] attributes."""
    addr_re = re.compile(r'\[Address\(RVA = "0x([0-9A-F]+)", Offset = "0x[0-9A-F]+", Length = "0x([0-9A-F]+)"\)\]')
    class_re = re.compile(r'^(\t*)(?:public |private |internal |protected |sealed |abstract |static |partial |new )*(?:class|struct) (\w+)')
    prop_re = re.compile(r'^\t*(?:public |private |internal |protected |static |virtual |override |new |abstract )*[\w<>\[\],. ]+ (\w+)\s*$')
    name_re = re.compile(r'(\w+)\s*(?:<[^>]*>)?\s*\(')
    out = []
    for dirpath, _, files in os.walk(cs_root):
        for fn in files:
            if not fn.endswith(".cs"):
                continue
            lines = open(os.path.join(dirpath, fn), encoding="utf-8").read().split("\n")
            stack, last_prop = [], None
            for i, line in enumerate(lines):
                m = class_re.match(line)
                if m:
                    depth = len(m.group(1))
                    stack = [s for s in stack if s[0] < depth] + [(depth, m.group(2))]
                    continue
                pm = prop_re.match(line)
                if pm and i + 1 < len(lines) and lines[i + 1].strip() == "{":
                    last_prop = pm.group(1)
                m = addr_re.search(line)
                if not m:
                    continue
                j = i + 1
                while j < len(lines) and lines[j].strip().startswith("["):
                    j += 1
                raw = lines[j] if j < len(lines) else "?"
                sig = raw.strip()
                indent = len(raw) - len(raw.lstrip("\t"))
                if sig in ("get", "set", "add", "remove"):
                    indent -= 1  # accessor sits one level inside its property
                owners = [s for s in stack if s[0] < indent]
                if sig in ("get", "set", "add", "remove"):
                    name = f"{last_prop}.{sig}"
                else:
                    nm = name_re.search(sig)
                    name = nm.group(1) if nm else sig[:40]
                cls = ".".join(s[1] for s in owners) or fn[:-3]
                out.append((int(m.group(1), 16), int(m.group(2), 16), f"{cls}::{name}"))
    out.sort()
    return out


def main():
    dll, cs_root, out_dir = sys.argv[1:4]
    os.makedirs(out_dir, exist_ok=True)
    pe = pefile.PE(dll, fast_load=True)
    # Game code lives in the "il2cpp" section; .text holds the runtime. Scan both.
    code_sections = [s for s in pe.sections if s.Name.rstrip(b"\0") in (b".text", b"il2cpp")]

    methods = method_table(cs_root)
    starts = [m[0] for m in methods]
    print(f"{len(methods)} Assembly-CSharp methods with addresses")

    def owner(rva):
        k = bisect.bisect_right(starts, rva) - 1
        if k >= 0 and methods[k][0] <= rva < methods[k][0] + methods[k][1]:
            return methods[k][2]
        return None

    calls, loads = defaultdict(int), defaultdict(int)
    unowned = Counter()
    for sec in code_sections:
        scan(sec.VirtualAddress, sec.get_data(), owner, calls, loads, unowned)

    print(f"hits outside known Assembly-CSharp methods: {dict(unowned)}")
    report(rows_from(calls, loads), out_dir)


def scan(base, data, owner, calls, loads, unowned):
    # call rel32 to get_Instance
    for m in re.finditer(rb"\xE8", data):
        p = m.start()
        if p + 5 > len(data):
            continue
        rel = struct.unpack_from("<i", data, p + 1)[0]
        if base + p + 5 + rel == GET_INSTANCE_RVA:
            o = owner(base + p)
            if o: calls[o] += 1
            else: unowned["call"] += 1
    # mov r64, [rip+disp32] -> REX.W (48/4C) 8B modrm(mod=00, rm=101) disp32
    for m in re.finditer(rb"[\x48\x4C]\x8B[\x05\x0D\x15\x1D\x25\x2D\x35\x3D]", data):
        p = m.start()
        if p + 7 > len(data):
            continue
        disp = struct.unpack_from("<i", data, p + 3)[0]
        if base + p + 7 + disp == PLAYER_TYPEINFO_RVA:
            o = owner(base + p)
            if o: loads[o] += 1
            else: unowned["typeinfo"] += 1


def rows_from(calls, loads):
    every = sorted(set(calls) | set(loads))
    return [{"method": m, "class": m.split("::")[0].split(".")[0], "calls": calls.get(m, 0), "typeinfoLoads": loads.get(m, 0)} for m in every]


def report(rows, out_dir):
    json.dump(rows, open(os.path.join(out_dir, "player_refs.json"), "w"), indent=1)

    by_class = defaultdict(lambda: [0, 0, 0])
    for r in rows:
        c = by_class[r["class"]]
        c[0] += 1; c[1] += r["calls"]; c[2] += r["typeinfoLoads"]
    with open(os.path.join(out_dir, "player_refs_by_class.tsv"), "w") as f:
        f.write("class\tmethods\tcalls_get_Instance\ttypeinfo_loads\n")
        for cls, (m, c, l) in sorted(by_class.items(), key=lambda kv: -kv[1][0]):
            f.write(f"{cls}\t{m}\t{c}\t{l}\n")

    print(f"methods referencing Player: {len(rows)} "
          f"(explicit get_Instance calls in {sum(1 for r in rows if r['calls'])}, "
          f"Player class-global loads in {sum(1 for r in rows if r['typeinfoLoads'])})")
    print(f"total call sites: {sum(r['calls'] for r in rows)}, total class-global loads: {sum(r['typeinfoLoads'] for r in rows)}")
    print(f"classes involved: {len(by_class)}")
    print("\ntop classes by number of methods:")
    for cls, (m, c, l) in sorted(by_class.items(), key=lambda kv: -kv[1][0])[:40]:
        print(f"  {m:4d} methods  {c:4d} calls  {l:4d} loads  {cls}")


if __name__ == "__main__":
    main()
