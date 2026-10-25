using Lattice.Api;
using Lattice.Core.Catalogue;
using Lattice.Core.Ports;

namespace Lattice.Core.Styles;

/// <summary>
/// A box in a piece's own space, in metres from its cell's centre, unturned. <see cref="Min"/> is at or below
/// <see cref="Max"/> on every axis.
/// </summary>
internal readonly record struct LocalBox
{
    private LocalBox(WorldVector min, WorldVector max)
    {
        Min = min;
        Max = max;
    }

    public WorldVector Min { get; }

    public WorldVector Max { get; }

    /// <summary>The box between two opposite corners, in any order.</summary>
    public static LocalBox Between(WorldVector corner, WorldVector opposite) => new(
        new WorldVector(System.Math.Min(corner.X, opposite.X), System.Math.Min(corner.Y, opposite.Y), System.Math.Min(corner.Z, opposite.Z)),
        new WorldVector(System.Math.Max(corner.X, opposite.X), System.Math.Max(corner.Y, opposite.Y), System.Math.Max(corner.Z, opposite.Z)));
}

/// <summary>
/// What styles read about a piece's look: its family (found by type, so a modded subclass inherits its base's) and the
/// bounds pick boxes are clipped to. Read from whatever prefab the game holds, modded pieces included. Main thread;
/// constant cost per call.
/// </summary>
internal interface IPieceLooks
{
    /// <summary>The piece's family; <see cref="PieceFamilies.Other"/> for a piece nothing classifies.</summary>
    PieceFamily Family(PrefabKey piece);

    /// <summary>The piece's bounds in its own space; none when unknown (picking then uses whole footprint cells).</summary>
    Option<LocalBox> Bounds(PrefabKey piece);
}

/// <summary>The looks known without the game: every piece is <see cref="PieceFamilies.Other"/>, with no bounds.</summary>
internal sealed class UnknownPieceLooks : IPieceLooks
{
    public PieceFamily Family(PrefabKey piece) => PieceFamilies.Other;

    public Option<LocalBox> Bounds(PrefabKey piece) => Option<LocalBox>.None;
}

/// <summary>
/// The looks the catalogue read from the game's prefabs: the family a mod registered, else the one found by type
/// (<see cref="IPieceFamilies"/>); the bounds box of the piece's look, none when the look is one grid cell or unread.
/// </summary>
internal sealed class CataloguePieceLooks(ICatalogue catalogue, IPieceFamilies families) : IPieceLooks
{
    public PieceFamily Family(PrefabKey piece) => families.FamilyOf(piece);

    public Option<LocalBox> Bounds(PrefabKey piece)
    {
        if (!catalogue.Piece(piece).TryGet(out var facts) || !facts.Look.TryGet(out var look) || look.Source is LookSource.Cell)
            return Option<LocalBox>.None;
        var centre = look.Bounds.Centre;
        var half = look.Bounds.Size;
        return Option.Some(LocalBox.Between(
            new WorldVector((float)(centre.X - half.X / 2), (float)(centre.Y - half.Y / 2), (float)(centre.Z - half.Z / 2)),
            new WorldVector((float)(centre.X + half.X / 2), (float)(centre.Y + half.Y / 2), (float)(centre.Z + half.Z / 2))));
    }
}
