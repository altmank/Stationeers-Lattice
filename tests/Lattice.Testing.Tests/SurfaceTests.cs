using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Lattice.Api;
using static Lattice.Testing.Tests.Hosted;

namespace Lattice.Testing.Tests;

/// <summary>
/// Lattice.Testing is a contract of its own: its public types follow the surface
/// rules, no Core or test kit type shows in its signatures, and it ships only in its own artifacts folder.
/// </summary>
public sealed class SurfaceTests
{
    private static readonly Assembly Testing = typeof(TestWorld).Assembly;

    private static IEnumerable<Type> Public => Testing.GetExportedTypes();

    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    /// <summary>The stubs a consumer's fakes inherit: the one namespace whose classes are unsealed by design.</summary>
    private const string Stubs = "Lattice.Testing.Stubs";

    [Fact]
    public void Public_classes_are_sealed_and_abstract_types_are_static_but_the_stubs()
    {
        foreach (var type in Public.Where(static t => t.IsClass && t.Namespace != Stubs))
            Assert.True(type.IsSealed, $"{type} is not sealed");
    }

    [Fact]
    public void Unsealed_public_classes_live_only_with_the_stubs()
    {
        foreach (var type in Public.Where(static t => t.IsClass && !t.IsSealed))
            Assert.Equal(Stubs, type.Namespace);
        Assert.Equal(25, Public.Count(static t => t.Namespace == Stubs));
    }

    [Fact]
    public void No_public_setter_mutable_collection_or_instance_field()
    {
        foreach (var type in Public)
        {
            foreach (var property in type.GetProperties(Declared))
            {
                Assert.True(property.SetMethod is not { IsPublic: true } || IsInit(property), $"{type}.{property.Name} has a public setter");
                Assert.False(Mutable(property.PropertyType), $"{type}.{property.Name} is a mutable collection");
            }

            foreach (var field in type.GetFields(Declared).Where(static f => !f.IsLiteral && !InEnum(f)))
                Assert.True(field.IsStatic && field.IsInitOnly, $"{type}.{field.Name} is a public field");
            foreach (var method in type.GetMethods(Declared))
            {
                Assert.False(Mutable(method.ReturnType), $"{type}.{method.Name} returns a mutable collection");
                foreach (var parameter in method.GetParameters())
                    Assert.False(Mutable(parameter.ParameterType), $"{type}.{method.Name}({parameter.Name}) takes a mutable collection");
            }
        }

        static bool InEnum(FieldInfo f) => f.DeclaringType!.IsEnum;
    }

    [Fact]
    public void Validated_values_have_no_public_constructor()
    {
        foreach (var type in new[] { typeof(TestPiece), typeof(TestNetwork), typeof(TestScenario), typeof(TestCatalogue), typeof(TestWorld), typeof(AllocationException) })
            Assert.DoesNotContain(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), static c => c.GetParameters().Length > 0 && !IsCopy(c));
    }

    [Fact]
    public void Equal_values_are_equal_with_equal_hash_codes()
    {
        var one = TestScenario.Empty.WithPiece(Conduit).WithStructure(Conduit.Key, At(0), default);
        var two = TestScenario.Empty.WithPiece(Conduit).WithStructure(Conduit.Key, At(0), default);
        Assert.Equal(one, two);
        Assert.Equal(one.GetHashCode(), two.GetHashCode());
        Assert.NotEqual(one, two.As(new Actor.Player(new ClientId(2))));
        Assert.Equal(Conduit, Must(TestPiece.Create("ModConduitLine", "ItemModConduit", PieceGrid.Small))
            .WithEnd(new CellOffset(0, 0, 0), SignedAxis.YPlus, ValueSet.From(new[] { TestNetwork.Data, TestNetwork.Power }))
            .WithEnd(new CellOffset(0, 0, 0), SignedAxis.YMinus, ValueSet.From(new[] { TestNetwork.Power, TestNetwork.Data }))
            .WithCell(new CellOffset(0, 0, 0), TestSlot.Cable)
            .WithCollision(2));
    }

    [Fact]
    public void No_Core_or_test_kit_type_shows_in_a_public_signature()
    {
        foreach (var type in Public)
        {
            Assert.True(Allowed(type.BaseType), $"{type} derives from {type.BaseType}");
            foreach (var face in type.GetInterfaces())
                Assert.True(Allowed(face), $"{type} implements {face}");
            foreach (var member in type.GetMembers(Declared))
                foreach (var used in Used(member))
                    Assert.True(Allowed(used), $"{type}.{member.Name} uses {used}");
        }
    }

    [Fact]
    public void It_references_only_the_framework_Api_Core_and_the_test_kit()
    {
        var names = Testing.GetReferencedAssemblies().Select(static a => a.Name).ToHashSet();

        Assert.Subset(new HashSet<string?> { "netstandard", "Lattice.Api", "Lattice.Core", "Lattice.TestKit" }, names);
    }

    [Fact]
    public void Its_version_is_the_Lattice_builds_with_a_major_only_assembly_version()
    {
        var api = typeof(IGhostSystem).Assembly.GetName().Version!;
        var testing = Testing.GetName().Version!;

        Assert.Equal(new Version(api.Major, 0, 0, 0), testing);
        Assert.Equal(
            typeof(IGhostSystem).Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()!.Version,
            Testing.GetCustomAttribute<AssemblyFileVersionAttribute>()!.Version);
    }

    [Fact]
    public void The_artifacts_folder_holds_the_four_assemblies_and_the_mod_folder_none_of_them()
    {
        var artifacts = Artifacts();
        var testing = Directory.GetFiles(Path.Combine(artifacts, "Lattice.Testing")).Select(Path.GetFileName).OrderBy(static n => n, StringComparer.Ordinal).ToArray();
        var mod = Directory.GetFiles(Path.Combine(artifacts, "Lattice"), "*", SearchOption.AllDirectories).Select(Path.GetFileName).ToArray();

        Assert.Equal(new[] { "Lattice.Api.dll", "Lattice.Api.xml", "Lattice.Core.dll", "Lattice.TestKit.dll", "Lattice.Testing.dll", "Lattice.Testing.xml" }, testing);
        Assert.DoesNotContain(mod, static n => n!.StartsWith("Lattice.Testing", StringComparison.Ordinal) || n.StartsWith("Lattice.TestKit", StringComparison.Ordinal));
    }

    private static bool IsInit(PropertyInfo property) =>
        property.SetMethod!.ReturnParameter.GetRequiredCustomModifiers().Any(static m => m.Name == "IsExternalInit");

    private static bool IsCopy(ConstructorInfo constructor) =>
        constructor.GetParameters() is [var only] && only.ParameterType == constructor.DeclaringType;

    private static bool Mutable(Type type) =>
        type.IsArray || type.IsGenericType && type.GetGenericTypeDefinition() is var g &&
        (g == typeof(List<>) || g == typeof(Dictionary<,>) || g == typeof(HashSet<>));

    private static IEnumerable<Type> Used(MemberInfo member) => member switch
    {
        PropertyInfo p => new[] { p.PropertyType },
        FieldInfo f => new[] { f.FieldType },
        EventInfo e => new[] { e.EventHandlerType! },
        MethodInfo m => m.GetParameters().Select(static p => p.ParameterType).Append(m.ReturnType),
        ConstructorInfo c => c.GetParameters().Select(static p => p.ParameterType),
        _ => Array.Empty<Type>(),
    };

    private static bool Allowed(Type? type)
    {
        if (type is null)
            return true;
        if (type.HasElementType)
            return Allowed(type.GetElementType());
        if (type.IsGenericType && !type.GetGenericArguments().All(Allowed))
            return false;
        var name = type.Assembly.GetName().Name;
        return name is "Lattice.Api" or "Lattice.Testing" or "netstandard" or "System.Runtime" or "System.Private.CoreLib";
    }

    private static string Artifacts()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Lattice.sln")))
                return Path.Combine(dir.FullName, "artifacts", AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}") ? "Debug" : "Release");
        throw new InvalidOperationException("no Lattice.sln above the test output");
    }
}

public sealed class WorkerThreadTests
{
    [Fact]
    public void With_worker_threads_the_answers_come_from_Lattices_own_threads()
    {
        using var world = Start(WithConduit.WithWorkerThreads());
        var placed = Assert.IsType<Outcome.Applied>(Submit(world, new Command.Place(ValueList.Of(Draft(Conduit.Key, At(0)))))).Changes.Added;

        var report = Ready(world, () => Must(world.System.Validation.Check(new Selector.Everything())));

        Assert.Equal(Assert.Single(placed).Id, Assert.IsType<Finding.Passes>(Assert.Single(report.Findings)).Ghost);
    }
}
