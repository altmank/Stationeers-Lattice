using System.Text;
using Lattice.Api;
using Lattice.Core.Grid;
using Lattice.Core.Ports;

namespace Lattice.Core.Runs;

/// <summary>
/// A set of the six grid directions, one bit each: the ways a run piece leaves its cell. <c>default</c> is the empty
/// set. Up to a turn, the sets of two to six directions are the eight one-cell cable shapes,
/// which cover every set of two to six directions.
/// </summary>
internal readonly record struct Directions(byte Bits)
{
    public static Directions None => default;

    public static Directions Of(Direction direction) => new((byte)(1 << ((int)direction - 1)));

    public static Directions Of(Direction first, Direction second) => Of(first).With(second);

    /// <summary>Both ways along an axis.</summary>
    public static Directions Along(Axis axis) => Of(DirectionMath.Along(axis, 1), DirectionMath.Along(axis, -1));

    public bool IsEmpty => Bits == 0;

    public int Count
    {
        get
        {
            var count = 0;
            for (var bits = Bits; bits != 0; bits &= (byte)(bits - 1))
                count++;
            return count;
        }
    }

    public bool Contains(Direction direction) => (Bits & Of(direction).Bits) != 0;

    public bool Covers(Directions other) => (Bits & other.Bits) == other.Bits;

    public Directions With(Direction direction) => new((byte)(Bits | Of(direction).Bits));

    public Directions Union(Directions other) => new((byte)(Bits | other.Bits));

    public Directions Intersect(Directions other) => new((byte)(Bits & other.Bits));

    /// <summary>The axis of a straight: exactly two opposite directions.</summary>
    public Option<Axis> StraightAxis =>
        this == Along(Axis.X) ? Option.Some(Axis.X)
        : this == Along(Axis.Y) ? Option.Some(Axis.Y)
        : this == Along(Axis.Z) ? Option.Some(Axis.Z)
        : Option<Axis>.None;

    /// <summary>The set each direction of this one lands on under a turn.</summary>
    public Directions Turned(IGrid grid, Turn turn)
    {
        var turned = None;
        for (var direction = Direction.XPlus; direction <= Direction.ZMinus; direction++)
            if (Contains(direction))
                turned = turned.With(grid.Apply(turn, direction));
        return turned;
    }

    /// <summary>The directions as the catalogue writes them, for reason facts: <c>x+,z-</c>.</summary>
    public override string ToString()
    {
        var text = new StringBuilder(18);
        for (var direction = Direction.XPlus; direction <= Direction.ZMinus; direction++)
            if (Contains(direction))
                text.Append(text.Length == 0 ? "" : ",").Append(Name(direction));
        return text.ToString();
    }

    private static string Name(Direction direction) => direction switch
    {
        Direction.XPlus => "x+",
        Direction.XMinus => "x-",
        Direction.YPlus => "y+",
        Direction.YMinus => "y-",
        Direction.ZPlus => "z+",
        _ => "z-",
    };
}
