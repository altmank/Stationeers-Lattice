using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Grid;
using Lattice.Core.Ports;

namespace Lattice.Core.Routes;

/// <summary>A network as a route sees it: a real network, or a structure on none (a lone piece, or planned pieces joined to nothing real).</summary>
internal readonly record struct NetworkKey(bool IsNetwork, long Value);

/// <summary>
/// Which network each piece of the planned world belongs to. A real structure is on the network the world reports, or
/// on its own when the world reports none. A planned piece (an assumed structure, negative id) is joined to every
/// piece its ends meet (an end in its cell facing a neighbour's end facing back, sharing a kind), followed through
/// planned pieces inside the search box and stopping at real ones, whose networks it takes. Built lazily per piece
/// asked, each piece followed once; at most <see cref="MostFollowed"/> planned pieces per instance.
/// </summary>
internal sealed class RouteNetworks(IWorldQuery world, Shapes shapes, IGrid grid, GridSize size, Cell min, Cell max)
{
    /// <summary>The most planned pieces followed in one search; past it, a planned piece counts as on its own.</summary>
    public const int MostFollowed = 4096;

    private readonly Dictionary<NetworkKey, NetworkKey> _parent = [];
    private readonly HashSet<ReferenceId> _followed = [];

    /// <summary>The network the structure is on, in this view.</summary>
    public NetworkKey Of(StructureInfo structure)
    {
        if (structure.Ref.Id.Value >= 0)
            return Find(RealKey(structure.Ref.Id));
        Follow(structure);
        return Find(new NetworkKey(false, structure.Ref.Id.Value));
    }

    private NetworkKey RealKey(ReferenceId id) => world.NetworkOf(id).TryGet(out var network)
        ? new NetworkKey(true, network.Value)
        : new NetworkKey(false, id.Value);

    private void Follow(StructureInfo first)
    {
        if (_followed.Contains(first.Ref.Id))
            return;
        var queue = new Queue<StructureInfo>();
        queue.Enqueue(first);
        _followed.Add(first.Ref.Id);
        while (queue.Count > 0)
        {
            var piece = queue.Dequeue();
            var key = new NetworkKey(false, piece.Ref.Id.Value);
            if (!shapes.Of(new Placement(piece.Ref.Piece, piece.Ref.At, piece.Turn)).TryGet(out var shape))
                continue;
            for (var i = 0; i < shape.Ends.Count; i++)
            {
                var end = shape.Ends[i];
                var next = grid.Neighbour(end.Cell, end.Facing, size);
                if (!Inside(next))
                    continue;
                foreach (var other in world.At(next))
                {
                    if (!Meets(other, next, end.Facing.Opposite(), shape.Piece.Ends[i]))
                        continue;
                    if (other.Ref.Id.Value >= 0)
                        Union(key, RealKey(other.Ref.Id));
                    else
                    {
                        Union(key, new NetworkKey(false, other.Ref.Id.Value));
                        if (_followed.Count < MostFollowed && _followed.Add(other.Ref.Id))
                            queue.Enqueue(other);
                    }
                }
            }
        }
    }

    private bool Meets(StructureInfo other, Cell cell, Direction facing, ConnectionEnd mine)
    {
        if (!shapes.Of(new Placement(other.Ref.Piece, other.Ref.At, other.Turn)).TryGet(out var shape))
            return false;
        for (var j = 0; j < shape.Ends.Count; j++)
            if (shape.Ends[j].Cell == cell && shape.Ends[j].Facing == facing && mine.SharesAKindWith(shape.Piece.Ends[j]))
                return true;
        return false;
    }

    private bool Inside(Cell cell) =>
        cell.X >= min.X && cell.X <= max.X && cell.Y >= min.Y && cell.Y <= max.Y && cell.Z >= min.Z && cell.Z <= max.Z;

    private NetworkKey Find(NetworkKey key)
    {
        var root = key;
        while (_parent.TryGetValue(root, out var up) && up != root)
            root = up;
        while (_parent.TryGetValue(key, out var up) && up != root)
        {
            _parent[key] = root;
            key = up;
        }

        return root;
    }

    // A real network is always the root, so two planned pieces joined to it read as that network.
    private void Union(NetworkKey a, NetworkKey b)
    {
        var (ra, rb) = (Find(a), Find(b));
        if (ra == rb)
            return;
        if (ra.IsNetwork && !rb.IsNetwork)
            _parent[rb] = ra;
        else
            _parent[ra] = rb;
    }
}
