using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Ports;

namespace Lattice.Core.Runs;

/// <summary>
/// Splits a piece that spans several cells where joins land: one piece per cell it fills, each leaving its cell the
/// ways the long piece did, plus the join's ways at a join cell. A side join on a long straight gives a T there and
/// single straights elsewhere; joins from both sides give a cross. The game never merges a long straight,
/// so a join on one has to replace it.
/// </summary>
internal static class SplitPlanner
{
    /// <summary>The pieces that replace <paramref name="piece"/>, in the order of its cells; refused when the kit has no piece for a cell's ways.</summary>
    public static Result<ValueList<Choice>, Reason> Split(
        KitShapes kit, Occupant piece, IReadOnlyDictionary<Cell, Directions> joins, IGrid grid)
    {
        var pieces = new List<Choice>(piece.Cells.Count);
        foreach (var cell in piece.Cells)
        {
            var ways = PieceCells.At(piece, cell, grid);
            if (joins.TryGetValue(cell, out var join))
                ways = ways.Union(join);
            if (PieceChooser.Exactly(kit, cell, ways) is not { } choice)
                return Result<ValueList<Choice>, Reason>.Error(PieceChooser.NoPiece(kit, cell, ways));
            pieces.Add(choice);
        }

        return Result<ValueList<Choice>, Reason>.Ok(ValueList.From(pieces));
    }
}
