using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Grid;
using Lattice.Core.Ports;

namespace Lattice.Core.Runs;

/// <summary>A piece, the turn it is placed in, and the ways it then leaves its cell.</summary>
internal readonly record struct PieceTurn(PrefabKey Piece, Turn Turn, Directions Ways);

/// <summary>
/// A kit's straight that spans <see cref="Cells"/> cells: two ends facing away from each other on one line, the
/// <see cref="Near"/> one leaving the piece's own cell. The game's long straights: the pivot cell plus N-1 cells along
/// local -z, ends +z at the pivot and -z at the far cell.
/// </summary>
internal sealed record LongStraight(PrefabKey Piece, int Cells, ConnectionEnd Near, ConnectionEnd Far);

/// <summary>
/// The run pieces of one kit, read from the catalogue's ends and nothing else, so a modded kit works the same as the
/// game's: a run piece has two or more ends and no device role. A one-cell piece (every end and footprint cell in its
/// own cell) gives the direction set it covers under each of the 24 turns; the first piece in the kit's option order,
/// then the lowest turn, answers each set. A long straight is a run piece with two opposite ends in different cells.
/// </summary>
internal sealed class KitShapes
{
    private readonly PieceTurn?[] _byShape = new PieceTurn?[64];
    private readonly List<NetworkType> _kinds = new(4);
    private readonly List<LongStraight> _longs = new(4);
    private readonly IGrid _grid;

    private KitShapes(KitKey kit, GridSize gridSize, IGrid grid)
    {
        Kit = kit;
        Grid = gridSize;
        _grid = grid;
        Step = StepOf(grid, gridSize);
    }

    public KitKey Kit { get; }

    /// <summary>The grid the kit's run pieces sit on.</summary>
    public GridSize Grid { get; }

    /// <summary>The distance between neighbouring cells of that grid, in decimetres.</summary>
    public int Step { get; }

    /// <summary>The kit's long straights, longest first.</summary>
    public IReadOnlyList<LongStraight> Longs => _longs;

    /// <summary>Reads a kit's run pieces; refused when the kit is unknown or has no one-cell straight.</summary>
    public static Result<KitShapes, Reason> Read(ICatalogue catalogue, IGrid grid, KitKey kit)
    {
        var known = catalogue.Kit(kit);
        if (!known.TryGet(out var facts))
            return Result<KitShapes, Reason>.Error(known.TryGetError(out var missing) ? missing : NoStraight(kit));

        KitShapes? shapes = null;
        foreach (var key in facts.Pieces)
        {
            if (!catalogue.Piece(key).TryGet(out var piece) || !IsRunPiece(piece))
                continue;
            shapes ??= new KitShapes(kit, piece.Grid, grid);
            if (piece.Grid == shapes.Grid)
                shapes.Add(piece);
        }

        return shapes is not null && shapes.For(Directions.None).IsSome
            ? Result<KitShapes, Reason>.Ok(shapes)
            : Result<KitShapes, Reason>.Error(NoStraight(kit));
    }

    /// <summary>
    /// The piece and turn that leave a cell exactly the given ways. No direction gives the kit's first straight
    /// unturned; one direction gives a straight along its axis (there is no one-ended piece).
    /// </summary>
    public Option<PieceTurn> For(Directions shape)
    {
        if (shape.IsEmpty)
        {
            var straight = _byShape[Directions.Along(Axis.Z).Bits] ?? _byShape[Directions.Along(Axis.X).Bits] ?? _byShape[Directions.Along(Axis.Y).Bits];
            return straight is { } found ? Option.Some(found) : Option<PieceTurn>.None;
        }

        if (shape.Count == 1)
            for (var direction = Direction.XPlus; direction <= Direction.ZMinus; direction++)
                if (shape.Contains(direction))
                    shape = shape.With(direction.Opposite());
        return _byShape[shape.Bits] is { } piece ? Option.Some(piece) : Option<PieceTurn>.None;
    }

    /// <summary>Whether this kit places the piece.</summary>
    public bool Places(PieceFacts piece)
    {
        foreach (var place in piece.Kits)
            if (place.Kit == Kit)
                return true;
        return false;
    }

    /// <summary>Whether an end joins the kit's networks: it shares a network kind with one of the kit's run pieces.</summary>
    public bool Joins(ConnectionEnd end)
    {
        foreach (var kind in end.Kinds)
            if (_kinds.Contains(kind))
                return true;
        return false;
    }

    /// <summary>
    /// The placement of a long straight that fills the cells from <paramref name="first"/> to <paramref name="last"/>
    /// (a line of its length), its ends leaving them outward; the lowest turn that does.
    /// </summary>
    public Option<Placement> Lay(LongStraight piece, Cell first, Cell last, Direction along)
    {
        for (var index = 0; index < Turn.Count; index++)
        {
            if (!Turn.Create(index).TryGet(out var turn))
                continue;
            var near = _grid.Apply(turn, piece.Near.Facing);
            var far = _grid.Apply(turn, piece.Far.Facing);
            if (near == along.Opposite() && far == along && Fits(piece, turn, first, last) is { } fromFirst)
                return Option.Some(new Placement(piece.Piece, fromFirst, turn));
            if (near == along && far == along.Opposite() && Fits(piece, turn, last, first) is { } fromLast)
                return Option.Some(new Placement(piece.Piece, fromLast, turn));
        }

        return Option<Placement>.None;
    }

    private Cell? Fits(LongStraight piece, Turn turn, Cell nearCell, Cell farCell)
    {
        var origin = nearCell.Plus(_grid.Apply(turn, piece.Near.At).Times(-1));
        return origin.Plus(_grid.Apply(turn, piece.Far.At)) == farCell ? origin : null;
    }

    private void Add(PieceFacts piece)
    {
        foreach (var end in piece.Ends)
            foreach (var kind in end.Kinds)
                if (!_kinds.Contains(kind))
                    _kinds.Add(kind);

        if (IsOneCell(piece))
            AddOneCell(piece);
        else if (LongOf(piece) is { } line)
            AddLong(line);
    }

    private void AddOneCell(PieceFacts piece)
    {
        var shape = Directions.None;
        foreach (var end in piece.Ends)
            shape = shape.With(end.Facing);
        if (shape.Count != piece.Ends.Count)
            return;
        for (var index = 0; index < Turn.Count; index++)
            if (Turn.Create(index).TryGet(out var turn))
            {
                var turned = shape.Turned(_grid, turn);
                _byShape[turned.Bits] ??= new PieceTurn(piece.Key, turn, turned);
            }
    }

    private void AddLong(LongStraight line)
    {
        var at = 0;
        while (at < _longs.Count && _longs[at].Cells >= line.Cells)
            at++;
        _longs.Insert(at, line);
    }

    private LongStraight? LongOf(PieceFacts piece)
    {
        if (piece.Ends.Count != 2)
            return null;
        var (a, b) = (piece.Ends[0], piece.Ends[1]);
        if (b.Facing != a.Facing.Opposite())
            return null;
        var (near, far) = a.At == default ? (a, b) : (b, a);
        var delta = far.At.Add(near.At.Times(-1));
        var axis = far.Facing.AxisOf();
        var length = delta.On(axis) * (DirectionMath.Along(axis, 1) == far.Facing ? 1 : -1);
        if (delta.Add(far.Facing.Unit().Times(-length)) != default || length <= 0 || length % Step != 0)
            return null;
        return new LongStraight(piece.Key, length / Step + 1, near, far);
    }

    private static bool IsRunPiece(PieceFacts piece)
    {
        if (piece.Ends.Count < 2)
            return false;
        foreach (var end in piece.Ends)
            if (end.Role.IsSome)
                return false;
        return true;
    }

    private static bool IsOneCell(PieceFacts piece)
    {
        foreach (var end in piece.Ends)
            if (end.At != default)
                return false;
        foreach (var cell in piece.Footprint)
            if (cell.At != default)
                return false;
        return true;
    }

    private static int StepOf(IGrid grid, GridSize size) => grid.Neighbour(default, Direction.XPlus, size).X - default(Cell).X;

    private static Reason NoStraight(KitKey kit) =>
        Reason.Of(ReasonCodes.NoPiece, RunCodes.Runs, ValueMap.Of("kit", kit.Hash.ToString()).With("shape", "straight"));
}
