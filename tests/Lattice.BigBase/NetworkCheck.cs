using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.TestKit;
using Lattice.Core.Board;
using Lattice.Core.Catalogue;
using Lattice.Core.Grid;
using Lattice.Core.Ports;
using Lattice.TestKit.Catalogue;

namespace Lattice.BigBase;

/// <summary>
/// What a base's pieces make of their networks, read from the pieces alone: for each run family (pipe, cable, chute) how
/// many separate connected pieces its run pieces form, and how many ends of the other pieces (devices) reach no run piece
/// of their kind.
/// </summary>
internal sealed record NetworkReport(ValueMap<string, int> Components, int DeviceEnds, int UnjoinedEnds, ValueList<string> Unjoined);

internal static class NetworkCheck
{
    private const int Small = 5;

    private static readonly PieceFamily[] RunFamilies = [PieceFamilies.Pipe, PieceFamilies.Cable, PieceFamilies.Chute];

    /// <summary>
    /// Two ends join when one leaves its cell facing a side and the other leaves the cell on that side facing back, and
    /// both carry one network kind. A run family's components count only its run pieces joined to one another.
    /// </summary>
    public static NetworkReport Of(FakeCatalogue catalogue, IReadOnlyList<BasePiece> pieces)
    {
        var shapes = new Shapes(catalogue, new CubeGrid());
        var placed = new List<PlacedShape>(pieces.Count);
        var byEnd = new Dictionary<(Cell, Direction), List<(int Piece, ValueSet<NetworkType> Kinds)>>();
        foreach (var piece in pieces)
        {
            var placement = new Placement(PrefabHash.Piece(piece.Prefab), piece.At, piece.Turn);
            if (!shapes.Of(placement).TryGet(out var shape))
                throw new InvalidOperationException(piece.Prefab + " has no shape");
            placed.Add(shape);
            for (var e = 0; e < shape.Ends.Count; e++)
            {
                var key = (shape.Ends[e].Cell, shape.Ends[e].Facing);
                if (!byEnd.TryGetValue(key, out var here))
                    byEnd[key] = here = [];
                here.Add((placed.Count - 1, shape.Piece.Ends[e].Kinds));
            }
        }

        var joined = new UnionFind(placed.Count);
        var ends = 0;
        var unjoined = new List<string>();
        for (var i = 0; i < placed.Count; i++)
        {
            var isRun = IsRun(placed[i]);
            for (var e = 0; e < placed[i].Ends.Count; e++)
            {
                var end = placed[i].Ends[e];
                var kinds = placed[i].Piece.Ends[e].Kinds;
                if (!isRun && !Laid(kinds))
                    continue;
                if (!isRun)
                    ends++;
                var found = false;
                var across = (end.Cell.Plus(end.Facing.Unit().Times(Small)), end.Facing.Opposite());
                if (byEnd.TryGetValue(across, out var there))
                    foreach (var (other, otherKinds) in there)
                    {
                        if (!Shares(kinds, otherKinds))
                            continue;
                        found |= IsRun(placed[other]);
                        if (isRun && IsRun(placed[other]) && placed[other].Piece.Family == placed[i].Piece.Family)
                            joined.Union(i, other);
                    }

                if (!isRun && !found)
                    unjoined.Add($"{pieces[i].Prefab} at {pieces[i].At} facing {end.Facing}");
            }
        }

        var components = new Dictionary<string, int>(StringComparer.Ordinal);
        var roots = new HashSet<int>();
        for (var i = 0; i < placed.Count; i++)
            if (IsRun(placed[i]) && roots.Add(joined.Find(i)))
            {
                var family = placed[i].Piece.Family.Value;
                components[family] = (components.TryGetValue(family, out var count) ? count : 0) + 1;
            }

        return new NetworkReport(ValueMap.From(components), ends, unjoined.Count, ValueList.From(unjoined));
    }

    // A network's own piece; a vent is a pipe-family piece that ends the network, so its end is checked as a device's.
    private static bool IsRun(PlacedShape shape) => Array.IndexOf(RunFamilies, shape.Piece.Family) >= 0 && !shape.Piece.Vents;

    // The kinds the generator lays networks for; an end of any other kind (data, say) is left unconnected.
    private static bool Laid(ValueSet<NetworkType> kinds)
    {
        foreach (var kind in kinds)
            if (kind is NetworkType.GasPipe or NetworkType.Power or NetworkType.Chute)
                return true;
        return false;
    }

    private static bool Shares(ValueSet<NetworkType> a, ValueSet<NetworkType> b)
    {
        foreach (var kind in a)
            if (b.Contains(kind))
                return true;
        return false;
    }

    private sealed class UnionFind(int size)
    {
        private readonly int[] _parent = InitialParents(size);

        public int Find(int x)
        {
            while (_parent[x] != x)
                x = _parent[x] = _parent[_parent[x]];
            return x;
        }

        public void Union(int a, int b) => _parent[Find(a)] = Find(b);

        private static int[] InitialParents(int size)
        {
            var parents = new int[size];
            for (var i = 0; i < size; i++)
                parents[i] = i;
            return parents;
        }
    }
}
