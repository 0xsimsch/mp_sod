"""Minimal reader for global-metadata.dat (v31) + IL2CPP metadata-usage decoding.

IL2CPP (v27+) stores each metadata usage as a pointer-sized global in .data whose
*initial* value is an encoded token: (usageType << 29) | (index << 1) | 1.
  1 TypeInfo (index = Il2CppType index), 2 Il2CppType, 3 MethodDef,
  4 FieldInfo (index into fieldRefs), 5 StringLiteral, 6 MethodRef (generic, unresolved here)
"""
import struct

NAMES = ("stringLiteral stringLiteralData string events properties methods parameterDefaultValues "
         "fieldDefaultValues fieldAndParameterDefaultValueData fieldMarshaledSizes parameters fields "
         "genericParameters genericParameterConstraints genericContainers nestedTypes interfaces "
         "vtableMethods interfaceOffsets typeDefinitions images assemblies fieldRefs").split()


class Metadata:
    def __init__(self, path):
        d = self.d = open(path, "rb").read()
        h = struct.unpack_from("<2I" + "i" * 2 * len(NAMES), d, 0)
        self.sec = {n: (h[2 + 2 * i], h[3 + 2 * i]) for i, n in enumerate(NAMES)}
        # type definitions (88 bytes)
        off, size = self.sec["typeDefinitions"]
        self.types = []
        self.byval = {}
        for i in range(size // 88):
            f = struct.unpack_from("<16i8H2I", d, off + i * 88)
            t = dict(name=self.s(f[0]), ns=self.s(f[1]), byval=f[2], declaring=f[3], parent=f[4],
                     fieldStart=f[8], methodStart=f[9], method_count=f[16], field_count=f[18])
            self.types.append(t)
            self.byval[f[2]] = i
        # methods (36 bytes)
        off, size = self.sec["methods"]
        self.methods = []
        for i in range(size // 36):
            f = struct.unpack_from("<7i4H", d, off + i * 36)
            self.methods.append((self.s(f[0]), f[1]))
        # fields (12 bytes)
        off, size = self.sec["fields"]
        self.fields = [struct.unpack_from("<3i", d, off + i * 12) for i in range(size // 12)]
        off, size = self.sec["fieldRefs"]
        self.fieldRefs = [struct.unpack_from("<2i", d, off + i * 8) for i in range(size // 8)]
        off, size = self.sec["stringLiteral"]
        self.lits = [struct.unpack_from("<2I", d, off + i * 8) for i in range(size // 8)]

    def s(self, idx):
        off = self.sec["string"][0] + idx
        return self.d[off:self.d.index(b"\0", off)].decode("utf-8", "replace")

    def type_name(self, tdi):
        t = self.types[tdi]
        n = t["name"]
        while t["declaring"] >= 0:
            # declaring is an Il2CppType index
            di = self.byval.get(t["declaring"])
            if di is None:
                break
            t = self.types[di]
            n = t["name"] + "." + n
        return (t["ns"] + "." if t["ns"] else "") + n

    def literal(self, i):
        length, data = self.lits[i]
        off = self.sec["stringLiteralData"][0] + data
        return self.d[off:off + length].decode("utf-8", "replace")

    def decode(self, v):
        """Decode an encoded metadata-usage value; None if it isn't one."""
        if not v & 1 or v >> 32:
            return None
        kind, idx = (v >> 29) & 7, (v & 0x1FFFFFFF) >> 1
        try:
            if kind == 1 or kind == 2:
                tdi = self.byval.get(idx)
                return ("class " if kind == 1 else "type ") + (self.type_name(tdi) if tdi is not None else f"#type{idx}")
            if kind == 3:
                name, decl = self.methods[idx]
                return f"method {self.type_name(decl)}::{name}"
            if kind == 4:
                ti, fi = self.fieldRefs[idx]
                tdi = self.byval.get(ti)
                if tdi is None:
                    return f"field #type{ti}.{fi}"
                t = self.types[tdi]
                return f"field {self.type_name(tdi)}.{self.s(self.fields[t['fieldStart'] + fi][0])}"
            if kind == 5:
                return "str " + repr(self.literal(idx))[:80]
            if kind == 6:
                return f"methodref #{idx}"
        except (IndexError, ValueError):
            return None
        return None


# ---------------------------------------------------------------- binary side
import pefile  # noqa: E402

CODE_REG_RVA = 0x33EC2B0      # Il2CppCodeRegistration (v42.04)
META_REG_RVA = 0x3869BC0      # Il2CppMetadataRegistration (v42.04)
PRIM = {1: "void", 2: "bool", 3: "char", 4: "sbyte", 5: "byte", 6: "short", 7: "ushort", 8: "int", 9: "uint",
        10: "long", 11: "ulong", 12: "float", 13: "double", 14: "string", 0x18: "IntPtr", 0x19: "UIntPtr",
        0x1C: "object"}


class Binary:
    """Maps native RVAs to method names (all images + generic instances) and resolves MethodRef usages."""

    def __init__(self, dll, md):
        self.md = md
        pe = self.pe = pefile.PE(dll, fast_load=True)
        self.base = pe.OPTIONAL_HEADER.ImageBase
        self.img = pe.get_memory_mapped_image()
        q = lambda rva, n=1: struct.unpack_from(f"<{n}Q", self.img, rva)
        self.q = q
        cr = q(CODE_REG_RVA, 18)
        gm_ptrs = cr[3] - self.base
        n_mod, mods = cr[13], cr[14] - self.base
        mr = q(META_REG_RVA, 16)
        self.ginsts = mr[3] - self.base
        gmt_n, gmt = mr[4], mr[5] - self.base
        self.types_n, self.types = mr[6], mr[7] - self.base
        self.specs_n, self.specs = mr[8], mr[9] - self.base
        # images from metadata
        off, size = md.sec["images"]
        images = {}
        for i in range(size // 40):
            f = struct.unpack_from("<10i", md.d, off + i * 40)
            images[md.s(f[0])] = (f[2], f[3])
        self.names = {}
        for i in range(n_mod):
            m = q(mods + 8 * i)[0] - self.base
            name_p, cnt, ptrs = q(m, 3)
            name = self.cstr(name_p - self.base)
            if name not in images:
                continue
            ts, tc = images[name]
            for ti in range(ts, ts + tc):
                t = md.types[ti]
                for mi in range(t["methodStart"], t["methodStart"] + t["method_count"]):
                    tok = struct.unpack_from("<i", md.d, md.sec["methods"][0] + mi * 36 + 24)[0]
                    rid = tok & 0xFFFFFF
                    if 0 < rid <= cnt:
                        p = q(ptrs - self.base + 8 * (rid - 1))[0]
                        if p:
                            self.names.setdefault(p - self.base, f"{md.type_name(ti)}::{md.methods[mi][0]}")
        for i in range(gmt_n):
            spec, midx = struct.unpack_from("<2i", self.img, gmt + 16 * i)
            p = q(gm_ptrs + 8 * midx)[0]
            if p:
                self.names.setdefault(p - self.base, self.spec_name(spec))

    def cstr(self, rva):
        return self.img[rva:self.img.index(b"\0", rva)].decode("utf-8", "replace")

    def type_str(self, tp, depth=0):
        if depth > 4:
            return "?"
        rva = tp - self.base
        data, bits = struct.unpack_from("<QI", self.img, rva)
        kind = (bits >> 16) & 0xFF
        if kind in PRIM:
            return PRIM[kind]
        if kind in (0x11, 0x12):  # valuetype / class: data = typedef index
            return self.md.type_name(data).split(".")[-1] if data < len(self.md.types) else "?"
        if kind == 0x15:  # genericinst
            gc = data - self.base
            tptr, cinst = struct.unpack_from("<2Q", self.img, gc)
            base = self.type_str(tptr, depth + 1)
            return base.split("`")[0] + "<" + self.inst_str(cinst, depth) + ">"
        if kind in (0x1D, 0x14):  # szarray / array
            return self.type_str(data, depth + 1) + "[]" if kind == 0x1D else "array"
        if kind in (0x13, 0x1E):
            return "T"
        return f"t{kind:x}"

    def inst_str(self, inst, depth=0):
        if not inst:
            return ""
        argc, argv = struct.unpack_from("<2Q", self.img, inst - self.base)
        return ",".join(self.type_str(self.q(argv - self.base + 8 * k)[0], depth + 1) for k in range(min(argc, 4)))

    def spec_name(self, spec):
        mdi, cii, mii = struct.unpack_from("<3i", self.img, self.specs + 12 * spec)
        name, decl = self.md.methods[mdi]
        tn = self.md.type_name(decl).split("`")[0]
        if cii >= 0:
            tn += "<" + self.inst_str(self.q(self.ginsts + 8 * cii)[0]) + ">"
        mn = name
        if mii >= 0:
            mn += "<" + self.inst_str(self.q(self.ginsts + 8 * mii)[0]) + ">"
        return f"{tn}::{mn}"

    def type_index_str(self, idx):
        return self.type_str(self.q(self.types + 8 * idx)[0])

    def decode(self, v):
        if v is None or not v & 1 or v >> 32:
            return None
        kind, idx = (v >> 29) & 7, (v & 0x1FFFFFFF) >> 1
        if kind == 6 and idx < self.specs_n:
            return "methodref " + self.spec_name(idx)
        if kind in (1, 2) and idx not in self.md.byval and idx < self.types_n:
            return ("class " if kind == 1 else "type ") + self.type_index_str(idx)
        return self.md.decode(v)
