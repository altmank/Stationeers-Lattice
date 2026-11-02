using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Ports;

namespace Lattice.Core.Runs;

/// <summary>
/// Splits a piece that spans several cells where joins land: one piece per cell it fills, each leaving its cell the
/// ways the long piece did, plus the join's ways at a join cell. A side join on a long straight gives a T there and
/// single straights elsewhere; joins from both sides give a cross. The game never merges a long straight,
/// so a join on one has to replace it. A face the caller names as bare (an end cell's open outer face that joins
/// nothing) is dropped where the kit has a piece for what is left, so a line's end turns instead of keeping a stub.
/// </summary>
internal static class SplitPlanner
{
    /// <summary>
    /// The pieces that replace <paramref name="piece"/>, in the order of its cells; refused when the kit has no piece
    /// for a cell's ways, or (with <paramref name="flowOf"/>, a cell's flow on given ways) none that passes items as
    /// the flow there goes.
    /// </summary>
    public static Result<ValueList<Choice>, Reason> Split(
        KitShapes kit, Occupant piece, IReadOnlyDictionary<Cell, Directions> joins, IGrid grid, System.Func<Cell, Directions, Flow>? flowOf = null,
        IReadOnlyDictionary<Cell, Directions>? bare = null)
    {
        var pieces = new List<Choice>(piece.Cells.Count);
        foreach (var cell in piece.Cells)
        {
            var ways = PieceCells.At(piece, cell, grid);
            if (joins.TryGetValue(cell, out var join))
                ways = ways.Union(join);
            var choice = bare is not null && bare.TryGetValue(cell, out var open) && !open.IsEmpty
                ? Choose(kit, cell, ways.Without(open), flowOf) ?? Choose(kit, cell, ways, flowOf)
                : Choose(kit, cell, ways, flowOf);
            if (choice is null)
                return Result<ValueList<Choice>, Reason>.Error(kit.For(ways).IsSome
                    ? PieceChooser.FlowBlocked(kit, cell, ways)
                    : PieceChooser.NoPiece(kit, cell, ways));
            pieces.Add(choice);
        }

        return Result<ValueList<Choice>, Reason>.Ok(ValueList.From(pieces));
    }

    private static Choice? Choose(KitShapes kit, Cell cell, Directions ways, System.Func<Cell, Directions, Flow>? flowOf) =>
        PieceChooser.Exactly(kit, cell, ways, flowOf is not null && kit.Steers(ways) ? flowOf(cell, ways) : Flow.None);
}
