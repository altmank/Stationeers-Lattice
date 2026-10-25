using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Core.Catalogue;

/// <summary>
/// A piece's narrowest family from its class chain, the prefab's own class first, so a modded subclass inherits its
/// base's family and an unknown class falls to the nearest game base it extends. A chain with none of these bases is
/// <see cref="PieceFamilies.Other"/>. A see-through face is a window when it seals air once built, read from the
/// piece's own last build state, so a modded subclass is judged by its own states.
/// </summary>
internal static class TypeFamilies
{
    private const string SeeThrough = "Assets.Scripts.Objects.WallTransparent";

    /// <summary>
    /// The first family a type along the chain names. A <c>WallTransparent</c> chain is <see cref="PieceFamilies.Window"/>
    /// when its last build state blocks air or it has no states, else <see cref="PieceFamilies.Wall"/> (a floor grating
    /// lets air through). Decompile: Assets.Scripts.Objects/WallTransparent.cs, Assets.Scripts.Objects/Structure.cs
    /// (<c>BuildState.BlockAir</c>, read by <c>Structure.CanAirPass</c>).
    /// </summary>
    public static PieceFamily Of(ValueList<TypeData> types, IReadOnlyList<StateData> states)
    {
        foreach (var type in types)
        {
            if (type.FullName == SeeThrough)
                return states.Count == 0 || states[states.Count - 1].BlocksAir ? PieceFamilies.Window : PieceFamilies.Wall;
            if (Named(type.FullName) is { } family)
                return family;
        }

        return PieceFamilies.Other;
    }

    // Decompile: Assets.Scripts.Objects.Electrical/Cable.cs, Assets.Scripts.Objects.Pipes/Pipe.cs (Piping and the
    // liquid pipes extend it), Assets.Scripts.Objects.Pipes/Chute.cs, Objects.Structures/Frame.cs,
    // Assets.Scripts.Objects/Wall.cs (WallTransparent, Floor and the shutter connector extend it),
    // Assets.Scripts.Objects.Pipes/Device.cs.
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

/// <summary>
/// The tree Lattice's families form: <see cref="PieceFamilies.Window"/> under <see cref="PieceFamilies.Wall"/>, every
/// other family (Lattice's or a mod's) with no parent. The chains of Lattice's families are made once; reading them
/// allocates nothing.
/// </summary>
internal static class FamilyTree
{
    private static readonly ValueList<PieceFamily> CableChain = ValueList.Of(PieceFamilies.Cable);
    private static readonly ValueList<PieceFamily> PipeChain = ValueList.Of(PieceFamilies.Pipe);
    private static readonly ValueList<PieceFamily> ChuteChain = ValueList.Of(PieceFamilies.Chute);
    private static readonly ValueList<PieceFamily> FrameChain = ValueList.Of(PieceFamilies.Frame);
    private static readonly ValueList<PieceFamily> WallChain = ValueList.Of(PieceFamilies.Wall);
    private static readonly ValueList<PieceFamily> WindowChain = ValueList.From(new[] { PieceFamilies.Window, PieceFamilies.Wall });
    private static readonly ValueList<PieceFamily> DeviceChain = ValueList.Of(PieceFamilies.Device);
    private static readonly ValueList<PieceFamily> OtherChain = ValueList.Of(PieceFamilies.Other);

    /// <summary>The family a family lies under; none at the top.</summary>
    public static Option<PieceFamily> ParentOf(PieceFamily family) =>
        family == PieceFamilies.Window ? Option.Some(PieceFamilies.Wall) : Option<PieceFamily>.None;

    /// <summary>The family and every family above it, narrowest first. Shared for Lattice's families; a new list for a mod's.</summary>
    public static ValueList<PieceFamily> ChainOf(PieceFamily family) =>
        family == PieceFamilies.Window ? WindowChain
        : family == PieceFamilies.Wall ? WallChain
        : family == PieceFamilies.Frame ? FrameChain
        : family == PieceFamilies.Cable ? CableChain
        : family == PieceFamilies.Pipe ? PipeChain
        : family == PieceFamilies.Chute ? ChuteChain
        : family == PieceFamilies.Device ? DeviceChain
        : family == PieceFamilies.Other ? OtherChain
        : Walk(family);

    /// <summary>Whether a chain holds a family.</summary>
    public static bool Holds(ValueList<PieceFamily> chain, PieceFamily family)
    {
        for (var i = 0; i < chain.Count; i++)
            if (chain[i] == family)
                return true;
        return false;
    }

    private static ValueList<PieceFamily> Walk(PieceFamily family)
    {
        var chain = new List<PieceFamily>(2) { family };
        for (var parent = ParentOf(family); parent.TryGet(out var above); parent = ParentOf(above))
            chain.Add(above);
        return ValueList.From(chain);
    }
}
