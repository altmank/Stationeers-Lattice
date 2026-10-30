using System;

namespace Lattice.Api;

/// <summary>
/// How the game snaps a piece's position along one world axis: to the nearest point <see cref="Offset"/> plus half a
/// <see cref="Size"/> from a multiple of <see cref="Size"/>, in metres, as the game's <c>GridCenter</c> does (a tie
/// going to the even multiple, as Unity's <c>Mathf.Round</c>). The large grid is size 2 offset 0 (odd metres), the
/// small grid size 0.5 offset 0.25 (every half metre). A size of zero or less snaps nothing: every point holds.
/// Added in contract 0.18.
/// </summary>
/// <param name="Size">The step between snap points, in metres.</param>
/// <param name="Offset">The shift of the snap points, in metres, before the half step.</param>
public readonly record struct AxisSnap(float Size, float Offset)
{
    /// <summary>
    /// The snap point nearest the coordinate, keyed in decimetres with a half rounded away from zero, as the game's
    /// <c>Grid3</c>. Single precision, as the game computes it. Constant cost. Added in contract 0.18.
    /// </summary>
    public int Key(float metres)
    {
        var centre = Size > 0f ? Snapped(metres) : metres;
        var decimetres = centre * 10f;
        return decimetres >= 0f ? (int)(decimetres + 0.5f) : (int)(decimetres - 0.5f);
    }

    /// <summary>
    /// Whether a cell coordinate, in decimetres, is a snap point: the game leaves a piece there where it stands.
    /// Constant cost. Added in contract 0.18.
    /// </summary>
    public bool Holds(int decimetres) => Size <= 0f || Key(decimetres / 10f) == decimetres;

    /// <summary>
    /// Whether a cell coordinate, in decimetres, is on a grid line: half a <see cref="Size"/> from a snap point, where a
    /// piece placed on a face stands on the axis square to its face. Constant cost. Added in 0.22.2.
    /// </summary>
    public bool OnLine(int decimetres) => Size <= 0f || (this with { Offset = Offset - Size * 0.5f }).Holds(decimetres);

    private float Snapped(float metres)
    {
        var shift = Offset + Size * 0.5f;
        return (float)Math.Round((metres - shift) / Size) * Size + shift;
    }
}

/// <summary>
/// How the game snaps a piece's position on each world axis (the game's <c>Structure.GetWorldGrid</c>): the grid's
/// own size and offset for most pieces (<see cref="Of"/>), each axis its own for a piece that sets
/// <c>_useAxisIndependentGridSnapping</c>, such as the gas and liquid rocket engines and umbilicals, whose x and z
/// snap to frame centres only. The axes are the world's, so a turn does not change them. A piece the game places on a
/// face (<see cref="OnFaces"/>) stands half a step off the snap on the axis square to its face. Added in contract 0.18.
/// </summary>
/// <param name="X">The snap along x.</param>
/// <param name="Y">The snap along y.</param>
/// <param name="Z">The snap along z.</param>
public readonly record struct PieceSnap(AxisSnap X, AxisSnap Y, AxisSnap Z)
{
    /// <summary>
    /// The grid's own snap on every axis: size 2 offset 0 on the large grid, size 0.5 offset 0.25 on the small, as
    /// <see cref="Cell.FromWorld"/> snaps. Added in contract 0.18.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The grid is not a <see cref="PieceGrid"/> value.</exception>
    public static PieceSnap Of(PieceGrid grid)
    {
        var axis = grid switch
        {
            PieceGrid.Large => new AxisSnap(2f, 0f),
            PieceGrid.Small => new AxisSnap(0.5f, 0.25f),
            _ => throw new ArgumentOutOfRangeException(nameof(grid), grid, "not a grid"),
        };
        return new PieceSnap(axis, axis, axis);
    }

    /// <summary>
    /// Whether the piece is one the game places on a face (<see cref="PiecePlacing.Face"/>: walls, windows, floors).
    /// The game's face cursor puts it at the snap point it aims into, moved back half a step along the piece's forward
    /// (decompile <c>Assets.Scripts.Inventory/InventoryManager.cs</c> <c>UpdatePlacement</c>, <c>PlacementSnap.Face</c>),
    /// so it stands on a grid line on one axis (<see cref="AxisSnap.OnLine"/>: even metres on the large grid) and on
    /// the snap on the other two; never at a snap point on all three. False for every other piece, and for a snap
    /// built without it. Added in 0.22.2.
    /// </summary>
    public bool OnFaces { get; init; }

    /// <summary>
    /// Whether a piece placed at the cell stands where the game snaps it: on its snap on every axis, or for a piece
    /// placed on a face (<see cref="OnFaces"/>) on a grid line on exactly one axis and on its snap on the other two.
    /// Where it does not, the game will not build it there, and Lattice fails a ghost at it with
    /// <see cref="ReasonCodes.OffSnap"/>. Constant cost. Added in contract 0.18; for face pieces, in 0.22.2.
    /// </summary>
    public bool Holds(Cell at) => OffAxis(at).IsNone;

    /// <summary>
    /// The first axis on which the cell is off where the game snaps the piece, none where <see cref="Holds"/>. For a
    /// piece placed on a face: the first axis on neither its snap nor a grid line; else, with no axis on a grid line,
    /// the first axis; with more than one, the second on a grid line. Constant cost. Added in contract 0.18; for face
    /// pieces, in 0.22.2.
    /// </summary>
    public Option<Axis> OffAxis(Cell at)
    {
        if (!OnFaces)
            return !X.Holds(at.X) ? Option.Some(Axis.X) : !Y.Holds(at.Y) ? Option.Some(Axis.Y) : !Z.Holds(at.Z) ? Option.Some(Axis.Z) : Option<Axis>.None;
        var lines = 0;
        var free = false;
        var second = Axis.X;
        for (var i = 0; i < 3; i++)
        {
            var (axis, snap, key) = i switch { 0 => (Axis.X, X, at.X), 1 => (Axis.Y, Y, at.Y), _ => (Axis.Z, Z, at.Z) };
            if (snap.Size <= 0f)
            {
                free = true;
                continue;
            }

            if (snap.Holds(key))
                continue;
            if (!snap.OnLine(key))
                return Option.Some(axis);
            if (++lines == 2)
                second = axis;
        }

        return lines switch
        {
            0 => free ? Option<Axis>.None : Option.Some(Axis.X),
            1 => Option<Axis>.None,
            _ => Option.Some(second),
        };
    }
}
