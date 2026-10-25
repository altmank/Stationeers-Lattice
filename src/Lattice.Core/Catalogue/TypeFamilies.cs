using Lattice.Api;

namespace Lattice.Core.Catalogue;

/// <summary>
/// A piece's family from its class chain, the prefab's own class first, so a modded subclass inherits its base's
/// family and an unknown class falls to the nearest game base it extends. A chain with none of these bases is
/// <see cref="PieceFamilies.Other"/>.
/// </summary>
internal static class TypeFamilies
{
    /// <summary>The first family a type along the chain names.</summary>
    public static PieceFamily Of(ValueList<TypeData> types)
    {
        foreach (var type in types)
            if (Named(type.FullName) is { } family)
                return family;
        return PieceFamilies.Other;
    }

    // Decompile: Assets.Scripts.Objects.Electrical/Cable.cs, Assets.Scripts.Objects.Pipes/Pipe.cs (Piping and the
    // liquid pipes extend it), Assets.Scripts.Objects.Pipes/Chute.cs, Objects.Structures/Frame.cs,
    // Assets.Scripts.Objects/Wall.cs (WallTransparent and the windows extend it), Assets.Scripts.Objects.Pipes/Device.cs.
    private static PieceFamily? Named(string fullName) => fullName switch
    {
        "Assets.Scripts.Objects.Electrical.Cable" => PieceFamilies.Cable,
        "Assets.Scripts.Objects.Pipes.Pipe" => PieceFamilies.Pipe,
        "Assets.Scripts.Objects.Pipes.Chute" => PieceFamilies.Chute,
        "Objects.Structures.Frame" => PieceFamilies.Frame,
        "Assets.Scripts.Objects.Wall" => PieceFamilies.Wall,
        "Assets.Scripts.Objects.Pipes.Device" => PieceFamilies.Device,
        _ => null,
    };
}
