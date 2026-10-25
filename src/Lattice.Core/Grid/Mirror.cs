using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Ports;

namespace Lattice.Core.Grid;

/// <summary>
/// Mirroring pieces across a plane. A piece is mirrorable when its reflected connection ends (ports included) equal its
/// own ends under one of the 24 turns, computed from catalogue ends with no hand table.
/// Reflections are about the piece's origin, the point its turns turn about.
/// Whether a piece mirrors does not depend on the plane's axis: two reflections make a turn.
/// </summary>
internal sealed class Mirror(IGrid grid)
{
    /// <summary>
    /// Refuses a plane that would send cell centres of the grid off them: on the large grid (centres on odd metres) the
    /// plane sits on a whole metre, a multiple of 10 dm; on the small grid (every half metre) on a multiple of 5 dm.
    /// </summary>
    public static Result<MirrorPlane, Reason> Fit(MirrorPlane plane, GridSize grid)
    {
        var multiple = grid switch
        {
            GridSize.Large => 10,
            GridSize.Small => 5,
            _ => throw new System.ArgumentOutOfRangeException(nameof(grid), grid, "not a grid"),
        };
        return plane.At % multiple == 0
            ? Result<MirrorPlane, Reason>.Ok(plane)
            : Result<MirrorPlane, Reason>.Error(Reason.InvalidValue(nameof(MirrorPlane), plane.At.ToString(), $"multiple-of-{multiple}"));
    }

    /// <summary>The ends seen in a mirror square to <paramref name="normal"/> through the piece's origin. Linear in the ends.</summary>
    public ValueList<ConnectionEnd> Reflect(IReadOnlyList<ConnectionEnd> ends, Axis normal)
    {
        var reflected = new List<ConnectionEnd>(ends.Count);
        for (var i = 0; i < ends.Count; i++)
            reflected.Add(ends[i] with { At = Reflect(ends[i].At, normal), Facing = grid.Reflect(ends[i].Facing, normal) });
        return ValueList.From(reflected);
    }

    /// <summary>The turn seen in the mirror: reflect, turn, reflect back. Constant cost.</summary>
    public Turn Reflect(Turn turn, Axis normal)
    {
        var rotation = new Rotation(
            grid.Reflect(grid.Apply(turn, grid.Reflect(Direction.XPlus, normal)), normal),
            grid.Reflect(grid.Apply(turn, grid.Reflect(Direction.YPlus, normal)), normal));
        for (var index = 0; index < Turn.Count; index++)
            if (Turn.Create(index).TryGet(out var candidate)
                && grid.Apply(candidate, Direction.XPlus) == rotation.XTo
                && grid.Apply(candidate, Direction.YPlus) == rotation.YTo)
                return candidate;
        throw new System.InvalidOperationException("a turn seen in a mirror is a turn");
    }

    /// <summary>
    /// The turn that lays the piece's ends on their reflection across a plane square to <paramref name="normal"/>; none
    /// when the piece has no mirror image among its turns. Quadratic in the ends, 24 times: meant for catalogue load.
    /// </summary>
    public Option<Turn> MirrorTurn(PieceFacts piece, Axis normal)
    {
        var reflected = Reflect(piece.Ends, normal);
        for (var index = 0; index < Turn.Count; index++)
            if (Turn.Create(index).TryGet(out var turn) && SameEnds(piece.Ends, turn, reflected))
                return Option<Turn>.Some(turn);
        return Option<Turn>.None;
    }

    /// <summary>Whether the piece has a mirror image among its own turns.</summary>
    public bool IsMirrorable(PieceFacts piece) => MirrorTurn(piece, Axis.X).IsSome;

    /// <summary>
    /// The turn of the mirror image of the piece placed with <paramref name="placed"/>: the piece's mirror turn, then
    /// the placed turn seen in the mirror. The mirrored cell is <see cref="IGrid.Reflect(Cell, MirrorPlane)"/>.
    /// None when the piece is not mirrorable.
    /// </summary>
    public Option<Turn> MirrorPlacement(PieceFacts piece, Turn placed, Axis normal)
    {
        var seen = Reflect(placed, normal);
        return MirrorTurn(piece, normal).Map(mirror => grid.Compose(mirror, seen));
    }

    private static CellOffset Reflect(CellOffset offset, Axis normal) => normal switch
    {
        Axis.X => offset with { X = -offset.X },
        Axis.Y => offset with { Y = -offset.Y },
        Axis.Z => offset with { Z = -offset.Z },
        _ => throw DirectionMath.NotAnAxis(normal),
    };

    private bool SameEnds(IReadOnlyList<ConnectionEnd> ends, Turn turn, ValueList<ConnectionEnd> target)
    {
        if (ends.Count != target.Count)
            return false;
        var matched = new bool[target.Count];
        for (var i = 0; i < ends.Count; i++)
        {
            var turned = ends[i] with { At = grid.Apply(turn, ends[i].At), Facing = grid.Apply(turn, ends[i].Facing) };
            if (!MatchOne(turned, target, matched))
                return false;
        }

        return true;
    }

    private static bool MatchOne(ConnectionEnd end, ValueList<ConnectionEnd> target, bool[] matched)
    {
        for (var j = 0; j < target.Count; j++)
            if (!matched[j] && target[j] == end)
                return matched[j] = true;
        return false;
    }
}
