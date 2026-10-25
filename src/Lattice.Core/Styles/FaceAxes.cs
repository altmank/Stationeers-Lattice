using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Grid;
using Lattice.Core.Ports;

namespace Lattice.Core.Styles;

/// <summary>
/// The axis a placed piece's face is square to (<see cref="FaceAxes.Of"/> over its spots), kept per piece, turn and
/// place within its large cell: a piece's spots move with whole large cells, so asking again, anywhere, allocates
/// nothing. What <see cref="StyleMatch.Facing"/> matches and <see cref="IPieceCatalogue.FacingOf"/> answers. Main thread.
/// </summary>
internal sealed class Facings(Shapes shapes)
{
    private const int LargeStep = 20;

    private readonly Dictionary<(PrefabKey Piece, Turn Turn, int X, int Y, int Z), Axis?> _known = new();

    /// <summary>The facings of a catalogue's pieces on the game's grid.</summary>
    public static Facings Over(ICatalogue catalogue) => new(new Shapes(catalogue, new CubeGrid()));

    /// <summary>The axis the piece's face is square to under the placement; null for a piece off the faces or one the catalogue lacks.</summary>
    public Axis? Of(Placement placement) => Of(placement.Piece, placement.At, placement.Turn);

    /// <summary>As <see cref="Of(Placement)"/>, for a piece at a cell and turn, with no placement to make.</summary>
    public Axis? Of(PrefabKey piece, Cell at, Turn turn)
    {
        var key = (piece, turn, Within(at.X), Within(at.Y), Within(at.Z));
        if (_known.TryGetValue(key, out var known))
            return known;
        var axis = shapes.Of(new Placement(piece, at, turn)).TryGet(out var shape) ? FaceAxes.Of(shape.Spots) : null;
        _known[key] = axis;
        return axis;
    }

    private static int Within(int key) => ((key % LargeStep) + LargeStep) % LargeStep;
}

/// <summary>Which axis a placed piece's face is square to, from the spots it takes.</summary>
internal static class FaceAxes
{
    /// <summary>The axis every spot's face is square to; null when a spot is not a face, the faces differ in axis, or there are none.</summary>
    public static Axis? Of(IReadOnlyList<SpotKey> spots)
    {
        Axis? axis = null;
        for (var i = 0; i < spots.Count; i++)
        {
            if (Faces.DirectionOf(spots[i].Slot) is not { } face)
                return null;
            var along = face.AxisOf();
            if (axis is { } seen && seen != along)
                return null;
            axis = along;
        }

        return axis;
    }
}
