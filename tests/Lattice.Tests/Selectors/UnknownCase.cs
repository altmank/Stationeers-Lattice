using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace Lattice.Tests.Selectors;

/// <summary>
/// A case of a closed union that this version of Lattice does not know, as a later contract minor would add: an
/// instance of a subclass made at run time, built without running any constructor (the union's own is private).
/// </summary>
internal static class UnknownCase
{
    private static readonly ModuleBuilder Module = AssemblyBuilder
        .DefineDynamicAssembly(new AssemblyName("Lattice.Tests.UnknownCases"), AssemblyBuilderAccess.Run)
        .DefineDynamicModule("UnknownCases");

    private static readonly Dictionary<Type, Type> Made = new();

    public static TUnion Of<TUnion>() where TUnion : class
    {
        lock (Made)
        {
            if (!Made.TryGetValue(typeof(TUnion), out var made))
                Made[typeof(TUnion)] = made = Define(typeof(TUnion));
            return (TUnion)RuntimeHelpers.GetUninitializedObject(made);
        }
    }

    private static Type Define(Type union)
    {
        var type = Module.DefineType("Later" + union.Name, TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class, union);
        foreach (var method in union.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (!method.IsAbstract)
                continue;
            var parameters = method.GetParameters();
            var types = new Type[parameters.Length];
            for (var i = 0; i < types.Length; i++)
                types[i] = parameters[i].ParameterType;
            var body = type.DefineMethod(method.Name, (method.Attributes & ~MethodAttributes.Abstract & ~MethodAttributes.NewSlot) | MethodAttributes.Final,
                method.ReturnType, types);
            var il = body.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ret);
            type.DefineMethodOverride(body, method);
        }

        return type.CreateType();
    }
}
