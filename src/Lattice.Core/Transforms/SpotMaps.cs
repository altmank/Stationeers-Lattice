using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Grid;
using Lattice.Core.Ports;

namespace Lattice.Core.Transforms;

/// <summary>Where a transformation takes one spot: its cell, and its face slot turned or reflected with it.</summary>
internal interface ISpotMap
{
    SpotKey Of(SpotKey spot);
}

/// <summary>A move by whole cells: the same slot of the moved cell.</summary>
internal readonly record struct Moving(CellOffset By) : ISpotMap
{
    public SpotKey Of(SpotKey spot) => spot with { Cell = spot.Cell.Plus(By) };
}

/// <summary>A turn about <paramref name="From"/> that ends at <paramref name="To"/>: a rotation has both at its pivot, a template placement goes from its local origin to the placed one.</summary>
internal readonly record struct Turning(IGrid Grid, Cell From, Cell To, Turn Turn) : ISpotMap
{
    public SpotKey Of(SpotKey spot)
    {
        var offset = Grid.Apply(Turn, new CellOffset(spot.Cell.X - From.X, spot.Cell.Y - From.Y, spot.Cell.Z - From.Z));
        var cell = To.Plus(offset);
        return Faces.DirectionOf(spot.Slot) is { } face
            ? new SpotKey(cell, Faces.SlotOf(Grid.Apply(Turn, face)))
            : spot with { Cell = cell };
    }
}

/// <summary>A mirror: the reflected cell, a face across the plane's normal facing the other way.</summary>
internal readonly record struct Reflecting(IGrid Grid, MirrorPlane Plane) : ISpotMap
{
    public SpotKey Of(SpotKey spot)
    {
        var cell = Grid.Reflect(spot.Cell, Plane);
        return Faces.DirectionOf(spot.Slot) is { } face
            ? new SpotKey(cell, Faces.SlotOf(Grid.Reflect(face, Plane.Normal)))
            : spot with { Cell = cell };
    }
}

/// <summary>Spots taken through a transformation and compared as sets. Quadratic in the spots of one piece, a handful.</summary>
internal static class SpotMaps
{
    /// <summary>Where <paramref name="map"/> takes every spot; none when there are none or one leaves the large cell centres, so the result could not stand on a face.</summary>
    public static Option<SpotKey[]> Target<TMap>(IReadOnlyList<SpotKey> spots, TMap map) where TMap : ISpotMap
    {
        if (spots.Count == 0)
            return Option<SpotKey[]>.None;
        var mapped = new SpotKey[spots.Count];
        for (var i = 0; i < mapped.Length; i++)
        {
            mapped[i] = map.Of(spots[i]);
            if (!Placements.IsCentre(mapped[i].Cell, GridSize.Large))
                return Option<SpotKey[]>.None;
        }

        return Option.Some(mapped);
    }

    /// <summary>Whether two lists hold the same spots, each as often.</summary>
    public static bool Same(IReadOnlyList<SpotKey> spots, IReadOnlyList<SpotKey> target)
    {
        if (spots.Count != target.Count)
            return false;
        var matched = new bool[target.Count];
        for (var i = 0; i < spots.Count; i++)
        {
            var found = false;
            for (var j = 0; j < target.Count && !found; j++)
                if (!matched[j] && target[j] == spots[i])
                    found = matched[j] = true;
            if (!found)
                return false;
        }

        return true;
    }
}
