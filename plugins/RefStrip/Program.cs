using System;
using System.IO;
using Mono.Cecil;
using Mono.Cecil.Cil;

// usage: RefStrip OUT_DIR IN.dll [IN.dll ...]
// Every method body becomes "throw null", private members of classes and embedded resources are dropped;
// types, the accessible members and their signatures stay. Struct fields stay (they decide the layout).
if (args.Length < 2) { Console.Error.WriteLine("usage: RefStrip OUT_DIR IN.dll [IN.dll ...]"); return 2; }
var outDir = args[0];
Directory.CreateDirectory(outDir);
var resolver = new DefaultAssemblyResolver();
foreach (var input in args[1..]) resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(input)));

foreach (var input in args[1..])
{
    using var asm = AssemblyDefinition.ReadAssembly(input, new ReaderParameters { AssemblyResolver = resolver });
    int methods = 0;
    foreach (var module in asm.Modules)
    {
        module.Resources.Clear();
        foreach (var type in module.GetTypes())
        {
            if (!type.IsValueType)
                for (int i = type.Fields.Count - 1; i >= 0; i--)
                    if (type.Fields[i].IsPrivate) type.Fields.RemoveAt(i);
            for (int i = type.Methods.Count - 1; i >= 0; i--)
            {
                var pm = type.Methods[i];
                if (pm.IsPrivate && !pm.IsConstructor && !pm.HasOverrides && pm.SemanticsAttributes == MethodSemanticsAttributes.None)
                    type.Methods.RemoveAt(i);
            }
            foreach (var m in type.Methods)
            {
                if (!m.HasBody) continue;
                var body = new MethodBody(m);
                var il = body.GetILProcessor();
                il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Throw);
                m.Body = body;
                methods++;
            }
        }
    }
    var output = Path.Combine(outDir, Path.GetFileName(input));
    asm.Write(output);
    Console.WriteLine($"{Path.GetFileName(input)}: {methods} bodies stripped, {new FileInfo(input).Length / 1024} -> {new FileInfo(output).Length / 1024} KB");
}
return 0;
