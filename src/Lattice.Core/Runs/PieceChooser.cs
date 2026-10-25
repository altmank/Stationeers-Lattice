using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Ports;

namespace Lattice.Core.Runs;

/// <summary>A chosen piece in its cell and turn, and the ways it leaves the cell.</summary>
internal sealed record Choice(Placement Placement, Directions Ways);

/// <summary>
/// The right piece and turn for a small cell: the kit's piece whose ends leave the cell exactly the ways it must
/// (straight, corner, T, cross, 3-, 5-, 6-way), found from the catalogue's ends under the 24 turns, never from piece
/// names. The ways are the ones the caller needs plus every built or planned neighbour's end that points into the
/// cell and joins the kit; when the kit has no piece for that set, the needed ways alone (the neighbour stays open).
/// Kit tables are read once per kit for the life of the chooser.
/// </summary>
internal sealed class PieceChooser(ICatalogue catalogue, IGrid grid)
{
    private readonly Dictionary<KitKey, Result<KitShapes, Reason>> _kits = [];

    /// <summary>The kit's run pieces, or why it has none.</summary>
    public Result<KitShapes, Reason> Kit(KitKey kit)
    {
        if (!_kits.TryGetValue(kit, out var shapes))
            _kits[kit] = shapes = KitShapes.Read(catalogue, grid, kit);
        return shapes;
    }

    /// <summary>The piece for a cell from its neighbours alone.</summary>
    public static Result<Choice, Reason> Choose(KitShapes kit, Cell cell, Neighbourhood around) =>
        Choose(kit, cell, Directions.None, around);

    /// <summary>The piece for a cell that must leave it at least the needed ways, joining the neighbours that point in.</summary>
    public static Result<Choice, Reason> Choose(KitShapes kit, Cell cell, Directions needed, Neighbourhood around)
    {
        var ways = needed.Union(around.EndsInto(cell, kit));
        if (Exactly(kit, cell, ways) is { } joined)
            return Result<Choice, Reason>.Ok(joined);
        return Exactly(kit, cell, needed) is { } alone
            ? Result<Choice, Reason>.Ok(alone)
            : Result<Choice, Reason>.Error(NoPiece(kit, cell, ways));
    }

    /// <summary>The piece that leaves the cell exactly these ways, if the kit has one.</summary>
    public static Choice? Exactly(KitShapes kit, Cell cell, Directions ways) =>
        kit.For(ways).TryGet(out var piece) ? new Choice(new Placement(piece.Piece, cell, piece.Turn), piece.Ways) : null;

    /// <summary>The reason a kit has no piece for a cell's ways.</summary>
    public static Reason NoPiece(KitShapes kit, Cell cell, Directions ways) =>
        Reason.Of(ReasonCodes.NoPiece, RunCodes.Runs, ValueMap
            .Of("kit", kit.Kit.Hash.ToString())
            .With("cell", $"{cell.X},{cell.Y},{cell.Z}")
            .With("shape", ways.ToString()));
}
