using System;
using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Core.EntryPoint;

/// <summary>An assembly loaded in the process: its simple name, version and file.</summary>
internal sealed record LoadedAssembly(string Name, string Version, string Location);

/// <summary>
/// The once-per-process log of Lattice's own loaded assemblies: LaunchPad loads every dll of every mod
/// folder, so a consumer that ships its own Lattice.Api or Lattice.Api.Unity puts a second copy in the process, with its own
/// <see cref="LatticeSystem.Current"/> that is never set. Each copy is named with its file; a second copy is called out.
/// Lattice runs on its own copy and acts on nothing else. Linear in the loaded assemblies.
/// </summary>
internal static class AssemblyCopies
{
    /// <summary>The names of Lattice's assemblies, which never change.</summary>
    public static ValueList<string> Names => ValueList.From(new[] { "Lattice.Api", "Lattice.Api.Unity", "Lattice.Core", "Lattice.Game" });

    /// <summary>One line per loaded copy of a Lattice assembly; <paramref name="own"/> maps each name to the file Lattice runs from.</summary>
    public static ValueList<string> Report(ValueList<LoadedAssembly> loaded, ValueMap<string, string> own)
    {
        var lines = new List<string>(Names.Count);
        foreach (var name in Names)
            foreach (var assembly in loaded)
            {
                if (!string.Equals(assembly.Name, name, StringComparison.Ordinal))
                    continue;
                var mine = own.TryGetValue(name, out var location) && SameFile(location, assembly.Location);
                lines.Add(mine
                    ? $"Lattice: {name} {assembly.Version} from {assembly.Location}"
                    : $"Lattice: a second copy of {name} {assembly.Version} is loaded from {assembly.Location}; Lattice runs on its own copy. The mod that ships it should reference {name} with Private=\"false\" and depend on Lattice instead.");
            }

        return ValueList.From(lines);
    }

    private static bool SameFile(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
