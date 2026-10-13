"""Estimate Actor.isPlayer checks (byte read at [reg+0x1B]) in selected classes.

Heuristic: offset 0x1B is also used by other classes' bool fields, so this is an
upper-bound estimate, limited to classes whose methods mostly handle Actors.

usage: python -I isplayer_refs.py GameAssembly.dll decompiled/cs/Assembly-CSharp out_dir
"""
import json
import os
import sys
from collections import defaultdict

import capstone
import pefile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from player_refs import method_table  # noqa: E402

IS_PLAYER_OFFSET = 0x1B
CLASSES = {"NewAIController", "Human", "Citizen", "Actor", "CitizenBehaviour", "MurderController",
           "InteractionController", "Interactable", "NewDoor", "SessionData", "GameplayController",
           "StatusController", "ActionController", "Case", "CaseComponent", "EvidenceCreator", "NewGameLocation"}


def main():
    dll, cs_root, out_dir = sys.argv[1:4]
    pe = pefile.PE(dll, fast_load=True)
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    md.detail = True
    hits = defaultdict(int)
    for rva, length, name in method_table(cs_root):
        cls = name.split("::")[0].split(".")[0]
        if cls not in CLASSES or length == 0:
            continue
        code = pe.get_data(rva, length)
        for ins in md.disasm(code, rva):
            if ins.mnemonic not in ("cmp", "movzx", "mov", "test"):
                continue
            for op in ins.operands:
                if op.type == capstone.x86.X86_OP_MEM and op.size == 1 and op.mem.disp == IS_PLAYER_OFFSET \
                        and op.mem.base not in (0, capstone.x86.X86_REG_RIP, capstone.x86.X86_REG_RSP):
                    hits[name] += 1
    rows = sorted(({"method": k, "class": k.split("::")[0].split(".")[0], "isPlayerReads": v} for k, v in hits.items()),
                  key=lambda r: r["method"])
    json.dump(rows, open(os.path.join(out_dir, "isplayer_refs.json"), "w"), indent=1)
    by_class = defaultdict(int)
    for r in rows:
        by_class[r["class"]] += 1
    print(f"methods with byte reads at +0x1B (likely isPlayer): {len(rows)}, reads: {sum(hits.values())}")
    for c, n in sorted(by_class.items(), key=lambda kv: -kv[1]):
        print(f"  {n:4d}  {c}")


if __name__ == "__main__":
    main()
