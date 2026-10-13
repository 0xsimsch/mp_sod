"""Compare two Shadows of Doubt .cit city files (JSON), ignoring volatile fields.

usage: python -I compare_cit.py A.cit B.cit
"""
import json
import sys
from collections import Counter

IGNORE_KEYS = {"date"}
FLOAT_EPS = 1e-4
MAX_EXAMPLES = 25


def load(path):
    with open(path, "rb") as f:
        raw = f.read()
    if raw[:1] != b"{":
        sys.exit(f"{path}: not plain JSON (starts with {raw[:8]!r}); add decompression")
    return json.loads(raw)


def walk(a, b, path, out):
    if isinstance(a, dict) and isinstance(b, dict):
        for k in a.keys() | b.keys():
            if k in IGNORE_KEYS:
                continue
            if k not in a or k not in b:
                out.append((f"{path}.{k}", "missing key", k in a, k in b))
                continue
            walk(a[k], b[k], f"{path}.{k}", out)
    elif isinstance(a, list) and isinstance(b, list):
        if len(a) != len(b):
            out.append((path, "list length", len(a), len(b)))
        for i, (x, y) in enumerate(zip(a, b)):
            walk(x, y, f"{path}[{i}]", out)
    elif isinstance(a, float) or isinstance(b, float):
        if not (isinstance(a, (int, float)) and isinstance(b, (int, float))) or abs(a - b) > FLOAT_EPS:
            out.append((path, "value", a, b))
    elif a != b:
        out.append((path, "value", a, b))


def bucket(path):
    """Collapse list indices so differences group by field, e.g. .citizens[].name"""
    out, skip = [], False
    for ch in path:
        if ch == "[":
            skip = True
            out.append("[]")
        elif ch == "]":
            skip = False
        elif not skip:
            out.append(ch)
    return "".join(out)


def main():
    a, b = load(sys.argv[1]), load(sys.argv[2])
    print("top-level keys:", sorted(a.keys()))
    for k in sorted(a.keys()):
        v = a[k]
        if isinstance(v, list):
            print(f"  {k}: {len(v)} vs {len(b.get(k, []))} entries")
    diffs = []
    walk(a, b, "", diffs)
    print(f"\nTOTAL DIFFERENCES (ignoring {sorted(IGNORE_KEYS)}): {len(diffs)}")
    if not diffs:
        print("IDENTICAL")
        return
    print("\nby field:")
    for field, n in Counter(bucket(d[0]) for d in diffs).most_common(40):
        print(f"  {n:8d}  {field}")
    print("\nexamples:")
    for d in diffs[:MAX_EXAMPLES]:
        print("  ", d[0], d[1], repr(d[2])[:80], "|", repr(d[3])[:80])


if __name__ == "__main__":
    main()
