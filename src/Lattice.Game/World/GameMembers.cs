using System;
using System.Collections.Generic;
using System.Reflection;
using Lattice.Api;
using Lattice.Core.Modules;

namespace Lattice.Game.World;

/// <summary>
/// Finds the game members a module's manifest names in the running game, so a module whose members are missing is
/// switched off before it touches any of them (architecture "Game updates"). A method matches only with exactly the
/// parameter types the manifest names; <c>.ctor</c> names a constructor. Types are looked up in every
/// loaded assembly. Runs once per load; linear in the manifest.
/// </summary>
internal static class GameMembers
{
    private const BindingFlags Every = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    /// <summary>The members of the manifest the running game does not have; empty when all are there.</summary>
    public static ValueList<GameMember> Missing(ModuleManifest manifest)
    {
        var types = new Dictionary<string, Type?>();
        var missing = new List<GameMember>();
        foreach (var member in manifest.Uses)
            if (Find(member, types) == null)
                missing.Add(member);
        return missing.Count == 0 ? ValueList<GameMember>.Empty : ValueList.From(missing);
    }

    /// <summary>The method or constructor a manifest entry names, or null when the game has none.</summary>
    public static MethodBase? Method(GameMember.Method method) => Find(method, new Dictionary<string, Type?>()) as MethodBase;

    /// <summary>The property a manifest entry names, or null when the game has none.</summary>
    public static PropertyInfo? Property(GameMember.Property property) => Find(property, new Dictionary<string, Type?>()) as PropertyInfo;

    private static object? Find(GameMember member, Dictionary<string, Type?> types)
    {
        var type = TypeNamed(member.TypeName, types);
        if (type == null)
            return null;
        return member switch
        {
            GameMember.Type => type,
            GameMember.Field field => DeclaredOrInherited(type, t => t.GetField(field.Name, Every)),
            GameMember.Property property => DeclaredOrInherited(type, t => t.GetProperty(property.Name, Every)),
            GameMember.Event @event => DeclaredOrInherited(type, t => t.GetEvent(@event.Name, Every)),
            GameMember.Method method => FindMethod(type, method, types),
            _ => null,
        };
    }

    private static MethodBase? FindMethod(Type type, GameMember.Method method, Dictionary<string, Type?> types)
    {
        var parameters = new Type[method.Parameters.Count];
        var i = 0;
        foreach (var name in method.Parameters)
        {
            var parameter = TypeNamed(name, types);
            if (parameter == null)
                return null;
            parameters[i++] = parameter;
        }

        if (method.Name == ".ctor")
            return type.GetConstructor(Every, null, parameters, null);
        return DeclaredOrInherited(type, t => Matching(t, method.Name, parameters));
    }

    private static MethodInfo? Matching(Type type, string name, Type[] parameters)
    {
        foreach (var candidate in type.GetMethods(Every))
        {
            if (candidate.Name != name)
                continue;
            var actual = candidate.GetParameters();
            if (actual.Length != parameters.Length)
                continue;
            var same = true;
            for (var i = 0; i < actual.Length && same; i++)
                same = actual[i].ParameterType == parameters[i];
            if (same)
                return candidate;
        }

        return null;
    }

    private static T? DeclaredOrInherited<T>(Type type, Func<Type, T?> find) where T : class
    {
        for (var at = type; at != null; at = at.BaseType)
            if (find(at) is { } found)
                return found;
        return null;
    }

    private static Type? TypeNamed(string fullName, Dictionary<string, Type?> types)
    {
        if (types.TryGetValue(fullName, out var known))
            return known;
        var found = Type.GetType(fullName);
        if (found == null)
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                if (assembly.GetType(fullName) is { } type)
                {
                    found = type;
                    break;
                }

        types[fullName] = found;
        return found;
    }
}
