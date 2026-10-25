using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Ports;

namespace Lattice.Core.Support;

/// <summary>
/// The grid questions the census rules share: points in front of, behind and below a placed piece, the large cell a
/// point falls in, what takes a spot (a cell's centre, a face, a small-grid slot) by the pieces' footprints, and
/// whether a large cell is blocked. Every answer reads catalogue and support facts only, never a prefab's name or class.
/// </summary>
internal sealed class SupportGeometry(IGrid grid, ISupportFacts facts, Shapes shapes)
{
    public SupportGeometry(IGrid grid, ISupportFacts facts, ICatalogue catalogue)
        : this(grid, facts, new Shapes(catalogue, grid))
    {
    }

    public IGrid Grid => grid;

    public Option<SupportFacts> FactsOf(PrefabKey piece) => facts.Of(piece);

    /// <summary>A grid's cell size in metres (large 2 m, small 0.5 m).</summary>
    public static float Metres(GridSize size) => size == GridSize.Large ? 2f : 0.5f;

    public WorldPosition CentreOf(Placement at, PieceFacts piece) => grid.CentreOf(at.At, piece.Grid);

    /// <summary>The point moved along the direction the piece's turn gives its own axis.</summary>
    public WorldPosition Along(WorldPosition from, Turn turn, Direction local, float metres)
    {
        var (x, y, z) = Unit(grid.Apply(turn, local));
        return new WorldPosition(from.X + x * metres, from.Y + y * metres, from.Z + z * metres);
    }

    public Cell LargeCellAt(WorldPosition point) => grid.Snap(point, GridSize.Large);

    /// <summary>The spots the placement takes, from its piece's footprint; refused for a piece the catalogue does not hold.</summary>
    public Result<PlacedShape, Reason> ShapeOf(Placement placement) => shapes.Of(placement);

    /// <summary>
    /// The structures standing in the cell that take the spot (the case's own ghost left out), with their facts; none
    /// when the catalogue does not know one of them, since then the answer is the game's.
    /// </summary>
    public Option<List<(StructureInfo Info, PieceFacts Piece)>> Taking(SupportCase @case, Cell cell, SpotKey spot)
    {
        var self = SupportWorld.IdOf(@case.Ghost);
        var here = @case.World.At(cell);
        var takers = new List<(StructureInfo, PieceFacts)>(here.Count);
        foreach (var info in here)
        {
            if (info.Ref.Id == self)
                continue;
            if (!shapes.Of(new Placement(info.Ref.Piece, info.Ref.At, info.Turn)).TryGet(out var shape))
                return Option<List<(StructureInfo, PieceFacts)>>.None;
            if (Contains(shape.Spots, spot))
                takers.Add((info, shape.Piece));
        }

        return Option.Some(takers);
    }

    /// <summary>The structures holding the large cell's centre (frames and other block pieces).</summary>
    public Option<List<(StructureInfo Info, PieceFacts Piece)>> CentreHolders(SupportCase @case, Cell cell) =>
        Taking(@case, cell, new SpotKey(cell, Slot.Centre));

    /// <summary>
    /// The structures on the face between two neighbouring large cells (walls, windows, floor plates), on either side
    /// of it: each cell keys the face its own way (<see cref="Shapes"/>).
    /// </summary>
    public Option<List<(StructureInfo Info, PieceFacts Piece)>> FaceHolders(SupportCase @case, Cell rear, Direction outward)
    {
        var front = grid.Neighbour(rear, outward, GridSize.Large);
        var face = Faces.Between(rear, outward, grid);
        if (!Taking(@case, rear, face.Near).TryGet(out var behind) || !Taking(@case, front, face.Far).TryGet(out var ahead))
            return Option<List<(StructureInfo, PieceFacts)>>.None;
        foreach (var holder in ahead)
            if (!behind.Contains(holder))
                behind.Add(holder);
        return Option.Some(behind);
    }

    /// <summary>
    /// Whether the large cell is blocked (<c>GridController.IsBlockedGrid</c>: its centre holds a <c>BlockGrid</c>
    /// structure whose current step blocks gravity); none when a holder's support facts are missing. A broken
    /// structure's state has no catalogue fact, so it is taken as not blocking.
    /// </summary>
    public Option<bool> Blocked(SupportCase @case, Cell cell)
    {
        if (!CentreHolders(@case, cell).TryGet(out var holders))
            return Option<bool>.None;
        foreach (var (info, piece) in holders)
        {
            if (!facts.Of(info.Ref.Piece).TryGet(out var support))
                return Option<bool>.None;
            if (support.BlocksGrid && BlocksGravity(piece, info.State))
                return Option.Some(true);
        }

        return Option.Some(false);
    }

    /// <summary>
    /// How many large-grid face planes the small cell lies on (0 inside a cell, 1 on a face, 2 on an edge, 3 on a
    /// corner). Large faces sit at even metres.
    /// </summary>
    public static int FacePlanes(Cell cell) =>
        (cell.X % 20 == 0 ? 1 : 0) + (cell.Y % 20 == 0 ? 1 : 0) + (cell.Z % 20 == 0 ? 1 : 0);

    /// <summary>Whether a piece is a straight: two ends leaving its own cell, facing opposite ways (<c>IsStraight</c>).</summary>
    public static bool IsStraight(PieceFacts piece)
    {
        if (piece.Ends.Count != 2)
            return false;
        var a = piece.Ends[0];
        var b = piece.Ends[1];
        return a.At == default && b.At == default && Opposite(a.Facing) == b.Facing;
    }

    /// <summary>Whether two turns give a piece's forward axis the same line, either way (<c>SmallGrid.IsSameOrientation</c>).</summary>
    public bool SameAxis(Turn a, Turn b)
    {
        var first = grid.Apply(a, Direction.ZPlus);
        var second = grid.Apply(b, Direction.ZPlus);
        return first == second || Opposite(first) == second;
    }

    public static Direction Opposite(Direction direction) => direction switch
    {
        Direction.XPlus => Direction.XMinus,
        Direction.XMinus => Direction.XPlus,
        Direction.YPlus => Direction.YMinus,
        Direction.YMinus => Direction.YPlus,
        Direction.ZPlus => Direction.ZMinus,
        Direction.ZMinus => Direction.ZPlus,
        _ => direction,
    };

    private static bool BlocksGravity(PieceFacts piece, BuildState state)
    {
        var step = state switch
        {
            BuildState.Building building => building.At.Value,
            BuildState.Built => piece.Steps.Count - 1,
            _ => -1,
        };
        return step >= 0 && step < piece.Steps.Count && piece.Steps[step].BlocksGravity;
    }

    private static bool Contains(IReadOnlyList<SpotKey> spots, SpotKey spot)
    {
        for (var i = 0; i < spots.Count; i++)
            if (spots[i] == spot)
                return true;
        return false;
    }

    private static (float X, float Y, float Z) Unit(Direction direction) => direction switch
    {
        Direction.XPlus => (1f, 0f, 0f),
        Direction.XMinus => (-1f, 0f, 0f),
        Direction.YPlus => (0f, 1f, 0f),
        Direction.YMinus => (0f, -1f, 0f),
        Direction.ZPlus => (0f, 0f, 1f),
        Direction.ZMinus => (0f, 0f, -1f),
        _ => (0f, 0f, 0f),
    };
}
