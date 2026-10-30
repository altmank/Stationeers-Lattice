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
/// A piece with roled ends is chosen only in a turn that passes items as the cell's flow goes; where the kit has the
/// shape but no such turn, the cell is refused <see cref="ReasonCodes.FlowBlocked"/>. Kit tables are read once per kit
/// for the life of the chooser.
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

    /// <summary>
    /// The piece for a cell that must leave it at least the needed ways, joining the neighbours that point in; with
    /// <paramref name="flowOf"/>, passing items as the flow on the ways it leaves goes.
    /// </summary>
    public static Result<Choice, Reason> Choose(KitShapes kit, Cell cell, Directions needed, Neighbourhood around, System.Func<Directions, Flow>? flowOf = null)
    {
        var ways = needed.Union(around.EndsInto(cell, kit));
        if (Exactly(kit, cell, ways, FlowOf(kit, ways, flowOf)) is { } joined)
            return Result<Choice, Reason>.Ok(joined);
        if (Exactly(kit, cell, needed, FlowOf(kit, needed, flowOf)) is { } alone)
            return Result<Choice, Reason>.Ok(alone);
        var blocked = kit.For(ways).IsSome ? ways : kit.For(needed).IsSome ? needed : Directions.None;
        return Result<Choice, Reason>.Error(blocked.IsEmpty ? NoPiece(kit, cell, ways) : FlowBlocked(kit, cell, blocked));
    }

    /// <summary>The piece that leaves the cell exactly these ways, if the kit has one.</summary>
    public static Choice? Exactly(KitShapes kit, Cell cell, Directions ways) => Exactly(kit, cell, ways, Flow.None);

    /// <summary>The piece that leaves the cell exactly these ways and passes items as the flow goes, if the kit has one.</summary>
    public static Choice? Exactly(KitShapes kit, Cell cell, Directions ways, Flow flow) =>
        kit.For(ways, flow).TryGet(out var piece) ? new Choice(new Placement(piece.Piece, cell, piece.Turn), piece.Ways) : null;

    /// <summary>The reason a kit has no piece for a cell's ways.</summary>
    public static Reason NoPiece(KitShapes kit, Cell cell, Directions ways) => Refusal(ReasonCodes.NoPiece, kit, cell, ways);

    /// <summary>The reason no piece the kit has for a cell's ways passes items as the run flows.</summary>
    public static Reason FlowBlocked(KitShapes kit, Cell cell, Directions ways) => Refusal(ReasonCodes.FlowBlocked, kit, cell, ways);

    private static Flow FlowOf(KitShapes kit, Directions ways, System.Func<Directions, Flow>? flowOf) =>
        flowOf is not null && kit.Steers(ways) ? flowOf(ways) : Flow.None;

    private static Reason Refusal(ReasonCode code, KitShapes kit, Cell cell, Directions ways) =>
        Reason.Of(code, RunCodes.Runs, ValueMap
            .Of("kit", kit.Kit.Hash.ToString())
            .With("cell", $"{cell.X},{cell.Y},{cell.Z}")
            .With("shape", ways.ToString()));
}
