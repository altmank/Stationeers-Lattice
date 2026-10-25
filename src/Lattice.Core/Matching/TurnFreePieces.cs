using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Ports;

namespace Lattice.Core.Matching;

/// <summary>
/// Which pieces have no direction, so a structure of one matches its ghost in any turn: frames and their kin.
/// Worked out from catalogue facts alone, with no list of pieces: a piece has no direction when every one of the 24
/// turns gives it the same spots and the same ends (cell, facing, kinds and role). A piece the game places on a face
/// always has one: its face. A modded piece is judged the same way. Each piece is worked out once, on first use;
/// quadratic in its ends, 24 times.
/// </summary>
internal sealed class TurnFreePieces(ICatalogue catalogue, IGrid grid, Shapes shapes)
{
    private readonly Dictionary<PrefabKey, bool> _known = new();

    /// <summary>Whether a structure of the piece matches in any turn; false for a piece the catalogue does not hold.</summary>
    public bool Has(PrefabKey piece)
    {
        if (!_known.TryGetValue(piece, out var free))
            _known[piece] = free = catalogue.Piece(piece).TryGet(out var facts) && WorkOut(facts);
        return free;
    }

    /// <summary>
    /// The pieces with no direction among every kit's pieces and any other piece asked about so far, by hash. Linear in
    /// the catalogue's kit pieces: meant for the load report.
    /// </summary>
    public ValueList<PrefabKey> Report()
    {
        foreach (var kit in catalogue.Kits)
            if (catalogue.Kit(kit).TryGet(out var facts))
                foreach (var piece in facts.Pieces)
                    Has(piece);

        var free = new List<PrefabKey>(_known.Count);
        foreach (var pair in _known)
            if (pair.Value)
                free.Add(pair.Key);
        free.Sort(static (a, b) => a.Hash.CompareTo(b.Hash));
        return ValueList.From(free);
    }

    private bool WorkOut(PieceFacts piece)
    {
        if (piece.Face.IsSome || !Spots(piece, Turn.None).TryGet(out var unturned))
            return false;
        var ends = Ends(piece, Turn.None);
        for (var index = 1; index < Turn.Count; index++)
        {
            if (!Turn.Create(index).TryGet(out var turn) || !Spots(piece, turn).TryGet(out var turned))
                return false;
            if (!turned.SetEquals(unturned) || !SameEnds(ends, Ends(piece, turn)))
                return false;
        }

        return true;
    }

    private Option<HashSet<SpotKey>> Spots(PieceFacts piece, Turn turn) =>
        shapes.Of(new Placement(piece.Key, default, turn))
            .Match(static shape => Option.Some(new HashSet<SpotKey>(shape.Spots)), static _ => Option<HashSet<SpotKey>>.None);

    private List<ConnectionEnd> Ends(PieceFacts piece, Turn turn)
    {
        var ends = new List<ConnectionEnd>(piece.Ends.Count);
        foreach (var end in piece.Ends)
            ends.Add(end with { At = grid.Apply(turn, end.At), Facing = grid.Apply(turn, end.Facing) });
        return ends;
    }

    private static bool SameEnds(List<ConnectionEnd> mine, List<ConnectionEnd> turned)
    {
        if (mine.Count != turned.Count)
            return false;
        var used = new bool[turned.Count];
        foreach (var end in mine)
        {
            var found = false;
            for (var j = 0; j < turned.Count && !found; j++)
                if (!used[j] && turned[j] == end)
                    used[j] = found = true;
            if (!found)
                return false;
        }

        return true;
    }
}
