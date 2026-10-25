using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Ports;

namespace Lattice.Core.Swaps;

/// <summary>A piece that fits in place of another, and the turn it takes there.</summary>
internal readonly record struct Fit(PrefabKey Piece, Turn Turn);

/// <summary>
/// Which pieces fit in place of a placed piece, from catalogue facts alone: a piece fits when, under some turn, it takes
/// exactly the spots the old one takes (footprint cells and slots: the same face, the same small slot, the same cells of
/// a long piece) and has exactly its ends, each leaving the same cell the same way and joining the same network kinds,
/// so every network the old piece was on stays one network of the same kind. The old turn is kept when it fits, else
/// the lowest turn that fits. A modded piece fits like any other: nothing here knows a prefab, kit or class by name.
/// Fits are kept per (old piece, old turn, candidate), as they do not depend on where the piece stands. Main thread.
/// </summary>
internal sealed class SwapShapes(ICatalogue catalogue, Shapes shapes)
{
    /// <summary>Where fits are worked out: they move with the piece, so one cell serves every structure.</summary>
    private static Cell Origin => new(0, 0, 0);

    private readonly Dictionary<(PrefabKey Old, Turn Turn, PrefabKey Candidate), Option<Turn>> _fits = new();
    private readonly Dictionary<(GridSize Grid, int Spots, int Ends), List<PieceFacts>> _buckets = new();
    private bool _bucketed;

    /// <summary>
    /// The turn the candidate takes in place of the old piece in its turn, or none when it does not fit (or either piece
    /// is not in the catalogue). Up to 24 shapes the first time a pair is asked, a lookup after.
    /// </summary>
    public Option<Turn> FitOf(PrefabKey old, Turn turn, PrefabKey candidate)
    {
        var key = (old, turn, candidate);
        if (!_fits.TryGetValue(key, out var fit))
            _fits[key] = fit = Find(new Placement(old, Origin, turn), candidate);
        return fit;
    }

    /// <summary>
    /// Every piece a kit places that fits in place of the old piece in its turn, in kit order then option order, the old
    /// piece left out; empty when the old piece is not in the catalogue. The first call reads the whole catalogue once.
    /// </summary>
    public ValueList<Fit> FitsFor(PrefabKey old, Turn turn)
    {
        if (!catalogue.Piece(old).TryGet(out var piece))
            return ValueList<Fit>.Empty;
        if (!_bucketed)
            Bucket();
        if (!_buckets.TryGetValue(BucketOf(piece), out var candidates))
            return ValueList<Fit>.Empty;

        var fits = new List<Fit>(candidates.Count);
        foreach (var candidate in candidates)
            if (candidate.Key != old && FitOf(old, turn, candidate.Key).TryGet(out var fit))
                fits.Add(new Fit(candidate.Key, fit));
        return ValueList.From(fits);
    }

    private Option<Turn> Find(Placement old, PrefabKey candidate)
    {
        if (!shapes.Of(old).TryGet(out var was) || !catalogue.Piece(candidate).TryGet(out var piece) ||
            piece.Grid != was.Piece.Grid || BucketOf(piece) != BucketOf(was.Piece))
            return Option<Turn>.None;

        if (Matches(was, old with { Piece = candidate }))
            return Option.Some(old.Turn);
        for (var index = 0; index < Turn.Count; index++)
        {
            if (Turn.Create(index).TryGet(out var turn) && turn != old.Turn && Matches(was, new Placement(candidate, old.At, turn)))
                return Option.Some(turn);
        }

        return Option<Turn>.None;
    }

    private bool Matches(PlacedShape was, Placement candidate) =>
        shapes.Of(candidate).TryGet(out var shape) && SameSpots(was, shape) && SameEnds(was, shape);

    private static bool SameSpots(PlacedShape a, PlacedShape b)
    {
        if (a.Spots.Count != b.Spots.Count)
            return false;
        foreach (var spot in a.Spots)
            if (!Has(b.Spots, spot))
                return false;
        return true;
    }

    private static bool Has(IReadOnlyList<SpotKey> spots, SpotKey spot)
    {
        for (var i = 0; i < spots.Count; i++)
            if (spots[i] == spot)
                return true;
        return false;
    }

    /// <summary>Each end of one has an end of the other leaving the same cell the same way with the same kinds; counts equal.</summary>
    private static bool SameEnds(PlacedShape a, PlacedShape b)
    {
        if (a.Ends.Count != b.Ends.Count)
            return false;
        for (var i = 0; i < a.Ends.Count; i++)
        {
            var found = false;
            for (var j = 0; j < b.Ends.Count && !found; j++)
                found = a.Ends[i].Cell == b.Ends[j].Cell && a.Ends[i].Facing == b.Ends[j].Facing &&
                        a.Piece.Ends[i].Kinds.Equals(b.Piece.Ends[j].Kinds);
            if (!found)
                return false;
        }

        return true;
    }

    private void Bucket()
    {
        _bucketed = true;
        var seen = new HashSet<PrefabKey>();
        foreach (var kit in catalogue.Kits)
        {
            if (!catalogue.Kit(kit).TryGet(out var facts))
                continue;
            foreach (var key in facts.Pieces)
            {
                if (!seen.Add(key) || !catalogue.Piece(key).TryGet(out var piece))
                    continue;
                var bucket = BucketOf(piece);
                if (!_buckets.TryGetValue(bucket, out var list))
                    _buckets[bucket] = list = new List<PieceFacts>();
                list.Add(piece);
            }
        }
    }

    /// <summary>What two pieces must share to fit: grid, spot count (an empty footprint is one spot), end count.</summary>
    private static (GridSize, int, int) BucketOf(PieceFacts piece) =>
        (piece.Grid, piece.Footprint.Count > 0 ? piece.Footprint.Count : 1, piece.Ends.Count);
}

