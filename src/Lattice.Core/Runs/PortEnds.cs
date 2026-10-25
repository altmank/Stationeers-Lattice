using System;
using System.Collections.Generic;
using System.Globalization;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Catalogue;
using Lattice.Core.Grid;
using Lattice.Core.Ports;

namespace Lattice.Core.Runs;

/// <summary>A run end a request named by its port: the port as it stands now, the cell it joins, and the way from that cell back into the port.</summary>
internal sealed record PortTarget(PortEnd End, Port Port, Direction Back)
{
    /// <summary>The run's end cell: the port's <see cref="Port.Joins"/>.</summary>
    public Cell Cell => Port.Joins;
}

/// <summary>
/// Resolves a run or route request's port ends against the board as the sender sees it: each ghost's new piece, its
/// ports at that placement (<see cref="PiecePorts"/>), and the one named. Refused, before anything is laid, when the ghost
/// is not to be seen (as an unknown one), the port is not its piece's, the ghost has no new piece, the request's cell is
/// not the port's joining cell now, the kit shares no kind with the port, or both ends name one port.
/// </summary>
internal sealed class PortEnds(PiecePorts ports, RuleId by)
{
    /// <summary>The request's port ends, the from end first; empty when it names none.</summary>
    public Result<ValueList<PortTarget>, Reason> Resolve(
        Option<PortEnd> fromPort, Cell from, Option<PortEnd> toPort, Cell to, KitShapes kit, IGhostBoard board)
    {
        if (fromPort.IsNone && toPort.IsNone)
            return Result<ValueList<PortTarget>, Reason>.Ok(ValueList<PortTarget>.Empty);
        if (fromPort.TryGet(out var a) && toPort.TryGet(out var b) && a == b)
            return Refuse(Facts(a).With("problem", "same-port"), ReasonCodes.InvalidValue);

        var targets = new List<PortTarget>(2);
        foreach (var (end, cell) in new[] { (fromPort, from), (toPort, to) })
        {
            if (!end.TryGet(out var named))
                continue;
            var resolved = One(named, cell, kit, board);
            if (!resolved.TryGet(out var target))
                return Result<ValueList<PortTarget>, Reason>.Error(resolved.ErrorOf());
            targets.Add(target);
        }

        return Result<ValueList<PortTarget>, Reason>.Ok(ValueList.From(targets));
    }

    private Result<PortTarget, Reason> One(PortEnd end, Cell cell, KitShapes kit, IGhostBoard board)
    {
        if (end is not PortEnd.OnGhost onGhost)
            return Fail(ValueMap.Of("problem", "port-end"), ReasonCodes.UnknownKind);
        var facts = Facts(onGhost);
        if (!board.Find(onGhost.Ghost).TryGet(out var ghost))
            return Fail(facts, ReasonCodes.NotFound);
        Placement? placement = ghost.Intent switch
        {
            Intent.Build build => build.Target,
            Intent.Swap swap => swap.New,
            _ => null,
        };
        if (placement is null)
            return Fail(facts.With("problem", "no-new-piece"), ReasonCodes.InvalidValue);
        var listed = ports.At(placement);
        if (!listed.TryGet(out var all))
            return Fail(facts.With("problem", "port"), ReasonCodes.NotFound);

        Port? port = null;
        foreach (var candidate in all)
            if (candidate.Index == onGhost.Port)
                port = candidate;
        if (port is null)
            return Fail(facts.With("problem", "port"), ReasonCodes.NotFound);
        if (port.Joins != cell)
            return Fail(facts.With("expected", Text(cell)).With("now", Text(port.Joins)), ReasonCodes.ChangedSince);
        if (!kit.Joins(port))
            return Fail(facts.With("kit", kit.Kit.Hash.ToString(CultureInfo.InvariantCulture)), ReasonCodes.PortMismatch);
        var toward = DirectionMath.DirectionOf(port.Toward) ?? Direction.XPlus;
        return Result<PortTarget, Reason>.Ok(new PortTarget(end, port, toward.Opposite()));
    }

    private static ValueMap<string, string> Facts(PortEnd end) => end is PortEnd.OnGhost onGhost
        ? ValueMap.Of("ghost", onGhost.Ghost.Value.ToString(CultureInfo.InvariantCulture)).With("port", onGhost.Port.ToString())
        : ValueMap.Of("problem", "port-end");

    private static string Text(Cell cell) => FormattableString.Invariant($"{cell.X},{cell.Y},{cell.Z}");

    private Result<ValueList<PortTarget>, Reason> Refuse(ValueMap<string, string> facts, ReasonCode code) =>
        Result<ValueList<PortTarget>, Reason>.Error(Reason.Of(code, by, facts));

    private Result<PortTarget, Reason> Fail(ValueMap<string, string> facts, ReasonCode code) =>
        Result<PortTarget, Reason>.Error(Reason.Of(code, by, facts));
}
