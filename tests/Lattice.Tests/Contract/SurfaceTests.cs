using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Lattice.Api;

namespace Lattice.Tests.Contract;

/// <summary>Rules for a shared library's public surface that reflection over the shipped assemblies can check.</summary>
public sealed class SurfaceTests
{
    private const BindingFlags Declared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly Assembly Api = typeof(Ghost).Assembly;
    private static readonly Assembly ApiUnity = typeof(LatticeDrawing).Assembly;
    private static readonly Assembly Core = typeof(Lattice.Core.Ports.ICatalogue).Assembly;
    private static readonly Assembly[] Contract = [Api, ApiUnity];

    private static readonly Type[] MutableCollections =
    [
        typeof(List<>), typeof(Dictionary<,>), typeof(HashSet<>), typeof(ICollection<>), typeof(IList<>),
        typeof(IDictionary<,>), typeof(ISet<>),
    ];

    private static readonly Type[] HiddenThreads = [typeof(Thread), typeof(Task), typeof(Task<>), typeof(ValueTask), typeof(ValueTask<>)];

    [Fact]
    public void A1_core_and_game_expose_no_public_types()
    {
        Assert.Empty(Core.GetExportedTypes());
        Assert.Empty(PublicTypeNames(ShippedPath("Lattice.Game")));
    }

    [Fact]
    public void A2_concrete_types_are_sealed_and_union_bases_have_no_accessible_constructor()
    {
        var offenders = new List<string>();
        foreach (var type in ApiClasses())
        {
            if (!type.IsAbstract && !type.IsSealed)
                offenders.Add($"{type} is not sealed");
            if (type.IsAbstract)
                foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    if ((ctor.IsPublic || ctor.IsFamily || ctor.IsFamilyOrAssembly) && !IsCopyConstructor(ctor))
                        offenders.Add($"{type} has an accessible constructor");
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void A3_D3_no_public_setters_fields_mutable_collections_arrays_or_threads_in_the_surface()
    {
        var offenders = new List<string>();
        foreach (var type in ContractTypes().Where(t => !t.IsEnum))
        {
            foreach (var property in type.GetProperties(Declared).Where(p => p.SetMethod is { IsPublic: true }))
                if (!IsInitOnly(property.SetMethod!))
                    offenders.Add($"{type}.{property.Name} has a public setter");
            foreach (var field in type.GetFields(Declared).Where(f => f.IsPublic && !f.IsLiteral && !(f.IsStatic && f.IsInitOnly)))
                offenders.Add($"{type}.{field.Name} is a public field");
            foreach (var used in SignatureTypes(type))
                if (IsForbidden(used))
                    offenders.Add($"{type} uses {used} in a public signature");
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void A4_record_collection_members_use_the_value_collections()
    {
        var offenders = new List<string>();
        foreach (var type in ApiClasses().Where(IsRecord))
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                if (IsCollection(property.PropertyType) && !IsValueCollection(property.PropertyType))
                    offenders.Add($"{type}.{property.Name}: {property.PropertyType}");

        Assert.Empty(offenders);
    }

    [Fact]
    public void A5_validated_value_objects_have_no_public_constructor()
    {
        var validated = ContractTypes()
            .Where(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Any(m => m.Name == "Create" && m.ReturnType.IsGenericType && m.ReturnType.GetGenericTypeDefinition() == typeof(Result<,>)))
            .ToList();

        Assert.NotEmpty(validated);
        Assert.DoesNotContain(validated, t => t.GetConstructors(BindingFlags.Public | BindingFlags.Instance).Length > 0);
    }

    [Fact]
    public void D4_the_only_mutable_statics_are_the_current_system_and_drawing_and_their_started_events()
    {
        var allowed = new HashSet<string>
        {
            "Lattice.Api.LatticeSystem._current", "Lattice.Api.LatticeSystem.Started",
            "Lattice.Api.LatticeDrawing._current", "Lattice.Api.LatticeDrawing.Started",
        };
        var offenders = new List<string>();
        foreach (var assembly in new[] { Api, ApiUnity, Core })
            foreach (var type in assembly.GetTypes().Where(t => !t.IsDefined(typeof(CompilerGeneratedAttribute), false)))
                foreach (var field in type.GetFields(Declared).Where(f => f.IsStatic && !f.IsLiteral))
                {
                    var name = $"{type.FullName}.{field.Name}";
                    if (!field.IsInitOnly && !allowed.Contains(name))
                        offenders.Add($"{name} is a mutable static");
                    if (field.IsInitOnly && IsForbidden(field.FieldType))
                        offenders.Add($"{name} holds a mutable collection");
                }

        Assert.Empty(offenders);
    }

    [Fact]
    public void E2_shipped_assemblies_reference_only_the_framework_lattice_and_the_game()
    {
        Assert.Equal(["netstandard"], ReferenceNames(ShippedPath("Lattice.Api")));
        Assert.Equal(["Lattice.Api", "netstandard"], ReferenceNames(ShippedPath("Lattice.Core")));
        Assert.Equal(["Lattice.Api", "netstandard", "Unity.TextMeshPro", "UnityEngine.CoreModule"], ReferenceNames(ShippedPath("Lattice.Api.Unity")));
        Assert.Empty(ReferenceNames(ShippedPath("Lattice.Render")).Except(
            ["netstandard", "Lattice.Api", "Lattice.Api.Unity", "Lattice.Core", "UnityEngine.CoreModule", "UnityEngine.AssetBundleModule",
             "Unity.TextMeshPro", "UnityEngine.UI", "UnityEngine.TextRenderingModule"]));
        Assert.Empty(ReferenceNames(ShippedPath("Lattice.Game")).Except(
            ["netstandard", "Lattice.Api", "Lattice.Api.Unity", "Lattice.Core", "Lattice.Render", "Assembly-CSharp", "UnityEngine", "UnityEngine.CoreModule",
             "Unity.TextMeshPro", "UnityEngine.UI", "BepInEx", "0Harmony", "StationeersLaunchPad", "LaunchPadBooster", "mscorlib"]));
    }

    [Fact]
    public void B2_assembly_version_is_the_major_and_matches_the_contract_constants()
    {
        var name = Api.GetName();
        var fileVersion = Api.GetCustomAttribute<AssemblyFileVersionAttribute>()!.Version;

        Assert.Equal(new Version(ApiVersion.ContractMajor, 0, 0, 0), name.Version);
        Assert.StartsWith($"{ApiVersion.ContractMajor}.{ApiVersion.ContractMinor}.", fileVersion);
        Assert.Equal(new ApiVersion(ApiVersion.ContractMajor, ApiVersion.ContractMinor), ApiVersion.Current);
    }

    [Fact]
    public void The_contract_references_no_unity_or_game_assembly()
    {
        var names = Api.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.DoesNotContain(names, n => n.StartsWith("Unity", StringComparison.Ordinal) || n.StartsWith("Assembly-CSharp", StringComparison.Ordinal)
            || n is "BepInEx" or "StationeersLaunchPad" or "LaunchPadBooster" or "0Harmony");
    }

    private static IEnumerable<Type> ContractTypes() => Contract.SelectMany(a => a.GetExportedTypes());

    private static IEnumerable<Type> ApiClasses() =>
        ContractTypes().Where(t => t.IsClass && !(t.IsAbstract && t.IsSealed) && !typeof(Delegate).IsAssignableFrom(t));

    private static bool IsRecord(Type type) => type.GetMethod("<Clone>$") is not null || type.GetProperty("EqualityContract", BindingFlags.NonPublic | BindingFlags.Instance) is not null;

    private static bool IsCopyConstructor(ConstructorInfo ctor)
    {
        var parameters = ctor.GetParameters();
        return parameters.Length == 1 && parameters[0].ParameterType == ctor.DeclaringType;
    }

    private static bool IsInitOnly(MethodInfo setter) =>
        setter.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.Name == "IsExternalInit");

    private static IEnumerable<Type> SignatureTypes(Type type)
    {
        foreach (var method in type.GetMethods(Declared).Where(m => m.IsPublic || m.IsFamily))
        {
            yield return method.ReturnType;
            foreach (var parameter in method.GetParameters())
                yield return parameter.ParameterType;
        }

        foreach (var property in type.GetProperties(Declared))
            yield return property.PropertyType;
        foreach (var field in type.GetFields(Declared).Where(f => f.IsPublic))
            yield return field.FieldType;
        foreach (var e in type.GetEvents(Declared))
            yield return e.EventHandlerType!;
    }

    private static bool IsForbidden(Type type)
    {
        if (type.IsByRef)
            return IsForbidden(type.GetElementType()!);
        if (type.IsArray)
            return true;
        if (Array.IndexOf(HiddenThreads, type) >= 0)
            return true;
        if (!type.IsGenericType)
            return false;
        var definition = type.GetGenericTypeDefinition();
        return Array.IndexOf(MutableCollections, definition) >= 0
               || Array.IndexOf(HiddenThreads, definition) >= 0
               || type.GetGenericArguments().Any(IsForbidden);
    }

    private static bool IsCollection(Type type) =>
        type != typeof(string) && type.GetInterfaces().Append(type).Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));

    private static bool IsValueCollection(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() is var d
        && (d == typeof(ValueList<>) || d == typeof(ValueSet<>) || d == typeof(ValueMap<,>));

    private static string ShippedPath(string project)
    {
        var configuration = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)).Parent!.Name;
        return Path.Combine(RepoRoot(), "src", project, "bin", configuration, "netstandard2.1", project + ".dll");
    }

    private static List<string> ReferenceNames(string path)
    {
        using var pe = new PEReader(File.OpenRead(path));
        var reader = pe.GetMetadataReader();
        return reader.AssemblyReferences.Select(h => reader.GetString(reader.GetAssemblyReference(h).Name)).Order().ToList();
    }

    private static List<string> PublicTypeNames(string path)
    {
        using var pe = new PEReader(File.OpenRead(path));
        var reader = pe.GetMetadataReader();
        return reader.TypeDefinitions
            .Select(reader.GetTypeDefinition)
            .Where(t => (t.Attributes & System.Reflection.TypeAttributes.VisibilityMask) == System.Reflection.TypeAttributes.Public)
            .Select(t => reader.GetString(t.Name))
            .ToList();
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Lattice.sln")))
                return dir.FullName;
        throw new InvalidOperationException("Lattice.sln not found above the test output folder.");
    }
}
