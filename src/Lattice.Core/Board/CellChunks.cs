using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Core.Board;

/// <summary>
/// Things filed by the chunk of space their own cell lies in, a cube 4 m on a side (two large cells), so a box asks
/// only the chunks it overlaps: linear in what those chunks hold, never in everything filed. A box larger than the
/// space filed walks only the chunks that hold something. Chunks left empty are dropped. Main thread.
/// </summary>
internal sealed class CellChunks<TId> where TId : notnull
{
    private const int Size = 40;

    private readonly Dictionary<(int X, int Y, int Z), List<(TId Id, Cell At)>> _chunks = new();

    public void Add(TId id, Cell at)
    {
        var key = Of(at);
        if (!_chunks.TryGetValue(key, out var held))
            _chunks[key] = held = new List<(TId, Cell)>(4);
        held.Add((id, at));
    }

    public void Remove(TId id, Cell at)
    {
        var key = Of(at);
        if (!_chunks.TryGetValue(key, out var held))
            return;
        for (var i = 0; i < held.Count; i++)
            if (EqualityComparer<TId>.Default.Equals(held[i].Id, id) && held[i].At == at)
            {
                held.RemoveAt(i);
                break;
            }

        if (held.Count == 0)
            _chunks.Remove(key);
    }

    public void Clear() => _chunks.Clear();

    /// <summary>Adds to <paramref name="into"/>, in no set order, everything whose own cell lies from <paramref name="min"/> to <paramref name="max"/> on every axis, both included.</summary>
    public void InBox(Cell min, Cell max, List<TId> into)
    {
        var (low, high) = (Of(min), Of(max));
        var span = (long)(high.X - low.X + 1) * (high.Y - low.Y + 1) * (high.Z - low.Z + 1);
        if (span <= _chunks.Count)
        {
            for (var x = low.X; x <= high.X; x++)
                for (var y = low.Y; y <= high.Y; y++)
                    for (var z = low.Z; z <= high.Z; z++)
                        if (_chunks.TryGetValue((x, y, z), out var held))
                            Inside(held, min, max, into);
            return;
        }

        foreach (var pair in _chunks)
        {
            var key = pair.Key;
            if (key.X >= low.X && key.X <= high.X && key.Y >= low.Y && key.Y <= high.Y && key.Z >= low.Z && key.Z <= high.Z)
                Inside(pair.Value, min, max, into);
        }
    }

    private static void Inside(List<(TId Id, Cell At)> held, Cell min, Cell max, List<TId> into)
    {
        for (var i = 0; i < held.Count; i++)
        {
            var at = held[i].At;
            if (at.X >= min.X && at.X <= max.X && at.Y >= min.Y && at.Y <= max.Y && at.Z >= min.Z && at.Z <= max.Z)
                into.Add(held[i].Id);
        }
    }

    private static (int X, int Y, int Z) Of(Cell cell) => (Floor(cell.X), Floor(cell.Y), Floor(cell.Z));

    private static int Floor(int key) => key >= 0 ? key / Size : -((-key + Size - 1) / Size);
}
