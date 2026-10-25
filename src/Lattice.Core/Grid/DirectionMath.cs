using System;
using Lattice.Api;
using Lattice.Core.Ports;

namespace Lattice.Core.Grid;

/// <summary>Directions as unit offsets along the grid axes, and the offset arithmetic the grid needs.</summary>
internal static class DirectionMath
{
    /// <summary>The direction as a unit offset (one, not one cell).</summary>
    public static CellOffset Unit(this Direction direction) => direction switch
    {
        Direction.XPlus => new(1, 0, 0),
        Direction.XMinus => new(-1, 0, 0),
        Direction.YPlus => new(0, 1, 0),
        Direction.YMinus => new(0, -1, 0),
        Direction.ZPlus => new(0, 0, 1),
        Direction.ZMinus => new(0, 0, -1),
        _ => throw NotADirection(direction),
    };

    /// <summary>The contract's face with this outward normal.</summary>
    public static Face ToFace(this Direction direction) => direction switch
    {
        Direction.XPlus => Face.PlusX,
        Direction.XMinus => Face.MinusX,
        Direction.YPlus => Face.PlusY,
        Direction.YMinus => Face.MinusY,
        Direction.ZPlus => Face.PlusZ,
        Direction.ZMinus => Face.MinusZ,
        _ => throw NotADirection(direction),
    };

    /// <summary>The direction of a face's outward normal; none for a value outside the contract's faces.</summary>
    public static Direction? DirectionOf(Face face) => face switch
    {
        Face.PlusX => Direction.XPlus,
        Face.MinusX => Direction.XMinus,
        Face.PlusY => Direction.YPlus,
        Face.MinusY => Direction.YMinus,
        Face.PlusZ => Direction.ZPlus,
        Face.MinusZ => Direction.ZMinus,
        _ => null,
    };

    /// <summary>The direction pointing the other way.</summary>
    public static Direction Opposite(this Direction direction) => direction switch
    {
        Direction.XPlus => Direction.XMinus,
        Direction.XMinus => Direction.XPlus,
        Direction.YPlus => Direction.YMinus,
        Direction.YMinus => Direction.YPlus,
        Direction.ZPlus => Direction.ZMinus,
        Direction.ZMinus => Direction.ZPlus,
        _ => throw NotADirection(direction),
    };

    /// <summary>The axis the direction runs along.</summary>
    public static Axis AxisOf(this Direction direction) => direction switch
    {
        Direction.XPlus or Direction.XMinus => Axis.X,
        Direction.YPlus or Direction.YMinus => Axis.Y,
        Direction.ZPlus or Direction.ZMinus => Axis.Z,
        _ => throw NotADirection(direction),
    };

    /// <summary>The direction along the axis, positive for a positive sign and negative otherwise.</summary>
    public static Direction Along(Axis axis, int sign) => axis switch
    {
        Axis.X => sign > 0 ? Direction.XPlus : Direction.XMinus,
        Axis.Y => sign > 0 ? Direction.YPlus : Direction.YMinus,
        Axis.Z => sign > 0 ? Direction.ZPlus : Direction.ZMinus,
        _ => throw NotAnAxis(axis),
    };

    /// <summary>The direction of a unit offset along one axis.</summary>
    public static Direction FromUnit(CellOffset unit) => unit switch
    {
        (1, 0, 0) => Direction.XPlus,
        (-1, 0, 0) => Direction.XMinus,
        (0, 1, 0) => Direction.YPlus,
        (0, -1, 0) => Direction.YMinus,
        (0, 0, 1) => Direction.ZPlus,
        (0, 0, -1) => Direction.ZMinus,
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "not a unit offset along one axis"),
    };

    /// <summary>The offset's component along the axis.</summary>
    public static int On(this CellOffset offset, Axis axis) => axis switch
    {
        Axis.X => offset.X,
        Axis.Y => offset.Y,
        Axis.Z => offset.Z,
        _ => throw NotAnAxis(axis),
    };

    /// <summary>The offset scaled by a whole number.</summary>
    public static CellOffset Times(this CellOffset offset, int factor) =>
        new(offset.X * factor, offset.Y * factor, offset.Z * factor);

    /// <summary>The sum of two offsets.</summary>
    public static CellOffset Add(this CellOffset offset, CellOffset other) =>
        new(offset.X + other.X, offset.Y + other.Y, offset.Z + other.Z);

    /// <summary>The cross product, so that X cross Y is Z.</summary>
    public static CellOffset Cross(this CellOffset a, CellOffset b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    /// <summary>A value outside <see cref="Direction"/> is a bug in the caller.</summary>
    public static ArgumentOutOfRangeException NotADirection(Direction direction) =>
        new(nameof(direction), direction, "not a direction");

    /// <summary>A value outside <see cref="Axis"/> is a bug in the caller.</summary>
    public static ArgumentOutOfRangeException NotAnAxis(Axis axis) =>
        new(nameof(axis), axis, "not an axis");
}
