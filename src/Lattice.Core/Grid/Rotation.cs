using Lattice.Api;
using Lattice.Core.Ports;

namespace Lattice.Core.Grid;

/// <summary>
/// A rotation of the grid axes onto themselves, given by where it sends +X and +Y; +Z goes to X cross Y, so it is
/// always a rotation and never a mirror. The two images must be square to each other.
/// </summary>
internal readonly record struct Rotation(Direction XTo, Direction YTo)
{
    /// <summary>Leaves every axis in place.</summary>
    public static Rotation Identity => new(Direction.XPlus, Direction.YPlus);

    /// <summary>Where +Z goes.</summary>
    public Direction ZTo => DirectionMath.FromUnit(XTo.Unit().Cross(YTo.Unit()));

    /// <summary>The direction after this rotation.</summary>
    public Direction Apply(Direction direction) => direction switch
    {
        Direction.XPlus => XTo,
        Direction.XMinus => XTo.Opposite(),
        Direction.YPlus => YTo,
        Direction.YMinus => YTo.Opposite(),
        Direction.ZPlus => ZTo,
        Direction.ZMinus => ZTo.Opposite(),
        _ => throw DirectionMath.NotADirection(direction),
    };

    /// <summary>The offset after this rotation.</summary>
    public CellOffset Apply(CellOffset offset) =>
        XTo.Unit().Times(offset.X).Add(YTo.Unit().Times(offset.Y)).Add(ZTo.Unit().Times(offset.Z));

    /// <summary>This rotation followed by <paramref name="next"/>.</summary>
    public Rotation Then(Rotation next) => new(next.Apply(XTo), next.Apply(YTo));
}
