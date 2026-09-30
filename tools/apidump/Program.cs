// Dumps the public types/members of .NET assemblies without loading them,
// so we can inspect the SDR# plugin API (net9 reference DLLs) from a net8 tool.
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

foreach (var path in args)
{
    using var fs = File.OpenRead(path);
    using var pe = new PEReader(fs);
    var md = pe.GetMetadataReader();
    var prov = new SigProvider(md);
    Console.WriteLine($"##### {Path.GetFileName(path)}");
    foreach (var th in md.TypeDefinitions)
    {
        var t = md.GetTypeDefinition(th);
        var vis = t.Attributes & TypeAttributes.VisibilityMask;
        if (vis != TypeAttributes.Public && vis != TypeAttributes.NestedPublic) continue;
        string kind = (t.Attributes & TypeAttributes.Interface) != 0 ? "interface" : "type";
        string baseT = t.BaseType.IsNil ? "" : " : " + prov.Name(t.BaseType);
        var ifs = t.GetInterfaceImplementations().Select(i => prov.Name(md.GetInterfaceImplementation(i).Interface)).ToList();
        Console.WriteLine($"{kind} {md.GetString(t.Namespace)}.{md.GetString(t.Name)}{baseT} {(ifs.Count > 0 ? "[" + string.Join(",", ifs) + "]" : "")}");
        foreach (var fh in t.GetFields())
        {
            var f = md.GetFieldDefinition(fh);
            if ((f.Attributes & FieldAttributes.FieldAccessMask) != FieldAttributes.Public) continue;
            var name = md.GetString(f.Name);
            if (name == "value__") continue;
            Console.WriteLine($"    field {f.DecodeSignature(prov, null)} {name}");
        }
        foreach (var mh in t.GetMethods())
        {
            var m = md.GetMethodDefinition(mh);
            if ((m.Attributes & MethodAttributes.MemberAccessMask) != MethodAttributes.Public) continue;
            var sig = m.DecodeSignature(prov, null);
            var names = m.GetParameters().Select(p => md.GetParameter(p)).Where(p => p.SequenceNumber > 0)
                .OrderBy(p => p.SequenceNumber).Select(p => md.GetString(p.Name)).ToList();
            var ps = sig.ParameterTypes.Select((pt, i) => pt + " " + (i < names.Count ? names[i] : ""));
            Console.WriteLine($"    {sig.ReturnType} {md.GetString(m.Name)}({string.Join(", ", ps)})");
        }
    }
}

class SigProvider : ISignatureTypeProvider<string, object>
{
    readonly MetadataReader _md;
    public SigProvider(MetadataReader md) { _md = md; }
    public string Name(EntityHandle h) => h.Kind switch
    {
        HandleKind.TypeDefinition => GetTypeFromDefinition(_md, (TypeDefinitionHandle)h, 0),
        HandleKind.TypeReference => GetTypeFromReference(_md, (TypeReferenceHandle)h, 0),
        HandleKind.TypeSpecification => GetTypeFromSpecification(_md, null, (TypeSpecificationHandle)h, 0),
        _ => "?"
    };
    public string GetArrayType(string e, ArrayShape s) => e + "[,]";
    public string GetByReferenceType(string e) => "ref " + e;
    public string GetFunctionPointerType(MethodSignature<string> s) => "fnptr";
    public string GetGenericInstantiation(string g, ImmutableArray<string> a) => g + "<" + string.Join(",", a) + ">";
    public string GetGenericMethodParameter(object c, int i) => "!!" + i;
    public string GetGenericTypeParameter(object c, int i) => "!" + i;
    public string GetModifiedType(string m, string u, bool r) => u;
    public string GetPinnedType(string e) => e;
    public string GetPointerType(string e) => e + "*";
    public string GetPrimitiveType(PrimitiveTypeCode t) => t.ToString();
    public string GetSZArrayType(string e) => e + "[]";
    public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k) => r.GetString(r.GetTypeDefinition(h).Name);
    public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k) => r.GetString(r.GetTypeReference(h).Name);
    public string GetTypeFromSpecification(MetadataReader r, object c, TypeSpecificationHandle h, byte k) => r.GetTypeSpecification(h).DecodeSignature(this, c);
}
