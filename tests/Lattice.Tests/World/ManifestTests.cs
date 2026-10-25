using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Lattice.Core.Modules;
using Lattice.Core.World;

namespace Lattice.Tests.World;

/// <summary>
/// The world module's manifest names every game member its code uses, and the game has each of them. Reads
/// Lattice.Game.dll's IL for the types in <c>Lattice.Game.World</c> and the game's assemblies on disk
/// (<c>STATIONEERS_DIR</c>); nothing is loaded.
/// </summary>
public sealed class ManifestTests
{
    private static readonly string[] GameAssemblies = ["Assembly-CSharp", "UnityEngine.CoreModule"];

    public static TheoryData<string> Modules() => new() { "world", "saving", "sync", "catalogue", "entry" };

    [Theory]
    [MemberData(nameof(Modules))]
    public void Every_game_member_a_modules_code_uses_is_in_its_manifest(string module)
    {
        var (manifest, ns) = Module(module);
        var listed = manifest.Uses.Select(Key).ToHashSet();
        var declaring = manifest.Uses.Select(m => m.TypeName).ToHashSet();
        var used = Il.GameMembersUsedBy(GamePath(), ns, GameAssemblies);

        var unlisted = used.Where(u => u.Kind == "type" ? !declaring.Contains(u.Type) && !listed.Contains(u.Key) : !listed.Contains(u.Key))
            .Select(u => u.Key).Distinct().Order().ToList();

        Assert.True(unlisted.Count == 0, $"Not in the {module} manifest:\n" + string.Join("\n", unlisted));
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void Every_manifest_member_exists_in_the_game(string module)
    {
        var managed = ManagedDir();
        if (managed == null)
            return;
        var game = new GameMetadata(GameAssemblies.Select(a => Path.Combine(managed, a + ".dll")));
        var missing = Module(module).Manifest.Uses.Where(m => !game.Has(m)).Select(Key).ToList();
        Assert.True(missing.Count == 0, "Not in the game:\n" + string.Join("\n", missing));
    }

    private static (ModuleManifest Manifest, string Namespace) Module(string module) => module switch
    {
        "world" => (WorldManifest.Module, "Lattice.Game.World"),
        "saving" => (Lattice.Core.Saving.SavingManifest.Module, "Lattice.Game.Saving"),
        "sync" => (Lattice.Core.Sync.SyncManifest.Module, "Lattice.Game.Sync"),
        "catalogue" => (Lattice.Core.Catalogue.CatalogueManifest.Module, "Lattice.Game.Catalogue"),
        "entry" => (Lattice.Core.EntryPoint.EntryManifest.Module, "Lattice.Game.EntryPoint"),
        _ => throw new ArgumentOutOfRangeException(nameof(module), module, null),
    };

    [Fact]
    public void A_method_with_other_parameters_is_missing()
    {
        var managed = ManagedDir();
        if (managed == null)
            return;
        var game = new GameMetadata(GameAssemblies.Select(a => Path.Combine(managed, a + ".dll")));
        var wrong = WorldManifest.SetCustomColor with { Parameters = Lattice.Api.ValueList.From(new[] { "System.Int32" }) };
        Assert.True(game.Has(WorldManifest.SetCustomColor));
        Assert.False(game.Has(wrong));
    }

    internal static string Key(GameMember member) => member switch
    {
        GameMember.Type t => $"type {t.FullName}",
        GameMember.Field f => $"field {f.DeclaringType}.{f.Name}",
        GameMember.Property p => $"property {p.DeclaringType}.{p.Name}",
        GameMember.Event e => $"event {e.DeclaringType}.{e.Name}",
        GameMember.Method m => $"method {m.DeclaringType}.{m.Name}({string.Join(",", m.Parameters)})",
        _ => "unknown",
    };

    private static string GamePath()
    {
        var configuration = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)).Parent!.Name;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Lattice.sln")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "src", "Lattice.Game", "bin", configuration, "netstandard2.1", "Lattice.Game.dll");
    }

    internal static string? ManagedDir()
    {
        var root = Environment.GetEnvironmentVariable("STATIONEERS_DIR");
        var managed = root == null ? null : Path.Combine(root, "rocketstation_Data", "Managed");
        return managed != null && File.Exists(Path.Combine(managed, "Assembly-CSharp.dll")) ? managed : null;
    }
}

/// <summary>A game member found in IL: its kind, declaring type and manifest key.</summary>
internal sealed record UsedMember(string Kind, string Type, string Key);

/// <summary>Reads which members of named assemblies a namespace's IL uses.</summary>
internal static class Il
{
    private static readonly Dictionary<short, OpCode> OpCodesByValue =
        typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => (OpCode)f.GetValue(null)!).ToDictionary(o => o.Value);

    public static List<UsedMember> GameMembersUsedBy(string assemblyPath, string ns, string[] assemblies)
    {
        using var pe = new PEReader(File.OpenRead(assemblyPath));
        var reader = pe.GetMetadataReader();
        var names = new Names(reader, assemblies);
        var used = new List<UsedMember>();
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            if (!InNamespace(reader, type, ns))
                continue;
            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                if (method.RelativeVirtualAddress == 0)
                    continue;
                foreach (var token in Tokens(pe.GetMethodBody(method.RelativeVirtualAddress).GetILReader()))
                    names.Add(MetadataTokens.EntityHandle(token), used);
            }

            foreach (var fieldHandle in type.GetFields())
                names.AddSignatureTypes(reader.GetFieldDefinition(fieldHandle).Signature, used, isField: true);
        }

        return used;
    }

    private static bool InNamespace(MetadataReader reader, TypeDefinition type, string ns)
    {
        while (type.IsNested)
            type = reader.GetTypeDefinition(type.GetDeclaringType());
        return reader.GetString(type.Namespace) == ns;
    }

    private static IEnumerable<int> Tokens(BlobReader il)
    {
        var tokens = new List<int>();
        while (il.RemainingBytes > 0)
        {
            short value = il.ReadByte();
            if (value == 0xFE)
                value = unchecked((short)(0xFE00 | il.ReadByte()));
            var op = OpCodesByValue[value];
            switch (op.OperandType)
            {
                case OperandType.InlineMethod:
                case OperandType.InlineField:
                case OperandType.InlineType:
                case OperandType.InlineTok:
                    tokens.Add(il.ReadInt32());
                    break;
                case OperandType.InlineSwitch:
                    il.Offset += 4 * il.ReadInt32();
                    break;
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    il.Offset += 1;
                    break;
                case OperandType.InlineVar:
                    il.Offset += 2;
                    break;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    il.Offset += 8;
                    break;
                default:
                    il.Offset += 4;
                    break;
            }
        }

        return tokens;
    }

    private sealed class Names(MetadataReader reader, string[] assemblies)
    {
        private readonly TypeNames _provider = new(reader);

        public void Add(EntityHandle handle, List<UsedMember> used)
        {
            switch (handle.Kind)
            {
                case HandleKind.MemberReference:
                    AddMember(reader.GetMemberReference((MemberReferenceHandle)handle), used);
                    break;
                case HandleKind.MethodSpecification:
                    Add(reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method, used);
                    break;
                case HandleKind.TypeReference:
                    AddType((TypeReferenceHandle)handle, used);
                    break;
                case HandleKind.TypeSpecification:
                    reader.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(_provider, null)
                        .Games.ForEach(g => AddType(g, used));
                    break;
            }
        }

        public void AddSignatureTypes(BlobHandle signature, List<UsedMember> used, bool isField)
        {
            if (isField)
                reader.GetBlobReader(signature);
            var decoded = new SignatureDecoder<TypeName, object?>(_provider, reader, null).DecodeFieldSignature(ref Blob(signature));
            decoded.Games.ForEach(g => AddType(g, used));
        }

        private BlobReader _blob;

        private ref BlobReader Blob(BlobHandle handle)
        {
            _blob = reader.GetBlobReader(handle);
            return ref _blob;
        }

        private void AddMember(MemberReference member, List<UsedMember> used)
        {
            if (member.Parent.Kind != HandleKind.TypeReference)
            {
                if (member.Parent.Kind == HandleKind.TypeSpecification)
                    reader.GetTypeSpecification((TypeSpecificationHandle)member.Parent).DecodeSignature(_provider, null)
                        .Games.ForEach(g => AddType(g, used));
                return;
            }

            var parent = (TypeReferenceHandle)member.Parent;
            if (!IsGame(parent))
                return;
            var type = _provider.FullName(parent);
            var name = reader.GetString(member.Name);
            if (member.GetKind() == MemberReferenceKind.Field)
            {
                used.Add(new UsedMember("field", type, $"field {type}.{name}"));
                return;
            }

            var signature = member.DecodeMethodSignature(_provider, null);
            foreach (var parameter in signature.ParameterTypes)
                parameter.Games.ForEach(g => AddType(g, used));
            if (name.StartsWith("get_", StringComparison.Ordinal) || name.StartsWith("set_", StringComparison.Ordinal))
                used.Add(new UsedMember("property", type, $"property {type}.{name[4..]}"));
            else if (name.StartsWith("add_", StringComparison.Ordinal))
                used.Add(new UsedMember("event", type, $"event {type}.{name[4..]}"));
            else if (name.StartsWith("remove_", StringComparison.Ordinal))
                used.Add(new UsedMember("event", type, $"event {type}.{name[7..]}"));
            else
                used.Add(new UsedMember("method", type,
                    $"method {type}.{name}({string.Join(",", signature.ParameterTypes.Select(p => p.Name))})"));
        }

        private void AddType(TypeReferenceHandle handle, List<UsedMember> used)
        {
            if (IsGame(handle))
            {
                var name = _provider.FullName(handle);
                used.Add(new UsedMember("type", name, $"type {name}"));
            }
        }

        private bool IsGame(TypeReferenceHandle handle)
        {
            var type = reader.GetTypeReference(handle);
            while (type.ResolutionScope.Kind == HandleKind.TypeReference)
                type = reader.GetTypeReference((TypeReferenceHandle)type.ResolutionScope);
            if (type.ResolutionScope.Kind != HandleKind.AssemblyReference)
                return false;
            var assembly = reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope).Name);
            return Array.IndexOf(assemblies, assembly) >= 0;
        }
    }
}

/// <summary>A decoded type: its reflection-style name and the type references inside it.</summary>
internal sealed record TypeName(string Name, List<TypeReferenceHandle> Games);

/// <summary>Decodes signatures into reflection-style full names (<c>Outer+Inner</c>, <c>System.Int32</c>).</summary>
internal sealed class TypeNames(MetadataReader reader) : ISignatureTypeProvider<TypeName, object?>
{
    public string FullName(TypeReferenceHandle handle)
    {
        var type = reader.GetTypeReference(handle);
        var name = reader.GetString(type.Name);
        if (type.ResolutionScope.Kind == HandleKind.TypeReference)
            return FullName((TypeReferenceHandle)type.ResolutionScope) + "+" + name;
        var ns = reader.GetString(type.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    public TypeName GetPrimitiveType(PrimitiveTypeCode typeCode) => new("System." + typeCode switch
    {
        PrimitiveTypeCode.Boolean => "Boolean",
        PrimitiveTypeCode.Byte => "Byte",
        PrimitiveTypeCode.SByte => "SByte",
        PrimitiveTypeCode.Char => "Char",
        PrimitiveTypeCode.Int16 => "Int16",
        PrimitiveTypeCode.UInt16 => "UInt16",
        PrimitiveTypeCode.Int32 => "Int32",
        PrimitiveTypeCode.UInt32 => "UInt32",
        PrimitiveTypeCode.Int64 => "Int64",
        PrimitiveTypeCode.UInt64 => "UInt64",
        PrimitiveTypeCode.Single => "Single",
        PrimitiveTypeCode.Double => "Double",
        PrimitiveTypeCode.String => "String",
        PrimitiveTypeCode.Object => "Object",
        PrimitiveTypeCode.IntPtr => "IntPtr",
        PrimitiveTypeCode.UIntPtr => "UIntPtr",
        PrimitiveTypeCode.Void => "Void",
        PrimitiveTypeCode.TypedReference => "TypedReference",
        _ => typeCode.ToString(),
    }, []);

    public TypeName GetTypeFromReference(MetadataReader r, TypeReferenceHandle handle, byte rawTypeKind) => new(FullName(handle), [handle]);

    public TypeName GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        var type = r.GetTypeDefinition(handle);
        return new(r.GetString(type.Namespace) + "." + r.GetString(type.Name), []);
    }

    public TypeName GetTypeFromSpecification(MetadataReader r, object? context, TypeSpecificationHandle handle, byte rawTypeKind) =>
        r.GetTypeSpecification(handle).DecodeSignature(this, context);

    public TypeName GetSZArrayType(TypeName elementType) => elementType with { Name = elementType.Name + "[]" };

    public TypeName GetArrayType(TypeName elementType, ArrayShape shape) => elementType with { Name = elementType.Name + "[" + new string(',', shape.Rank - 1) + "]" };

    public TypeName GetByReferenceType(TypeName elementType) => elementType with { Name = elementType.Name + "&" };

    public TypeName GetPointerType(TypeName elementType) => elementType with { Name = elementType.Name + "*" };

    public TypeName GetPinnedType(TypeName elementType) => elementType;

    public TypeName GetModifiedType(TypeName modifier, TypeName unmodifiedType, bool isRequired) => unmodifiedType;

    public TypeName GetGenericInstantiation(TypeName genericType, ImmutableArray<TypeName> typeArguments) =>
        new(genericType.Name + "[" + string.Join(",", typeArguments.Select(a => a.Name)) + "]",
            genericType.Games.Concat(typeArguments.SelectMany(a => a.Games)).ToList());

    public TypeName GetGenericTypeParameter(object? context, int index) => new("!" + index, []);

    public TypeName GetGenericMethodParameter(object? context, int index) => new("!!" + index, []);

    public TypeName GetFunctionPointerType(MethodSignature<TypeName> signature) => new("fnptr", []);
}

/// <summary>The game's assemblies read as metadata: which types and members they define.</summary>
internal sealed class GameMetadata
{
    private readonly Dictionary<string, (MetadataReader Reader, TypeDefinition Type)> _types = new();
    private readonly List<PEReader> _keep = new();

    public GameMetadata(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            var pe = new PEReader(File.OpenRead(path));
            _keep.Add(pe);
            var reader = pe.GetMetadataReader();
            foreach (var handle in reader.TypeDefinitions)
            {
                var type = reader.GetTypeDefinition(handle);
                _types[FullName(reader, type)] = (reader, type);
            }
        }
    }

    public bool Has(GameMember member)
    {
        for (var name = member.TypeName; name != null; name = BaseOf(name))
            if (_types.TryGetValue(name, out var found) && Declares(found.Reader, found.Type, member))
                return true;
        return false;
    }

    private static bool Declares(MetadataReader reader, TypeDefinition type, GameMember member)
    {
        switch (member)
        {
            case GameMember.Type:
                return true;
            case GameMember.Field field:
                return type.GetFields().Any(h => reader.GetString(reader.GetFieldDefinition(h).Name) == field.Name);
            case GameMember.Property property:
                return type.GetProperties().Any(h => reader.GetString(reader.GetPropertyDefinition(h).Name) == property.Name);
            case GameMember.Event @event:
                return type.GetEvents().Any(h => reader.GetString(reader.GetEventDefinition(h).Name) == @event.Name);
            case GameMember.Method method:
                var names = new TypeDefNames(reader);
                return type.GetMethods().Any(h =>
                {
                    var definition = reader.GetMethodDefinition(h);
                    if (reader.GetString(definition.Name) != method.Name)
                        return false;
                    var parameters = definition.DecodeSignature(names, null).ParameterTypes;
                    return parameters.SequenceEqual(method.Parameters);
                });
            default:
                return false;
        }
    }

    private string? BaseOf(string name)
    {
        if (!_types.TryGetValue(name, out var found) || found.Type.BaseType.IsNil)
            return null;
        var reader = found.Reader;
        return found.Type.BaseType.Kind switch
        {
            HandleKind.TypeDefinition => FullName(reader, reader.GetTypeDefinition((TypeDefinitionHandle)found.Type.BaseType)),
            HandleKind.TypeReference => new TypeNames(reader).FullName((TypeReferenceHandle)found.Type.BaseType),
            _ => null,
        };
    }

    internal static string FullName(MetadataReader reader, TypeDefinition type)
    {
        var name = reader.GetString(type.Name);
        if (type.IsNested)
            return FullName(reader, reader.GetTypeDefinition(type.GetDeclaringType())) + "+" + name;
        var ns = reader.GetString(type.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }
}

/// <summary>Reflection-style names of parameter types in a definition's own assembly.</summary>
internal sealed class TypeDefNames(MetadataReader reader) : ISignatureTypeProvider<string, object?>
{
    private readonly TypeNames _references = new(reader);

    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => _references.GetPrimitiveType(typeCode).Name;

    public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle handle, byte rawTypeKind) => _references.FullName(handle);

    public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle handle, byte rawTypeKind) =>
        GameMetadata.FullName(r, r.GetTypeDefinition(handle));

    public string GetTypeFromSpecification(MetadataReader r, object? context, TypeSpecificationHandle handle, byte rawTypeKind) =>
        r.GetTypeSpecification(handle).DecodeSignature(this, context);

    public string GetSZArrayType(string elementType) => elementType + "[]";

    public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', shape.Rank - 1) + "]";

    public string GetByReferenceType(string elementType) => elementType + "&";

    public string GetPointerType(string elementType) => elementType + "*";

    public string GetPinnedType(string elementType) => elementType;

    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
        genericType + "[" + string.Join(",", typeArguments) + "]";

    public string GetGenericTypeParameter(object? context, int index) => "!" + index;

    public string GetGenericMethodParameter(object? context, int index) => "!!" + index;

    public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
}
