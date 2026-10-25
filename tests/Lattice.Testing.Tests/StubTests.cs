using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Lattice.Api;
using Lattice.Testing.Stubs;

namespace Lattice.Testing.Tests;

/// <summary>
/// The stubs in <c>Lattice.Testing.Stubs</c>: one per contract interface, every member a fake can override, each
/// failing loudly until it is overridden. A minor that adds an interface, or a member a fake could not override, fails
/// here; a member the stub lacks fails Lattice's own build.
/// </summary>
public sealed class StubTests
{
    private static readonly Assembly Api = typeof(IGhostSystem).Assembly;
    private static readonly Assembly Testing = typeof(StubPlans).Assembly;

    private static IEnumerable<Type> Contracts => Api.GetExportedTypes().Where(static t => t.IsInterface);

    private static Type StubOf(Type contract) =>
        Testing.GetExportedTypes().Single(t => t.Namespace == "Lattice.Testing.Stubs" && t.IsClass && contract.IsAssignableFrom(t));

    public static TheoryData<string> ContractNames()
    {
        var data = new TheoryData<string>();
        foreach (var contract in Api.GetExportedTypes().Where(static t => t.IsInterface))
            data.Add(contract.Name);
        return data;
    }

    private static Type Contract(string name) => Api.GetExportedTypes().Single(t => t.IsInterface && t.Name == name);

    [Fact]
    public void Every_contract_interface_has_a_stub()
    {
        Assert.Equal(25, Contracts.Count());
        foreach (var contract in Contracts)
        {
            var stubs = Testing.GetExportedTypes().Where(t => t.IsClass && contract.IsAssignableFrom(t) && t.Namespace == "Lattice.Testing.Stubs").ToList();
            Assert.True(stubs.Count == 1, $"{contract.Name}: {stubs.Count} stubs");
            Assert.Equal("Stub" + contract.Name[1..], stubs[0].Name);
        }
    }

    [Theory]
    [MemberData(nameof(ContractNames))]
    public void Every_contract_interface_has_a_stub_whose_members_all_override(string name)
    {
        var contract = Contract(name);
        var stub = StubOf(contract);

        Assert.True(stub.IsPublic && !stub.IsSealed && !stub.IsAbstract, $"{stub.Name} is not a public, unsealed, concrete class");
        Assert.NotNull(stub.GetConstructor(Type.EmptyTypes));
        foreach (var implemented in new[] { contract }.Concat(contract.GetInterfaces()))
        {
            var map = stub.GetInterfaceMap(implemented);
            foreach (var target in map.TargetMethods)
            {
                Assert.True(target.IsPublic, $"{stub.Name}.{target.Name} is not public");
                Assert.Equal(stub, target.DeclaringType);
                Assert.True(target.IsVirtual && !target.IsFinal, $"{stub.Name}.{target.Name} cannot be overridden");
            }
        }

        foreach (var e in contract.GetEvents())
            Assert.NotNull(stub.GetMethod("Raise" + e.Name, BindingFlags.Public | BindingFlags.Instance));
    }

    [Theory]
    [MemberData(nameof(ContractNames))]
    public void Every_member_a_test_has_not_set_up_throws_naming_itself(string name)
    {
        var contract = Contract(name);
        var stub = StubOf(contract);
        var instance = Activator.CreateInstance(stub)!;
        var accessors = new HashSet<MethodInfo>(contract.GetEvents().SelectMany(static e => new[] { e.AddMethod!, e.RemoveMethod! }));

        foreach (var method in contract.GetMethods().Where(m => !accessors.Contains(m)))
        {
            var callable = method.IsGenericMethodDefinition ? Closed(method) : method;
            var arguments = callable.GetParameters().Select(static p => Default(p.ParameterType)).ToArray();

            var thrown = Assert.Throws<TargetInvocationException>(() => callable.Invoke(instance, arguments));

            var failure = Assert.IsType<NotSupportedException>(thrown.InnerException);
            var member = method.IsSpecialName ? method.Name[4..] : method.Name;
            Assert.Equal($"{contract.Name}.{member} is not set up in {stub.Name}: override it", failure.Message);
        }
    }

    [Theory]
    [MemberData(nameof(ContractNames))]
    public void Events_take_subscribers_and_raise_reaches_them_and_dispose_does_nothing(string name)
    {
        var contract = Contract(name);
        var stub = StubOf(contract);
        var instance = Activator.CreateInstance(stub)!;

        foreach (var e in contract.GetEvents())
        {
            var heard = 0;
            var handler = Handler(e.EventHandlerType!, () => heard++);
            e.AddMethod!.Invoke(instance, [handler]);
            var raise = stub.GetMethod("Raise" + e.Name)!;
            raise.Invoke(instance, raise.GetParameters().Select(static p => Default(p.ParameterType)).ToArray());
            e.RemoveMethod!.Invoke(instance, [handler]);
            raise.Invoke(instance, raise.GetParameters().Select(static p => Default(p.ParameterType)).ToArray());

            Assert.Equal(1, heard);
        }

        if (instance is IDisposable disposable)
            disposable.Dispose();
    }

    [Theory]
    [MemberData(nameof(ContractNames))]
    public void Every_contract_interface_says_to_fake_it_through_its_stub(string name)
    {
        var docs = System.Xml.Linq.XDocument.Load(System.IO.Path.ChangeExtension(Api.Location, ".xml"));
        var summary = docs.Descendants("member").Single(m => (string?)m.Attribute("name") == "T:Lattice.Api." + name).Value;
        var flat = string.Join(" ", summary.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        Assert.Contains($"to fake it in a test, inherit Lattice.Testing.Stubs.Stub{name[1..]}, which gains every added member in the same release.", flat);
    }

    [Fact]
    public void A_fake_that_overrides_one_member_answers_it_and_still_fails_loudly_elsewhere()
    {
        var plans = new OnePlan();

        Assert.Equal(new PlanId(4), plans.Default);
        Assert.Throws<NotSupportedException>(() => plans.All);
    }

    private sealed class OnePlan : StubPlans
    {
        public override PlanId Default => new(4);
    }

    private static MethodInfo Closed(MethodInfo method)
    {
        var arguments = method.GetGenericArguments().Select(static a =>
        {
            foreach (var constraint in a.GetGenericParameterConstraints())
                if (constraint.IsClass && !constraint.IsAbstract)
                    return constraint;
                else if (constraint.IsClass && constraint.IsAbstract)
                    return Api.GetExportedTypes().First(t => t.IsSubclassOf(constraint) && !t.IsAbstract);
            return typeof(object);
        }).ToArray();
        return method.MakeGenericMethod(arguments);
    }

    private static object? Default(Type type)
    {
        if (type.IsByRef)
            type = type.GetElementType()!;
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }

    private static Delegate Handler(Type handlerType, Action heard)
    {
        var invoke = handlerType.GetMethod("Invoke")!;
        var parameters = invoke.GetParameters().Select(static p => System.Linq.Expressions.Expression.Parameter(p.ParameterType)).ToArray();
        var body = System.Linq.Expressions.Expression.Invoke(System.Linq.Expressions.Expression.Constant(heard));
        return System.Linq.Expressions.Expression.Lambda(handlerType, body, parameters).Compile();
    }
}
