using System;
using UnityEngine;

namespace Lattice.Api;

/// <summary>Turns as Unity rotations. Added in contract 0.19.</summary>
public static class TurnRotations
{
    /// <summary>
    /// The rotation the game gives a piece placed with the turn: the one that sends Unity's right, up and forward
    /// (+X, +Y, +Z) to <see cref="Turn.Axes"/>. It is one of the 24 the game's quarter turns about the world axes make,
    /// and the rotation Lattice places the piece with. Unit length; constant cost; allocates nothing. Added in contract
    /// 0.19.
    /// </summary>
    public static Quaternion ToRotation(this Turn turn)
    {
        var axes = turn.Axes;
        var x = Unit(axes.X);
        var y = Unit(axes.Y);
        var z = Unit(axes.Z);
        float m00 = x.x, m10 = x.y, m20 = x.z;
        float m01 = y.x, m11 = y.y, m21 = y.z;
        float m02 = z.x, m12 = z.y, m22 = z.z;
        var trace = m00 + m11 + m22;
        if (trace > 0f)
        {
            var s = (float)Math.Sqrt(trace + 1f) * 2f;
            return new Quaternion((m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, s / 4f);
        }

        if (m00 > m11 && m00 > m22)
        {
            var s = (float)Math.Sqrt(1f + m00 - m11 - m22) * 2f;
            return new Quaternion(s / 4f, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s);
        }

        if (m11 > m22)
        {
            var s = (float)Math.Sqrt(1f + m11 - m00 - m22) * 2f;
            return new Quaternion((m01 + m10) / s, s / 4f, (m12 + m21) / s, (m02 - m20) / s);
        }

        var t = (float)Math.Sqrt(1f + m22 - m00 - m11) * 2f;
        return new Quaternion((m02 + m20) / t, (m12 + m21) / t, t / 4f, (m10 - m01) / t);
    }

    private static Vector3 Unit(SignedAxis axis)
    {
        var sign = axis.Sign == AxisSign.Minus ? -1f : 1f;
        return axis.Axis switch
        {
            Axis.X => new Vector3(sign, 0f, 0f),
            Axis.Y => new Vector3(0f, sign, 0f),
            _ => new Vector3(0f, 0f, sign),
        };
    }
}
