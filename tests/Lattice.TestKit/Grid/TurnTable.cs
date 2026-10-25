using System;
using Lattice.Api;
using Lattice.Core.Ports;

namespace Lattice.TestKit.Grid;

/// <summary>An integer vector on the grid axes.</summary>
internal readonly record struct Vector3i(int X, int Y, int Z)
{
    public static Vector3i Of(Direction direction) => direction switch
    {
        Direction.XPlus => new(1, 0, 0),
        Direction.XMinus => new(-1, 0, 0),
        Direction.YPlus => new(0, 1, 0),
        Direction.YMinus => new(0, -1, 0),
        Direction.ZPlus => new(0, 0, 1),
        Direction.ZMinus => new(0, 0, -1),
        _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, null),
    };

    public Vector3i Times(int scale) => new(X * scale, Y * scale, Z * scale);

    public Direction ToDirection() => this switch
    {
        (1, 0, 0) => Direction.XPlus,
        (-1, 0, 0) => Direction.XMinus,
        (0, 1, 0) => Direction.YPlus,
        (0, -1, 0) => Direction.YMinus,
        (0, 0, 1) => Direction.ZPlus,
        (0, 0, -1) => Direction.ZMinus,
        _ => throw new InvalidOperationException($"{this} is not a unit axis vector."),
    };
}

/// <summary>A rotation of the cube as an integer 3x3 matrix, rows first.</summary>
internal readonly record struct Rotation(int M00, int M01, int M02, int M10, int M11, int M12, int M20, int M21, int M22)
{
    public static Rotation Identity { get; } = new(1, 0, 0, 0, 1, 0, 0, 0, 1);

    public Vector3i Apply(Vector3i v) => new(
        M00 * v.X + M01 * v.Y + M02 * v.Z,
        M10 * v.X + M11 * v.Y + M12 * v.Z,
        M20 * v.X + M21 * v.Y + M22 * v.Z);

    /// <summary>This rotation after <paramref name="first"/>: this * first.</summary>
    public Rotation After(Rotation first) => new(
        M00 * first.M00 + M01 * first.M10 + M02 * first.M20,
        M00 * first.M01 + M01 * first.M11 + M02 * first.M21,
        M00 * first.M02 + M01 * first.M12 + M02 * first.M22,
        M10 * first.M00 + M11 * first.M10 + M12 * first.M20,
        M10 * first.M01 + M11 * first.M11 + M12 * first.M21,
        M10 * first.M02 + M11 * first.M12 + M12 * first.M22,
        M20 * first.M00 + M21 * first.M10 + M22 * first.M20,
        M20 * first.M01 + M21 * first.M11 + M22 * first.M21,
        M20 * first.M02 + M21 * first.M12 + M22 * first.M22);

    public Rotation Transposed() => new(M00, M10, M20, M01, M11, M21, M02, M12, M22);
}

/// <summary>
/// The test kit's numbering of the 24 cube turns: index = 4 * up + yaw, where the turn first yaws a quarter turn
/// <c>yaw</c> times about +Y, then carries +Y to the up direction (+Y, -Y, +X, -X, +Z, -Z). Index 0 is no turn.
/// This is the test kit's numbering, used by its fakes and its save loader; Lattice's own belongs to its IGrid.
/// </summary>
internal static class TurnTable
{
    private static readonly Rotation[] Rotations = Build();

    public static Rotation Of(Turn turn) => Rotations[turn.Index];

    public static Turn Of(Rotation rotation)
    {
        for (var i = 0; i < Rotations.Length; i++)
            if (Rotations[i] == rotation)
                return Turn.Create(i).TryGet(out var turn) ? turn : throw new InvalidOperationException();
        throw new InvalidOperationException($"{rotation} is not a cube turn.");
    }

    /// <summary>
    /// The turn a unit quaternion (Unity's x, y, z, w) stands for, or none when it is not a quarter-turn rotation
    /// (each matrix entry within 0.01 of -1, 0 or 1).
    /// </summary>
    public static Option<Turn> FromQuaternion(double x, double y, double z, double w)
    {
        var m = new[]
        {
            1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w),
            2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w),
            2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y),
        };
        var r = new int[9];
        for (var i = 0; i < 9; i++)
        {
            r[i] = (int)Math.Round(m[i]);
            if (Math.Abs(m[i] - r[i]) > 0.01)
                return Option<Turn>.None;
        }

        var rotation = new Rotation(r[0], r[1], r[2], r[3], r[4], r[5], r[6], r[7], r[8]);
        for (var i = 0; i < Rotations.Length; i++)
            if (Rotations[i] == rotation)
                return Turn.Create(i).ToOption();
        return Option<Turn>.None;
    }

    private static Rotation[] Build()
    {
        Rotation[] ups =
        [
            Rotation.Identity,
            new(1, 0, 0, 0, -1, 0, 0, 0, -1),  // half turn about X: +Y to -Y
            new(0, 1, 0, -1, 0, 0, 0, 0, 1),   // quarter about Z: +Y to +X
            new(0, -1, 0, 1, 0, 0, 0, 0, 1),   // quarter about Z: +Y to -X
            new(1, 0, 0, 0, 0, -1, 0, 1, 0),   // quarter about X: +Y to +Z
            new(1, 0, 0, 0, 0, 1, 0, -1, 0),   // quarter about X: +Y to -Z
        ];
        Rotation[] yaws =
        [
            Rotation.Identity,
            new(0, 0, 1, 0, 1, 0, -1, 0, 0),
            new(-1, 0, 0, 0, 1, 0, 0, 0, -1),
            new(0, 0, -1, 0, 1, 0, 1, 0, 0),
        ];
        var all = new Rotation[Turn.Count];
        for (var u = 0; u < ups.Length; u++)
            for (var k = 0; k < yaws.Length; k++)
                all[4 * u + k] = ups[u].After(yaws[k]);
        return all;
    }
}
