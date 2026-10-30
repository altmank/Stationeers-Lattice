using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Grid;
using Lattice.Core.Ports;

namespace Lattice.Core.Selectors;

/// <summary>Something that holds cells: a ghost or a structure, its own cell, and its shape when the catalogue knows the piece.</summary>
internal readonly record struct Held<TId>(TId Id, Cell Own, Option<PlacedShape> Shape) where TId : notnull;

/// <summary>Ghosts or structures as the selectors read them, through their indices only.</summary>
internal interface IHeldPieces<TId> where TId : notnull
{
    /// <summary>Adds every holder with a spot or its own cell in the cell. Constant cost per holder found.</summary>
    void At(Cell cell, List<Held<TId>> into);
}

/// <summary>
/// Selections that follow pieces end to end, on ghosts or on structures alike. Two pieces join where an end of one
/// leaves its cell towards the next small cell and the other has an end leaving that cell back towards it, the two
/// sharing a network kind (the game's <c>SmallGrid.IsConnected</c>, as <see cref="Consequences.Connectivity"/> models it).
/// </summary>
internal static class Walks
{
    /// <summary>
    /// The run through a cell: from each run piece in the cell (a small piece with ends and no device port), along the
    /// pieces joined end to end, both ways. A piece with more than two ends (a junction) or with a device port stops the
    /// walk and is not taken; a piece the walk starts on is always taken, a junction included, and the walk does not
    /// leave a junction. Linear in the run's pieces.
    /// </summary>
    public static HashSet<TId> Run<TId>(IHeldPieces<TId> pieces, Cell cell, IGrid grid) where TId : notnull
    {
        var taken = new HashSet<TId>();
        var next = new Queue<PlacedShape>();
        var found = new List<Held<TId>>(4);
        pieces.At(cell, found);
        foreach (var start in found)
            if (start.Shape.TryGet(out var shape) && IsRunPiece(shape) && taken.Add(start.Id) && shape.Ends.Count <= 2)
                next.Enqueue(shape);

        while (next.Count > 0)
        {
            var piece = next.Dequeue();
            for (var end = 0; end < piece.Ends.Count; end++)
            {
                found.Clear();
                var there = grid.Neighbour(piece.Ends[end].Cell, piece.Ends[end].Facing, GridSize.Small);
                pieces.At(there, found);
                foreach (var held in found)
                    if (!taken.Contains(held.Id) && held.Shape.TryGet(out var other) && IsRunPiece(other) && other.Ends.Count <= 2 &&
                        Joins(piece, end, other, there, null))
                    {
                        taken.Add(held.Id);
                        next.Enqueue(other);
                    }
            }
        }

        return taken;
    }

    /// <summary>
    /// The holders joined end to end to the seed shapes, and through each other, by ends that carry one of the kinds.
    /// The seeds themselves are not taken. Linear in the seeds and what is reached.
    /// </summary>
    public static HashSet<TId> Joined<TId>(IHeldPieces<TId> pieces, IReadOnlyList<PlacedShape> seeds, ValueSet<NetworkType> kinds, IGrid grid)
        where TId : notnull
    {
        var taken = new HashSet<TId>();
        Spread(pieces, new Queue<PlacedShape>(seeds), kinds, grid, taken, runOnly: false);
        return taken;
    }

    /// <summary>
    /// The joined run pieces from a cell: each run piece in the cell, and the run pieces joined end to end to it and
    /// through each other by ends that carry one of its own ends' kinds, through junctions (any number of ends). A
    /// piece with a device port is not taken and the walk does not pass it. Linear in the starts and what each reaches.
    /// </summary>
    public static HashSet<TId> RunNetwork<TId>(IHeldPieces<TId> pieces, Cell cell, IGrid grid) where TId : notnull
    {
        var picked = new HashSet<TId>();
        var found = new List<Held<TId>>(4);
        pieces.At(cell, found);
        foreach (var start in found)
        {
            if (!start.Shape.TryGet(out var shape) || !IsRunPiece(shape))
                continue;
            var taken = new HashSet<TId> { start.Id };
            var next = new Queue<PlacedShape>();
            next.Enqueue(shape);
            Spread(pieces, next, KindsOf(shape), grid, taken, runOnly: true);
            picked.UnionWith(taken);
        }

        return picked;
    }

    /// <summary>
    /// Takes the holders joined end to end to the queued shapes, and through each other, by ends that carry one of the
    /// kinds; with <paramref name="runOnly"/>, run pieces only. A holder already taken is not taken again.
    /// </summary>
    private static void Spread<TId>(IHeldPieces<TId> pieces, Queue<PlacedShape> next, ValueSet<NetworkType> kinds, IGrid grid,
        HashSet<TId> taken, bool runOnly) where TId : notnull
    {
        var found = new List<Held<TId>>(4);
        while (next.Count > 0)
        {
            var piece = next.Dequeue();
            for (var end = 0; end < piece.Ends.Count; end++)
            {
                if (!Shares(piece.Piece.Ends[end].Kinds, kinds))
                    continue;
                found.Clear();
                var there = grid.Neighbour(piece.Ends[end].Cell, piece.Ends[end].Facing, GridSize.Small);
                pieces.At(there, found);
                foreach (var held in found)
                    if (!taken.Contains(held.Id) && held.Shape.TryGet(out var other) && (!runOnly || IsRunPiece(other)) &&
                        Joins(piece, end, other, there, kinds))
                    {
                        taken.Add(held.Id);
                        next.Enqueue(other);
                    }
            }
        }
    }

    /// <summary>Every kind any of the piece's ends carries.</summary>
    private static ValueSet<NetworkType> KindsOf(PlacedShape shape)
    {
        var kinds = new HashSet<NetworkType>();
        foreach (var end in shape.Piece.Ends)
            kinds.UnionWith(end.Kinds);
        return ValueSet.From(kinds);
    }

    /// <summary>A small piece with ends, none of them a device port (<see cref="PieceFacts.HasDevicePorts"/>).</summary>
    private static bool IsRunPiece(PlacedShape shape) => shape.IsSmall && shape.Ends.Count > 0 && !shape.Piece.HasDevicePorts;

    /// <summary>Whether <paramref name="other"/> has an end leaving <paramref name="there"/> back towards the piece's end, sharing its kinds and the filter.</summary>
    private static bool Joins(PlacedShape piece, int end, PlacedShape other, Cell there, ValueSet<NetworkType>? filter)
    {
        var back = piece.Ends[end].Facing.Opposite();
        var kinds = piece.Piece.Ends[end].Kinds;
        for (var i = 0; i < other.Ends.Count; i++)
            if (other.Ends[i].Cell == there && other.Ends[i].Facing == back && Shares(other.Piece.Ends[i].Kinds, kinds) &&
                (filter is null || Shares(other.Piece.Ends[i].Kinds, filter)))
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
}
