using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace Lattice.Core.World;

/// <summary>What a walk over types found: the methods the inspector returned, and the types it could not load.</summary>
internal sealed record TypeWalkResult(IReadOnlyList<MethodBase> Methods, IReadOnlyList<Type> Skipped)
{
    private const int NamedInLog = 5;

    /// <summary>One log line for the skipped types, or null when none were skipped.</summary>
    public string? SkippedLine() =>
        Skipped.Count == 0 ? null : $"Lattice: skipped {Skipped.Count} types that don't load: {Named()}.";

    private string Named()
    {
        var names = new string[Math.Min(Skipped.Count, NamedInLog)];
        for (var i = 0; i < names.Length; i++)
            names[i] = $"{Skipped[i].FullName} ({Skipped[i].Assembly.GetName().Name}.dll)";
        return string.Join(", ", names) + (Skipped.Count > NamedInLog ? ", ..." : "");
    }
}

/// <summary>
/// Walks types of other mods' assemblies with a reflection inspector. A type whose own members can't be resolved
/// (a field of a type from a missing assembly) throws from the reflection call; the walk skips that type and goes on.
/// Only the exceptions type loading throws are caught; anything else the inspector throws surfaces.
/// </summary>
internal static class TypeWalk
{
    /// <summary>Applies <paramref name="inspect"/> to every non-null type, skipping the ones that don't load. Each method appears once, in the order found.</summary>
    public static TypeWalkResult Run(IEnumerable<Type?> types, Func<Type, IEnumerable<MethodBase>> inspect)
    {
        var methods = new List<MethodBase>();
        var seen = new HashSet<MethodBase>();
        var skipped = new List<Type>();
        foreach (var type in types)
        {
            if (type == null)
                continue;
            var found = new List<MethodBase>();
            try
            {
                foreach (var method in inspect(type))
                    found.Add(method);
            }
            catch (Exception e) when (DoesNotLoad(e))
            {
                skipped.Add(type);
                continue;
            }

            foreach (var method in found)
                if (seen.Add(method))
                    methods.Add(method);
        }

        return new TypeWalkResult(methods, skipped);
    }

    /// <summary>The exceptions the runtime throws while loading a type or resolving its members.</summary>
    public static bool DoesNotLoad(Exception e) =>
        e is TypeLoadException or FileNotFoundException or FileLoadException or BadImageFormatException or NotSupportedException;
}
