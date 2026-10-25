using System;
using Lattice.Api;
using Lattice.Core.Ports;

namespace Lattice.Core.Grid;

/// <summary>
/// The 24 cube turns and their products, built once per instance (constant cost per call afterwards).
/// Index <c>4u + k</c>: first <c>k</c> quarter yaws about +Y, each taking +Z to +X as Unity's
/// <c>Euler(0, 90, 0)</c> does, then the tilt that takes +Y to the <c>u</c>-th of Y+, Y-, X+, X-, Z+, Z-.
/// So 0 is no turn and 0 to 3 are the four yaws.
/// </summary>
internal sealed class TurnTable
{
    private readonly Turn[] _turns = new Turn[Turn.Count];
    private readonly Rotation[] _rotations = new Rotation[Turn.Count];
    private readonly byte[] _compose = new byte[Turn.Count * Turn.Count];
    private readonly byte[] _inverse = new byte[Turn.Count];

    /// <summary>Builds the turns, the product table and the inverses.</summary>
    public TurnTable()
    {
        var yaw = new Rotation(Direction.ZMinus, Direction.YPlus);
        Rotation[] tilts =
        [
            Rotation.Identity,
            new(Direction.XPlus, Direction.YMinus),
            new(Direction.YMinus, Direction.XPlus),
            new(Direction.YPlus, Direction.XMinus),
            new(Direction.XPlus, Direction.ZPlus),
            new(Direction.XPlus, Direction.ZMinus),
        ];

        for (var index = 0; index < Turn.Count; index++)
        {
            var rotation = Rotation.Identity;
            for (var k = 0; k < index % 4; k++)
                rotation = rotation.Then(yaw);
            _rotations[index] = rotation.Then(tilts[index / 4]);
            _turns[index] = Turn.Create(index).TryGet(out var turn) ? turn : throw new InvalidOperationException($"turn {index}");
        }

        for (var first = 0; first < Turn.Count; first++)
            for (var then = 0; then < Turn.Count; then++)
            {
                var product = IndexOf(_rotations[first].Then(_rotations[then]));
                _compose[first * Turn.Count + then] = product;
                if (product == 0)
                    _inverse[first] = (byte)then;
            }
    }

    /// <summary>The rotation a turn makes.</summary>
    public Rotation RotationOf(Turn turn) => _rotations[turn.Index];

    /// <summary>The turn that makes the rotation.</summary>
    public Turn TurnOf(Rotation rotation) => _turns[IndexOf(rotation)];

    /// <summary><paramref name="first"/>, then <paramref name="then"/>.</summary>
    public Turn Compose(Turn first, Turn then) => _turns[_compose[first.Index * Turn.Count + then.Index]];

    /// <summary>The turn that undoes this one.</summary>
    public Turn Invert(Turn turn) => _turns[_inverse[turn.Index]];

    private byte IndexOf(Rotation rotation)
    {
        for (var index = 0; index < Turn.Count; index++)
            if (_rotations[index] == rotation)
                return (byte)index;
        throw new ArgumentOutOfRangeException(nameof(rotation), rotation, "not one of the 24 turns");
    }
}
