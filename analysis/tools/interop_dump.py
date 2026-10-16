#!/usr/bin/env python3
"""Dump the members of a type from a BepInEx interop (or any .NET) DLL.

usage: interop_dump.py DLL.dll TypeName [name-filter]

Prints properties (accessibility of the getter + type) and fields. The BepInEx IL2CPP interop
assemblies expose the game's fields as properties, so this is how you see the game's data layout
without a decompiler - the stripped copies in lib/refs work too.
Needs: python3 -m pip install dnfile
"""
import sys
import dnfile

if len(sys.argv) < 3:
    sys.exit(__doc__)

path, want = sys.argv[1], sys.argv[2]
name_filter = sys.argv[3] if len(sys.argv) > 3 else ''

pe = dnfile.dnPE(path)
md = pe.net.mdtables

PRIM = {0x01: 'void', 0x02: 'bool', 0x03: 'char', 0x04: 'sbyte', 0x05: 'byte', 0x06: 'short', 0x07: 'ushort',
        0x08: 'int', 0x09: 'uint', 0x0A: 'long', 0x0B: 'ulong', 0x0C: 'float', 0x0D: 'double', 0x0E: 'string',
        0x16: 'typedref', 0x1C: 'object'}


def deref(x):
    return x.row if hasattr(x, 'row') else x


def rd(blob, i):  # compressed uint (ECMA-335 II.23.2.5)
    a = blob[i]; i += 1
    if a < 0x80:
        return a, i
    if a & 0x40 == 0:
        return ((a & 0x3F) << 8) | blob[i], i + 1
    return ((a & 0x1F) << 24) | int.from_bytes(blob[i:i + 4], 'big'), i + 4


def tdor_name(tok):  # TypeDefOrRef coded index -> type name
    table, rid = tok & 3, tok >> 2
    try:
        r = md.TypeDef.rows[rid - 1] if table == 0 else md.TypeRef.rows[rid - 1]
        ns = str(r.TypeNamespace or '')
        return (ns + '.' if ns else '') + str(r.TypeName)
    except Exception:
        return '?'


def sig_type(blob, i):  # minimal signature parser -> (name, next index)
    b = blob[i]; i += 1
    if b in (0x12, 0x11):  # CLASS / VALUETYPE
        v, i = rd(blob, i)
        return tdor_name(v), i
    if b == 0x1D:  # SZARRAY
        t, i = sig_type(blob, i)
        return t + '[]', i
    if b == 0x15:  # GENERICINST: element is a TypeDefOrRef token, then the type arguments
        tok, i = rd(blob, i)
        base = tdor_name(tok)
        n, i = rd(blob, i)
        args = []
        for _ in range(n):
            a, i = sig_type(blob, i)
            args.append(a)
        return f'{base}<{", ".join(args)}>', i
    if b == 0x0F:  # PTR
        t, i = sig_type(blob, i)
        return t + '*', i
    return PRIM.get(b, f'<0x{b:02X}>'), i


def accessibility(flags, kind):
    for name, attr in (('public', 'Public'), ('private', 'Private'), ('protected', 'Family'),
                       ('internal', 'Assembly'), ('private protected', 'FamANDAssem'),
                       ('protected internal', 'FamORAssem')):
        if getattr(flags, f'md{attr}', None) is not None:  # method flags
            if getattr(flags, f'md{attr}', False):
                return name
        if getattr(flags, f'fd{attr}', None) is not None:  # field flags
            if getattr(flags, f'fd{attr}', False):
                return name
    return '?'


for td in (t for t in md.TypeDef.rows if str(t.TypeName) == want):
    ns = str(td.TypeNamespace or '')
    base = str(td.Extends.row.TypeName) if td.Extends else '?'
    print(f'=== {ns}.{want} : {base}')
    methods = [deref(m) for m in td.MethodList] if td.MethodList else []
    if getattr(md, 'PropertyMap', None):
        for pm in md.PropertyMap.rows:
            if pm.Parent is None or deref(pm.Parent) is not td:
                continue
            for p in (deref(x) for x in pm.PropertyList):
                if name_filter and name_filter not in str(p.Name):
                    continue
                blob = p.Type.value_bytes()
                _, i = rd(blob, 1)  # PROPERTY flag + param count
                try:
                    ty, _ = sig_type(blob, i)
                except Exception:
                    ty = '<sig?>'
                acc = '?'
                for m in methods:
                    if str(m.Name) == 'get_' + str(p.Name):
                        acc = accessibility(m.Flags, 'method')
                print(f'  {acc} {ty} {p.Name} {{ get; }}')
    for f in (deref(x) for x in td.FieldList) if td.FieldList else []:
        if name_filter and name_filter not in str(f.Name):
            continue
        if str(f.Name).startswith(('NativeFieldInfoPtr', 'NativeMethodInfoPtr')):
            continue
        try:
            ty, _ = sig_type(f.Signature.value_bytes(), 1)
        except Exception:
            ty = '<sig?>'
        st = ' static' if f.Flags.fdStatic else ''
        print(f'  {accessibility(f.Flags, "field")}{st} {ty} {f.Name}')
    if getattr(md, 'MethodDef', None):
        print('  -- methods --')
        for m in methods:
            if name_filter and name_filter not in str(m.Name):
                continue
            name = str(m.Name)
            if name.startswith(('get_', 'set_')):
                continue
            try:
                blob = m.Signature.value_bytes()
                ret, i = sig_type(blob, 1)  # 0 = calling convention
                pc, i = rd(blob, i)
                args = []
                for _ in range(pc):
                    t, i = sig_type(blob, i)
                    args.append(t)
            except Exception:
                args = ['?']
            st = ' static' if m.Flags.mdStatic else ''
            print(f'  {accessibility(m.Flags, "method")}{st} {ret} {name}({", ".join(args)})')
