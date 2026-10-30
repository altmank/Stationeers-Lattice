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
/// <remarks>
/// Pieces with a direction can still have turns that look alike: a T junction turned end for end, a cross or a
/// straight spun about its own line. <see cref="LookAlike"/> groups the 24 turns of such a piece once, on first use:
/// two turns look alike when they give the same spots and the same ends by cell, facing, kinds and role (a chute's
/// input and output stay apart). Which of the game's port slots an end is (<see cref="ConnectionEnd.GameIndex"/>,
/// <see cref="ConnectionEnd.GameName"/>) and how far its end point lies out (<see cref="ConnectionEnd.Reach"/>, measured
/// from the mesh and a little different for each end of one junction) do not count: the game joins ends by cell and
/// facing. A face piece keeps its exact turn.
/// </remarks>
internal sealed class TurnFreePieces(ICatalogue catalogue, IGrid grid, Shapes shapes)
{
    private readonly Dictionary<PrefabKey, bool> _known = new();
    private readonly Dictionary<PrefabKey, byte[]?> _alike = new();

    /// <summary>Whether a structure of the piece matches in any turn; false for a piece the catalogue does not hold.</summary>
    public bool Has(PrefabKey piece)
    {
        if (!_known.TryGetValue(piece, out var free))
            _known[piece] = free = catalogue.Piece(piece).TryGet(out var facts) && WorkOut(facts);
        return free;
    }

    /// <summary>
    /// Whether a structure of the piece standing in <paramref name="built"/> shows what a ghost of it in
    /// <paramref name="planned"/> shows: the same turn, a piece with no direction, or a turn that looks alike. False
    /// for a piece the catalogue does not hold in any other turn. Constant cost once the piece is worked out; up to 24
    /// shapes and 24 by 24 end comparisons the first time.
    /// </summary>
    public bool LookAlike(PrefabKey piece, Turn planned, Turn built)
    {
        if (planned == built || Has(piece))
            return true;
        if (!_alike.TryGetValue(piece, out var groups))
            _alike[piece] = groups = catalogue.Piece(piece).TryGet(out var facts) ? Groups(facts) : null;
        return groups is not null && groups[planned.Index] == groups[built.Index];
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

    /// <summary>
    /// A turn the game's builder can give the piece (<see cref="PieceFacts.AllowedTurns"/>) that gives it the same
    /// spots and ends as <paramref name="turn"/>, the lowest such; <paramref name="turn"/> itself when allowed; none when
    /// no allowed turn looks the same. Up to 24 shapes for a turn that is not allowed.
    /// </summary>
    public Option<Turn> AllowedTwin(PieceFacts piece, Turn turn)
    {
        if (piece.AllowedTurns.Contains(turn))
            return Option.Some(turn);
        if (!Spots(piece, turn).TryGet(out var wanted))
            return Option<Turn>.None;
        var ends = Ends(piece, turn);
        foreach (var allowed in piece.AllowedTurns.ToList())
            if (Spots(piece, allowed).TryGet(out var spots) && spots.SetEquals(wanted) && SameEnds(ends, Ends(piece, allowed)))
                return Option.Some(allowed);
        return Option<Turn>.None;
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

    /// <summary>
    /// Each turn's group: the lowest turn that looks like it (itself when none does); none for a face piece, which keeps
    /// its exact turn.
    /// </summary>
    private byte[]? Groups(PieceFacts piece)
    {
        if (piece.Face.IsSome)
            return null;
        var spots = new HashSet<SpotKey>?[Turn.Count];
        var ends = new List<ConnectionEnd>[Turn.Count];
        var groups = new byte[Turn.Count];
        for (var index = 0; index < Turn.Count; index++)
        {
            groups[index] = (byte)index;
            if (!Turn.Create(index).TryGet(out var turn))
                continue;
            spots[index] = Spots(piece, turn).TryGet(out var turned) ? turned : null;
            ends[index] = Looks(Ends(piece, turn));
            if (spots[index] is not { } mine)
                continue;
            for (var earlier = 0; earlier < index; earlier++)
                if (groups[earlier] == earlier && spots[earlier] is { } theirs && theirs.SetEquals(mine) && SameEnds(ends[earlier], ends[index]))
                {
                    groups[index] = (byte)earlier;
                    break;
                }
        }

        return groups;
    }

    /// <summary>The ends by what joins them: cell, facing, kinds and role; their game slot and measured reach left out.</summary>
    private static List<ConnectionEnd> Looks(List<ConnectionEnd> ends)
    {
        for (var i = 0; i < ends.Count; i++)
            ends[i] = ends[i] with { Reach = Option<Millimetres>.None, GameIndex = Option<int>.None, GameName = Option<string>.None };
        return ends;
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
