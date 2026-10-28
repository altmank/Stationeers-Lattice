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
/// snap to frame centres only. The axes are the world's, so a turn does not change them. Added in contract 0.18.
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
    /// Whether a piece placed at the cell stands on its snap on every axis. Where it does not, the game will not build
    /// it there, and Lattice fails a ghost at it with <see cref="ReasonCodes.OffSnap"/>. Constant cost. Added in contract
    /// 0.18.
    /// </summary>
    public bool Holds(Cell at) => X.Holds(at.X) && Y.Holds(at.Y) && Z.Holds(at.Z);

    /// <summary>
    /// The first axis on which the cell is off the snap, none when it stands on it on every axis. Constant cost. Added in
    /// contract 0.18.
    /// </summary>
    public Option<Axis> OffAxis(Cell at) =>
        !X.Holds(at.X) ? Option.Some(Axis.X) : !Y.Holds(at.Y) ? Option.Some(Axis.Y) : !Z.Holds(at.Z) ? Option.Some(Axis.Z) : Option<Axis>.None;
}
