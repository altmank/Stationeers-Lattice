using System.Collections.Generic;
using Lattice.Api;
using Lattice.TestKit;
using Lattice.TestKit.Catalogue;
using Lattice.Core.Grid;
using Lattice.Core.Ports;

namespace Lattice.BigBase;

internal enum RunKind
{
    Pipe,
    Cable,
    Chute,
}

/// <summary>One network the generator lays: its kind and the kit its pieces come from.</summary>
internal sealed class Net(RunKind kind, Kit kit)
{
    public RunKind Kind { get; } = kind;

    public int Cells { get; set; }

    public bool Fits(int mask) => kit.Fits(mask);

    public Option<(NamedPiece Piece, Turn Turn)> Match(int mask) => kit.Match(mask);
}

/// <summary>A run cell: its network and the sides its piece joins (one bit per direction).</summary>
internal sealed class RunCell(Net net, int mask)
{
    public Net Net { get; } = net;

    public int Mask { get; set; } = mask;
}

/// <summary>
/// A kit's one-cell run pieces and, for each set of sides, the first of them (in the kit's order) and the first turn
/// its piece allows that points its ends exactly to those sides; none when no piece of the kit can.
/// </summary>
internal sealed class Kit
{
    private readonly Option<(NamedPiece Piece, Turn Turn)>[] _matches = new Option<(NamedPiece, Turn)>[64];

    public Kit(string name, int weight, List<NamedPiece> pieces, IGrid grid)
    {
        Name = name;
        Weight = weight;
        for (var mask = 1; mask < 64; mask++)
            _matches[mask] = Find(pieces, mask, grid);
    }

    public string Name { get; }

    public int Weight { get; }

    public bool Fits(int mask) => mask is > 0 and < 64 && _matches[mask].IsSome;

    public Option<(NamedPiece Piece, Turn Turn)> Match(int mask) => mask is > 0 and < 64 ? _matches[mask] : Option<(NamedPiece, Turn)>.None;

    private static Option<(NamedPiece, Turn)> Find(List<NamedPiece> pieces, int mask, IGrid grid)
    {
        foreach (var piece in pieces)
        {
            if (piece.Facts.Ends.Count != Ones(mask))
                continue;
            for (var i = 0; i < Turn.Count; i++)
            {
                if (!Turn.Create(i).TryGet(out var turn) || !piece.Facts.AllowedTurns.Contains(turn))
                    continue;
                var sides = 0;
                foreach (var end in piece.Facts.Ends)
                    sides |= BaseGenerator.Bit(grid.Apply(turn, end.Facing));
                if (sides == mask)
                    return Option.Some((piece, turn));
            }
        }

        return Option<(NamedPiece, Turn)>.None;
    }

    private static int Ones(int mask)
    {
        var count = 0;
        for (var m = mask; m != 0; m &= m - 1)
            count++;
        return count;
    }
}
