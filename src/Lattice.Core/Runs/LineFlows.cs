using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Grid;
using Lattice.Core.Ports;

namespace Lattice.Core.Runs;

/// <summary>
/// The flow of the lines a run meets, at the cell it meets them: first the flow of earlier runs of the same request
/// (a route's main run, for its branches), then what the line's pieces say. Plain chutes pass items either way, so a
/// line has a flow only where a roled end sits on it: walking from the cell along the line, the first end that faces
/// back is an output (items come from that side) or an input (they go there), or a two-ended piece's far end says so (decompile
/// <c>Assets.Scripts.Objects.Pipes/Chute.cs</c> <c>IsValidInputConnection</c>). The walk goes through plain pieces of
/// two ends, at most <see cref="Reach"/> pieces, and stops at a fork, a gap or a built or planned piece of another
/// network.
/// </summary>
internal sealed class LineFlows(Neighbourhood around, IGrid grid, KitShapes kit, IReadOnlyDictionary<Cell, Flow>? known = null)
{
    /// <summary>The most pieces a walk along a line reads.</summary>
    public const int Reach = 64;

    /// <summary>The flow on a cell's line ways, each way's when it is readable.</summary>
    public Flow At(Cell cell, Directions lineWays)
    {
        if (lineWays.IsEmpty)
            return Flow.None;
        var flow = Flow.None;
        if (known is not null && known.TryGetValue(cell, out var earlier))
            flow = new Flow(earlier.In.Intersect(lineWays), earlier.Out.Intersect(lineWays));
        for (var way = Direction.XPlus; way <= Direction.ZMinus; way++)
            if (lineWays.Contains(way) && !flow.Known.Contains(way))
                flow = flow.Union(Along(cell, way));
        if (lineWays.StraightAxis.IsSome && flow.Known.Count == 1)
            for (var way = Direction.XPlus; way <= Direction.ZMinus; way++)
                if (lineWays.Contains(way) && !flow.Known.Contains(way))
                    flow = flow.In.IsEmpty ? flow.Entering(way) : flow.Leaving(way);
        return flow;
    }

    /// <summary>The flow on one way out of a cell, read along the line that way; none when the line does not say.</summary>
    public Flow Along(Cell cell, Direction way)
    {
        var (from, heading) = (cell, way);
        for (var step = 0; step < Reach; step++)
        {
            var next = grid.Neighbour(from, heading, kit.Grid);
            var back = heading.Opposite();
            if (known is not null && known.TryGetValue(next, out var earlier))
            {
                if (earlier.Out.Contains(back))
                    return Flow.None.Entering(way);
                if (earlier.In.Contains(back))
                    return Flow.None.Leaving(way);
            }

            if (Facing(next, back) is not var (occupant, end))
                return Flow.None;
            if (occupant.Shape.Piece.Ends[end].Role.TryGet(out var role))
                return Steering.IsInput(role) ? Flow.None.Leaving(way) : Flow.None.Entering(way);
            if (occupant.Shape.Ends.Count != 2)
                return Flow.None;
            var other = 1 - end;
            if (occupant.Shape.Piece.Ends[other].Role.TryGet(out var far))
                return Steering.IsInput(far) ? Flow.None.Entering(way) : Flow.None.Leaving(way);
            (from, heading) = (occupant.Shape.Ends[other].Cell, occupant.Shape.Ends[other].Facing);
        }

        return Flow.None;
    }

    // The planned or built piece with an end in the cell facing back along the walk, joining the kit's networks.
    private (Occupant Occupant, int End)? Facing(Cell cell, Direction back)
    {
        foreach (var occupant in around.At(cell))
        {
            var ends = occupant.Shape.Ends;
            for (var i = 0; i < ends.Count; i++)
                if (ends[i].Cell == cell && ends[i].Facing == back && kit.Joins(occupant.Shape.Piece.Ends[i]))
                    return (occupant, i);
        }

        return null;
    }
}
