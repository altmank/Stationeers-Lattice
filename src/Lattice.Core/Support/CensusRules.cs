using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Catalogue;
using Lattice.Core.Ports;

namespace Lattice.Core.Support;

/// <summary>Reasons a census rule gives: <see cref="ReasonCodes.NeedsSupport"/> from the group, with the game's own string.</summary>
internal static class SupportReasons
{
    public static Reason Needs(RuleId group, string gameString) =>
        Reason.Of(ReasonCodes.NeedsSupport, group, ValueMap<string, string>.Of("group", group.Value).With("game", gameString));
}

/// <summary>
/// A frame half a grid below (<c>SmallGrid.HasFrameBelow</c>, decompile Assets.Scripts.Objects/SmallGrid.cs):
/// a structure that allows mounting holds the large cell below the piece's centre, and the large cell 0.99 m in front
/// of it is not blocked. A large-grid piece looks at the large cell below its own (<c>LargeElectrical</c>, census).
/// The game's string: <c>PlacementRequiresFrame</c>.
/// </summary>
internal sealed class FrameBelowRule(SupportGeometry geometry) : ICensusRule
{
    public RuleId Group => SupportGroups.FrameBelow;

    public Judgement Judge(SupportCase @case) => FrameBelow.Find(geometry, @case, Group);
}

/// <summary>
/// A frame below as <see cref="FrameBelowRule"/>, or solid ground (<c>Flag</c>, census). Terrain is not in Lattice's
/// ports, so without a frame the game's own check answers.
/// </summary>
internal sealed class FrameOrGroundRule(SupportGeometry geometry) : ICensusRule
{
    public RuleId Group => SupportGroups.FrameOrGround;

    public Judgement Judge(SupportCase @case)
    {
        var frame = FrameBelow.Find(geometry, @case, Group);
        return frame is Judgement.Unsupported ? Judgement.Game : frame;
    }
}

internal static class FrameBelow
{
    public static Judgement Find(SupportGeometry geometry, SupportCase @case, RuleId group)
    {
        var (_, at, piece, _) = @case;
        Cell below;
        if (piece.Grid == GridSize.Large)
            below = geometry.Grid.Neighbour(at.At, geometry.Grid.Apply(at.Turn, Direction.YMinus), GridSize.Large);
        else
        {
            var centre = geometry.CentreOf(at, piece);
            var half = SupportGeometry.Metres(piece.Grid) / 2f;
            var front = geometry.Along(geometry.Along(centre, at.Turn, Direction.ZPlus, 0.99f), at.Turn, Direction.YPlus, 0.01f);
            if (!geometry.Blocked(@case, geometry.LargeCellAt(front)).TryGet(out var blocked))
                return Judgement.Game;
            if (blocked)
                return new Judgement.Unsupported(SupportReasons.Needs(group, "PlacementRequiresFrame"));
            below = geometry.LargeCellAt(geometry.Along(centre, at.Turn, Direction.YMinus, half));
        }

        if (!geometry.CentreHolders(@case, below).TryGet(out var holders))
            return Judgement.Game;
        foreach (var (info, _) in holders)
        {
            if (!geometry.FactsOf(info.Ref.Piece).TryGet(out var support))
                return Judgement.Game;
            if (support.AllowsMounting)
                return new Judgement.Supported(ValueList.Of(info.Ref));
        }

        return new Judgement.Unsupported(SupportReasons.Needs(group, "PlacementRequiresFrame"));
    }
}

/// <summary>
/// A blocked grid behind the mount point (<c>SmallGrid.CanMountOnWall</c>, <c>GetSupport</c>,
/// Assets.Scripts.Objects/SmallGrid.cs). For a piece on one large face plane: the large cell in front must not be
/// blocked (<c>InvalidBlocked</c>); the support is what holds the centre of the large cell behind, else a face piece
/// (wall, window, plate) on the plane between the two cells (<c>InvalidMissingSupport</c> without either). A piece that
/// requires a frame refuses a face piece (<c>InvalidRequiresFrame</c>); the support must allow mounting
/// (<c>InvalidNotMountable</c>). A small piece on no face plane, inside a large cell, fails <c>InvalidMissingSupport</c>
/// whatever stands around it: the game's default branch for a position off every face, edge and corner, which returns
/// before any support is looked at (<c>CanMountOnWall</c>, the switch on <c>RocketGrid.GridLocation</c>). A piece on
/// an edge or corner, or a large-grid piece, is the game's to answer.
/// </summary>
internal sealed class WallMountRule(SupportGeometry geometry) : ICensusRule
{
    public RuleId Group => SupportGroups.WallMount;

    public Judgement Judge(SupportCase @case)
    {
        var (_, at, piece, _) = @case;
        if (piece.Grid != GridSize.Small)
            return Judgement.Game;
        var planes = SupportGeometry.FacePlanes(at.At);
        if (planes == 0)
            return new Judgement.Unsupported(SupportReasons.Needs(Group, "InvalidMissingSupport"));
        if (planes != 1 || !geometry.FactsOf(at.Piece).TryGet(out var own))
            return Judgement.Game;
        var centre = geometry.CentreOf(at, piece);
        var half = SupportGeometry.Metres(piece.Grid) / 2f;
        var front = geometry.LargeCellAt(geometry.Along(centre, at.Turn, Direction.ZPlus, half));
        if (!geometry.Blocked(@case, front).TryGet(out var blocked))
            return Judgement.Game;
        if (blocked)
            return new Judgement.Unsupported(SupportReasons.Needs(Group, "InvalidBlocked"));

        var rear = geometry.LargeCellAt(geometry.Along(centre, at.Turn, Direction.ZMinus, half));
        if (!geometry.CentreHolders(@case, rear).TryGet(out var holders))
            return Judgement.Game;
        var onFace = false;
        if (holders.Count == 0)
        {
            if (!geometry.FaceHolders(@case, rear, geometry.Grid.Apply(at.Turn, Direction.ZPlus)).TryGet(out holders))
                return Judgement.Game;
            onFace = holders.Count > 0;
        }

        if (holders.Count == 0)
            return new Judgement.Unsupported(SupportReasons.Needs(Group, "InvalidMissingSupport"));
        if (onFace && own.RequiresFrame)
            return new Judgement.Unsupported(SupportReasons.Needs(Group, "InvalidRequiresFrame"));
        foreach (var (info, _) in holders)
        {
            if (!geometry.FactsOf(info.Ref.Piece).TryGet(out var support))
                return Judgement.Game;
            if (support.AllowsMounting)
                return new Judgement.Supported(ValueList.Of(info.Ref));
        }

        return new Judgement.Unsupported(SupportReasons.Needs(Group, "InvalidNotMountable"));
    }
}

/// <summary>
/// Mounted on a straight run piece in its own small cell, along the same line (<c>DeviceCableMounted.CanConstruct</c>,
/// <c>DevicePipeMounted.CanConstruct</c>, <c>SmallGrid.IsSameOrientation</c>). The host is what takes the cell's
/// <paramref name="slot"/> by its footprint. The game's strings: <paramref name="missing"/> with no host,
/// <paramref name="broken"/> on a broken host, <c>PlacementBlockedBySmallGrid</c> for a host that is not a straight
/// on the same line, <c>PipeContentTypeIncorrect</c> for a pipe of other content (<c>DevicePipeMounted.IsContentMatch</c>:
/// the same content, or the device takes any).
/// </summary>
internal sealed class OnRunRule(SupportGeometry geometry, RuleId group, Slot slot, string missing, string broken) : ICensusRule
{
    public RuleId Group => group;

    public Judgement Judge(SupportCase @case)
    {
        var (_, at, piece, _) = @case;
        if (!geometry.Taking(@case, at.At, new SpotKey(at.At, slot)).TryGet(out var hosts))
            return Judgement.Game;
        if (hosts.Count == 0)
            return new Judgement.Unsupported(SupportReasons.Needs(Group, missing));
        var (info, host) = hosts[0];
        if (info.State is BuildState.Broken)
            return new Judgement.Unsupported(SupportReasons.Needs(Group, broken));
        if (!SupportGeometry.IsStraight(host) || !geometry.SameAxis(info.Turn, at.Turn))
            return new Judgement.Unsupported(SupportReasons.Needs(Group, "PlacementBlockedBySmallGrid"));
        if (piece.Pipe.TryGet(out var mine) && host.Pipe.TryGet(out var theirs) && mine != theirs && mine != PipeContent.Any)
            return new Judgement.Unsupported(SupportReasons.Needs(Group, "PipeContentTypeIncorrect"));
        return new Judgement.Supported(ValueList.Of(info.Ref));
    }
}

/// <summary>
/// Outside every room (<c>IndustrialFiltration</c>: <c>"Must be placed outside"</c>, <c>RoomController.GetRoom</c>).
/// Rooms come from the game; Lattice does not flood rooms. In a room of the built world it fails, outside one it
/// passes; when ghosts assumed built change cells within <see cref="Reach"/> large cells, they may open or close a
/// room, so the answer turns on them.
/// </summary>
internal sealed class OutsideRule(SupportGeometry geometry) : ICensusRule
{
    /// <summary>How far, in large cells, an assumed change is taken to reach a room: a heuristic, not a game rule.</summary>
    public const int Reach = 8;

    public RuleId Group => SupportGroups.Outside;

    public Judgement Judge(SupportCase @case)
    {
        var (_, at, piece, world) = @case;
        var cell = geometry.LargeCellAt(geometry.CentreOf(at, piece));
        var reach = Reach * 20;
        var near = world.ChangesIn(
            new Cell(cell.X - reach, cell.Y - reach, cell.Z - reach),
            new Cell(cell.X + reach, cell.Y + reach, cell.Z + reach),
            @case.Ghost);
        if (near.Count > 0)
            return new Judgement.Uncertain(near);
        return world.RoomAt(cell).IsSome
            ? new Judgement.Unsupported(SupportReasons.Needs(Group, "Must be placed outside"))
            : Judgement.Nothing;
    }
}

/// <summary>
/// Elevator shafts (<c>ElevatorShaft.CanConstruct</c>, Assets.Scripts.Objects.Electrical/ElevatorShaft.cs): a shaft
/// above or below must have the same turn (<c>ElevatorShaftRotationFail</c>). With shafts both above and below, the
/// game refuses when both carry a carriage, a world fact Lattice does not hold, so the game answers.
/// </summary>
internal sealed class ElevatorShaftRule(SupportGeometry geometry) : ICensusRule
{
    public RuleId Group => SupportGroups.ElevatorShaft;

    public Judgement Judge(SupportCase @case)
    {
        var (_, at, piece, world) = @case;
        var above = Shaft(world, geometry.Grid.Neighbour(at.At, Direction.YPlus, piece.Grid));
        var below = Shaft(world, geometry.Grid.Neighbour(at.At, Direction.YMinus, piece.Grid));
        if ((above.TryGet(out var up) && up.Turn != at.Turn) || (below.TryGet(out var down) && down.Turn != at.Turn))
            return new Judgement.Unsupported(SupportReasons.Needs(Group, "ElevatorShaftRotationFail"));
        if (above.TryGet(out up) && below.TryGet(out down) && world.IsReal(up.Ref) && world.IsReal(down.Ref))
            return Judgement.Game;
        return Judgement.Nothing;
    }

    private static Option<StructureInfo> Shaft(SupportWorld world, Cell cell)
    {
        foreach (var info in world.At(cell))
            if (world.Catalogue.Piece(info.Ref.Piece).TryGet(out var facts) &&
                facts.SupportGroup.TryGet(out var group) && group == SupportGroups.ElevatorShaft)
                return Option.Some(info);
        return Option<StructureInfo>.None;
    }
}
