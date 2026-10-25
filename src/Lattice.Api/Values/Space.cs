using System;

namespace Lattice.Api;

/// <summary>A grid cell in decimetres, as the game's Grid3.</summary>
public readonly record struct Cell(int X, int Y, int Z)
{
    /// <summary>This cell moved by the offset.</summary>
    public Cell Plus(CellOffset offset) => new(X + offset.X, Y + offset.Y, Z + offset.Z);

    /// <summary>
    /// The cell a piece on the grid takes when placed at the point, as the game snaps it: each axis to the grid's cell
    /// centre (large: 2 m cells centred on odd metres; small: 0.5 m cells centred on every half metre), a tie going to
    /// the even multiple as Unity's <c>Mathf.Round</c> does, then keyed in decimetres with a half rounded away from
    /// zero, as the game's <c>Grid3</c>. Single precision, as the game computes it. Lattice's own placements snap with
    /// this. Constant cost. Added in contract 0.6.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The grid is not a <see cref="PieceGrid"/> value.</exception>
    public static Cell FromWorld(WorldPoint point, PieceGrid grid)
    {
        var (size, offset) = grid switch
        {
            PieceGrid.Large => (2f, 0f),
            PieceGrid.Small => (0.5f, 0.25f),
            _ => throw new ArgumentOutOfRangeException(nameof(grid), grid, "not a grid"),
        };
        return new Cell(Key(point.X, size, offset), Key(point.Y, size, offset), Key(point.Z, size, offset));
    }

    /// <summary>
    /// The cell across a face on the grid: one cell along the face's normal (20 decimetres on the large grid, 5 on the
    /// small). Added in contract 0.6.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The face or the grid is not one of its values.</exception>
    public Cell Neighbour(Face face, PieceGrid grid)
    {
        var step = grid switch
        {
            PieceGrid.Large => 20,
            PieceGrid.Small => 5,
            _ => throw new ArgumentOutOfRangeException(nameof(grid), grid, "not a grid"),
        };
        return face switch
        {
            Face.PlusX => this with { X = X + step },
            Face.MinusX => this with { X = X - step },
            Face.PlusY => this with { Y = Y + step },
            Face.MinusY => this with { Y = Y - step },
            Face.PlusZ => this with { Z = Z + step },
            Face.MinusZ => this with { Z = Z - step },
            _ => throw new ArgumentOutOfRangeException(nameof(face), face, "not a face"),
        };
    }

    // GridCenter(size, offset) on one axis (decompile Assets.Scripts.Util/ExtensionMethods.cs GridCenter), then Grid3's
    // key of the centre (Assets.Scripts.GridSystem/Grid3.cs Round).
    private static int Key(float metres, float size, float offset)
    {
        var shift = offset + size * 0.5f;
        var centre = (float)Math.Round((metres - shift) / size) * size + shift;
        var decimetres = centre * 10f;
        return decimetres >= 0f ? (int)(decimetres + 0.5f) : (int)(decimetres - 0.5f);
    }
}

/// <summary>A difference between two cells, in decimetres.</summary>
public readonly record struct CellOffset(int X, int Y, int Z);

/// <summary>
/// One of the 24 turns of a cube. <c>default</c> is index 0: no turn. What each index means is <see cref="Axes"/>,
/// fixed for the life of the contract: index <c>4u + k</c> is <c>k</c> quarter yaws about +Y (each taking +Z to +X),
/// then the tilt that takes +Y to the <c>u</c>-th of +Y, -Y, +X, -X, +Z, -Z.
/// </summary>
public readonly record struct Turn
{
    /// <summary>How many turns a cube has.</summary>
    public const int Count = 24;

    private Turn(byte index) => Index = index;

    /// <summary>The turn's index, 0 to 23.</summary>
    public byte Index { get; }

    /// <summary>No turn.</summary>
    public static Turn None => default;

    /// <summary>Where this turn sends +X, +Y and +Z. A rotation, never a mirror: Z is X cross Y.</summary>
    public TurnAxes Axes
    {
        get
        {
            var x = Images[Index * 2];
            var y = Images[Index * 2 + 1];
            return new TurnAxes(AxisOf(x), AxisOf(y), AxisOf(Cross(x, y)));
        }
    }

    /// <summary>Refuses an index outside 0 to 23.</summary>
    public static Result<Turn, Reason> Create(int index) =>
        index is >= 0 and < Count
            ? Result<Turn, Reason>.Ok(new Turn((byte)index))
            : Result<Turn, Reason>.Error(Reason.InvalidValue(nameof(Turn), index.ToString(), "0-to-23"));

    /// <summary>
    /// The turn that sends +X to <paramref name="x"/> and +Y to <paramref name="y"/>. Refuses a pair that is not
    /// square (the same axis twice) and a <c>default</c> or unknown <see cref="SignedAxis"/>.
    /// </summary>
    public static Result<Turn, Reason> Of(SignedAxis x, SignedAxis y)
    {
        var xCode = CodeOf(x);
        var yCode = CodeOf(y);
        if (xCode >= 0 && yCode >= 0 && xCode / 2 != yCode / 2)
            for (var index = 0; index < Count; index++)
                if (Images[index * 2] == xCode && Images[index * 2 + 1] == yCode)
                    return Result<Turn, Reason>.Ok(new Turn((byte)index));
        return Result<Turn, Reason>.Error(Reason.InvalidValue(nameof(Turn), Text(x) + "," + Text(y), "square-axes"));
    }

    /// <summary>Per index, the images of +X and +Y as direction codes: 2 * (axis - 1) + (1 when minus).</summary>
    private static ReadOnlySpan<byte> Images =>
    [
        0, 2, 5, 2, 1, 2, 4, 2,
        0, 3, 4, 3, 1, 3, 5, 3,
        3, 0, 5, 0, 2, 0, 4, 0,
        2, 1, 5, 1, 3, 1, 4, 1,
        0, 4, 2, 4, 1, 4, 3, 4,
        0, 5, 3, 5, 1, 5, 2, 5,
    ];

    private static int CodeOf(SignedAxis axis) =>
        axis.Axis is >= Axis.X and <= Axis.Z && axis.Sign is AxisSign.Plus or AxisSign.Minus
            ? 2 * ((int)axis.Axis - 1) + (axis.Sign == AxisSign.Minus ? 1 : 0)
            : -1;

    private static SignedAxis AxisOf(int code) =>
        new((Axis)(code / 2 + 1), code % 2 == 0 ? AxisSign.Plus : AxisSign.Minus);

    /// <summary>The code of X cross Y for two square direction codes.</summary>
    private static int Cross(int x, int y)
    {
        var xAxis = x / 2;
        var yAxis = y / 2;
        var cyclic = (yAxis - xAxis + 3) % 3 == 1;
        var minus = (x % 2 == 1) ^ (y % 2 == 1) ^ !cyclic;
        return 2 * (3 - xAxis - yAxis) + (minus ? 1 : 0);
    }

    private static string Text(SignedAxis axis) => CodeOf(axis) switch
    {
        < 0 => "none",
        var code => (code % 2 == 0 ? "+" : "-") + (char)('x' + code / 2),
    };
}

/// <summary>Which way along an axis. Values are fixed for the life of the contract.</summary>
public enum AxisSign
{
    /// <summary>Towards larger coordinates.</summary>
    Plus = 1,

    /// <summary>Towards smaller coordinates.</summary>
    Minus = 2,
}

/// <summary>A grid direction: an axis and which way along it. <c>default</c> is no direction.</summary>
/// <param name="Axis">The axis.</param>
/// <param name="Sign">Which way.</param>
public readonly record struct SignedAxis(Axis Axis, AxisSign Sign)
{
    /// <summary>+X.</summary>
    public static SignedAxis XPlus => new(Axis.X, AxisSign.Plus);

    /// <summary>-X.</summary>
    public static SignedAxis XMinus => new(Axis.X, AxisSign.Minus);

    /// <summary>+Y (up).</summary>
    public static SignedAxis YPlus => new(Axis.Y, AxisSign.Plus);

    /// <summary>-Y (down).</summary>
    public static SignedAxis YMinus => new(Axis.Y, AxisSign.Minus);

    /// <summary>+Z.</summary>
    public static SignedAxis ZPlus => new(Axis.Z, AxisSign.Plus);

    /// <summary>-Z.</summary>
    public static SignedAxis ZMinus => new(Axis.Z, AxisSign.Minus);
}

/// <summary>Where a turn sends the three grid axes.</summary>
/// <param name="X">The image of +X.</param>
/// <param name="Y">The image of +Y.</param>
/// <param name="Z">The image of +Z.</param>
public readonly record struct TurnAxes(SignedAxis X, SignedAxis Y, SignedAxis Z);

/// <summary>A grid axis. Values are fixed for the life of the contract.</summary>
public enum Axis
{
    /// <summary>The X axis.</summary>
    X = 1,

    /// <summary>The Y axis (up).</summary>
    Y = 2,

    /// <summary>The Z axis.</summary>
    Z = 3,
}

/// <summary>A mirror plane: square to <see cref="Normal"/>, through the coordinate <see cref="At"/> in decimetres.</summary>
public readonly record struct MirrorPlane(Axis Normal, int At);

/// <summary>Power in watts.</summary>
public readonly record struct Watts(double Value);

/// <summary>Pressure, or a pressure difference, in kilopascals.</summary>
public readonly record struct Kilopascals(double Value);

/// <summary>An amount of gas in moles.</summary>
public readonly record struct Moles(double Value);

/// <summary>A temperature in kelvin.</summary>
public readonly record struct Kelvin(double Value);
