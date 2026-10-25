using System;
using Lattice.Api;
using Lattice.Core.Grid;
using Lattice.Core.Ports;

namespace Lattice.Core.World;

/// <summary>A rotation as Unity writes it: a unit quaternion (x, y, z, w).</summary>
internal readonly record struct GameRotation(float X, float Y, float Z, float W);

/// <summary>Turns to and from the game's rotations, in Lattice's numbering (<see cref="TurnTable"/>).</summary>
internal static class TurnReading
{
    private const double Tolerance = 0.01;

    private static readonly TurnTable Turns = new();

    /// <summary>The turn a rotation makes, or none when it is not a quarter-turn rotation (within 0.01).</summary>
    public static Option<Turn> FromGame(GameRotation q)
    {
        double x = q.X, y = q.Y, z = q.Z, w = q.W;
        var xTo = Snap(1 - 2 * (y * y + z * z), 2 * (x * y + z * w), 2 * (x * z - y * w));
        var yTo = Snap(2 * (x * y - z * w), 1 - 2 * (x * x + z * z), 2 * (y * z + x * w));
        if (!xTo.TryGet(out var xDirection) || !yTo.TryGet(out var yDirection) || xDirection.AxisOf() == yDirection.AxisOf())
            return Option<Turn>.None;
        return Option.Some(Turns.TurnOf(new Rotation(xDirection, yDirection)));
    }

    /// <summary>The game rotation that makes a turn.</summary>
    public static GameRotation ToGame(Turn turn)
    {
        var rotation = Turns.RotationOf(turn);
        var xTo = rotation.XTo.Unit();
        var yTo = rotation.YTo.Unit();
        var zTo = rotation.ZTo.Unit();
        double m00 = xTo.X, m10 = xTo.Y, m20 = xTo.Z;
        double m01 = yTo.X, m11 = yTo.Y, m21 = yTo.Z;
        double m02 = zTo.X, m12 = zTo.Y, m22 = zTo.Z;
        var trace = m00 + m11 + m22;
        if (trace > 0)
        {
            var s = Math.Sqrt(trace + 1) * 2;
            return Of((m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, s / 4);
        }

        if (m00 > m11 && m00 > m22)
        {
            var s = Math.Sqrt(1 + m00 - m11 - m22) * 2;
            return Of(s / 4, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s);
        }

        if (m11 > m22)
        {
            var s = Math.Sqrt(1 + m11 - m00 - m22) * 2;
            return Of((m01 + m10) / s, s / 4, (m12 + m21) / s, (m02 - m20) / s);
        }

        var t = Math.Sqrt(1 + m22 - m00 - m11) * 2;
        return Of((m02 + m20) / t, (m12 + m21) / t, t / 4, (m10 - m01) / t);
    }

    private static GameRotation Of(double x, double y, double z, double w) => new((float)x, (float)y, (float)z, (float)w);

    private static Option<Direction> Snap(double x, double y, double z)
    {
        var rx = (int)Math.Round(x);
        var ry = (int)Math.Round(y);
        var rz = (int)Math.Round(z);
        if (Math.Abs(x - rx) > Tolerance || Math.Abs(y - ry) > Tolerance || Math.Abs(z - rz) > Tolerance)
            return Option<Direction>.None;
        if (Math.Abs(rx) + Math.Abs(ry) + Math.Abs(rz) != 1)
            return Option<Direction>.None;
        return Option.Some(DirectionMath.FromUnit(new CellOffset(rx, ry, rz)));
    }
}
