using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;

namespace Lattice.Core.Support;

/// <summary>
/// The world as it stands once every ghost a viewer sees is done, read cell by cell: the same overlay as
/// <see cref="SupportWorld"/> with every build, swap and removal ghost assumed. Build and swap ghosts' placements
/// stand, fully built, under the negated ghost id (<see cref="SupportWorld.IdOf"/>); the structures that removal and
/// swap ghosts take are gone, and a part-built structure a build ghost was started on stands as the ghost's finished
/// piece instead. Stale ghosts are left out, as the build order leaves them out. With <c>only</c>, just those ghosts
/// are assumed done and the rest of the board is left out (the effects of a set of ghosts). An assumed piece stands in
/// its own cell; with <c>shapes</c>, in every cell one of its spots lies in, as a built structure does. Each cell is read
/// once and cached, so one instance belongs to one pass over an unchanging board. Main thread, as the board is.
/// </summary>
internal sealed class PlannedWorld(IGhostBoard board, IWorldQuery world, ISet<GhostId>? only = null, Shapes? shapes = null) : IWorldQuery
{
    private readonly Dictionary<Cell, ValueList<StructureInfo>> _at = [];

    public Option<StructureInfo> Structure(ReferenceId id)
    {
        if (id.Value < 0)
            return board.Find(new GhostId(-id.Value)).TryGet(out var ghost) && Put(ghost).TryGet(out var put)
                ? Option.Some(SupportWorld.AssumedInfo(ghost.Id, put))
                : Option<StructureInfo>.None;
        return world.Structure(id).TryGet(out var real) && !Taken(real.Ref) ? Option.Some(real) : Option<StructureInfo>.None;
    }

    public ValueList<StructureInfo> At(Cell cell)
    {
        if (_at.TryGetValue(cell, out var known))
            return known;

        var real = world.At(cell);
        var ghosts = board.At(cell);
        var here = new List<StructureInfo>(real.Count + ghosts.Count);
        foreach (var info in real)
            if (!Taken(info.Ref))
                here.Add(info);
        foreach (var ghost in ghosts)
            if (Put(ghost).TryGet(out var put) && Fills(put, cell))
                here.Add(SupportWorld.AssumedInfo(ghost.Id, put));
        return _at[cell] = ValueList.From(here);
    }

    // The board lists a ghost in each cell its spots lie in and in its target's cell, so the put's shape decides.
    private bool Fills(Placement put, Cell cell)
    {
        if (put.At == cell)
            return true;
        if (shapes is null || !shapes.Of(put).TryGet(out var shape))
            return false;
        foreach (var spot in shape.Spots)
            if (spot.Cell == cell)
                return true;
        return false;
    }

    public Result<ValueList<StructureRef>, Reason> Select(StructureSelector selector) =>
        Result<ValueList<StructureRef>, Reason>.Error(
            Reason.Of(ReasonCodes.ModuleMissing, SupportCodes.Order, ValueMap<string, string>.Of("module", "selectors")));

    /// <summary>The real network of a real structure that stands; none for an assumed or taken one.</summary>
    public Option<NetworkRef> NetworkOf(ReferenceId id) =>
        id.Value < 0 || world.Structure(id).TryGet(out var real) && Taken(real.Ref) ? Option<NetworkRef>.None : world.NetworkOf(id);

    public Option<RoomRef> RoomAt(Cell cell) => world.RoomAt(cell);

    public Option<GhostId> AssumedFrom(StructureRef structure) =>
        structure.Id.Value < 0 ? Option.Some(new GhostId(-structure.Id.Value)) : Option<GhostId>.None;

    private bool Counts(Ghost ghost) => ghost.State is not GhostState.Stale && (only is null || only.Contains(ghost.Id));

    private Option<Placement> Put(Ghost ghost) => !Counts(ghost)
        ? Option<Placement>.None
        : ghost.Intent switch
        {
            Intent.Build build => Option.Some(build.Target),
            Intent.Swap swap => Option.Some(swap.New),
            _ => Option<Placement>.None,
        };

    private bool Taken(StructureRef structure)
    {
        foreach (var ghost in board.On(structure))
            if (Counts(ghost) && ghost.Intent is Intent.Remove or Intent.Swap or Intent.Build)
                return true;
        return false;
    }
}
