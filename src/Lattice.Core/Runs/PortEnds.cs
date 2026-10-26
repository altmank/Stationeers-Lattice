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
/// Resolves a run or route request's port ends against the board as the sender sees it and the world as it stands: a
/// ghost's new piece, or a built structure's piece, cell and turn (found by id, else by piece and cell); its ports at
/// that placement (<see cref="PiecePorts"/>); and the one named. Refused, before anything is laid, when the ghost is not
/// to be seen (as an unknown one) or the structure no longer stands, the port is not its piece's, the ghost has no new
/// piece, the request's cell is not the port's joining cell now, the kit shares no kind with the port, or both ends
/// name one port.
/// </summary>
internal sealed class PortEnds(PiecePorts ports, RuleId by)
{
    /// <summary>The request's port ends, the from end first; empty when it names none.</summary>
    public Result<ValueList<PortTarget>, Reason> Resolve(
        Option<PortEnd> fromPort, Cell from, Option<PortEnd> toPort, Cell to, KitShapes kit, IGhostBoard board, IWorldQuery world)
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
            var resolved = One(named, cell, kit, board, world);
            if (!resolved.TryGet(out var target))
                return Result<ValueList<PortTarget>, Reason>.Error(resolved.ErrorOf());
            targets.Add(target);
        }

        return Result<ValueList<PortTarget>, Reason>.Ok(ValueList.From(targets));
    }

    private Result<PortTarget, Reason> One(PortEnd end, Cell cell, KitShapes kit, IGhostBoard board, IWorldQuery world)
    {
        var facts = Facts(end);
        var placed = end switch
        {
            PortEnd.OnGhost onGhost => OfGhost(onGhost, board, facts),
            PortEnd.OnStructure onStructure => OfStructure(onStructure, world, facts),
            _ => Fail<Placement>(facts, ReasonCodes.UnknownKind),
        };
        if (!placed.TryGet(out var placement))
            return Result<PortTarget, Reason>.Error(placed.ErrorOf());
        var listed = ports.At(placement);
        if (!listed.TryGet(out var all))
            return Fail(facts.With("problem", "port"), ReasonCodes.NotFound);

        var index = IndexOf(end);
        Port? port = null;
        foreach (var candidate in all)
            if (candidate.Index == index)
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

    /// <summary>A ghost's new piece; none to be seen answers as an unknown ghost does.</summary>
    private Result<Placement, Reason> OfGhost(PortEnd.OnGhost end, IGhostBoard board, ValueMap<string, string> facts)
    {
        if (!board.Find(end.Ghost).TryGet(out var ghost))
            return Fail<Placement>(facts, ReasonCodes.NotFound);
        return ghost.Intent switch
        {
            Intent.Build build => Result<Placement, Reason>.Ok(build.Target),
            Intent.Swap swap => Result<Placement, Reason>.Ok(swap.New),
            _ => Fail<Placement>(facts.With("problem", "no-new-piece"), ReasonCodes.InvalidValue),
        };
    }

    /// <summary>A standing structure's piece, cell and turn: by its id, else the structure of its piece in its cell.</summary>
    private Result<Placement, Reason> OfStructure(PortEnd.OnStructure end, IWorldQuery world, ValueMap<string, string> facts)
    {
        var named = end.Structure;
        if (world.Structure(named.Id).TryGet(out var byId))
            return Result<Placement, Reason>.Ok(new Placement(byId.Ref.Piece, byId.Ref.At, byId.Turn));
        foreach (var info in world.At(named.At))
            if (info.Ref.Piece == named.Piece && info.Ref.At == named.At)
                return Result<Placement, Reason>.Ok(new Placement(info.Ref.Piece, info.Ref.At, info.Turn));
        return Fail<Placement>(facts, ReasonCodes.NotFound);
    }

    private static PortIndex IndexOf(PortEnd end) => end switch
    {
        PortEnd.OnGhost onGhost => onGhost.Port,
        PortEnd.OnStructure onStructure => onStructure.Port,
        _ => default,
    };

    private static ValueMap<string, string> Facts(PortEnd end) => end switch
    {
        PortEnd.OnGhost onGhost => ValueMap.Of("ghost", onGhost.Ghost.Value.ToString(CultureInfo.InvariantCulture)).With("port", onGhost.Port.ToString()),
        PortEnd.OnStructure onStructure => ValueMap
            .Of("structure", onStructure.Structure.Id.Value.ToString(CultureInfo.InvariantCulture))
            .With("port", onStructure.Port.ToString()),
        _ => ValueMap.Of("problem", "port-end"),
    };

    private static string Text(Cell cell) => FormattableString.Invariant($"{cell.X},{cell.Y},{cell.Z}");

    private Result<ValueList<PortTarget>, Reason> Refuse(ValueMap<string, string> facts, ReasonCode code) =>
        Result<ValueList<PortTarget>, Reason>.Error(Reason.Of(code, by, facts));

    private Result<PortTarget, Reason> Fail(ValueMap<string, string> facts, ReasonCode code) => Fail<PortTarget>(facts, code);

    private Result<T, Reason> Fail<T>(ValueMap<string, string> facts, ReasonCode code)
        where T : notnull =>
        Result<T, Reason>.Error(Reason.Of(code, by, facts));
}
