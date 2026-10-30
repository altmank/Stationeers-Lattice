using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Slices;

namespace Lattice.Core.Catalogue;

/// <summary>
/// The structure prefabs' data read so far, kept for the life of the process: the game's prefabs are loaded once and do
/// not change between worlds, so they are read a slice a frame before the first world starts (<see cref="ReadSome"/>)
/// and every world's catalogue reads them from here (<see cref="Over"/>). Prefab data holds no localised text; kit and
/// item names stay with each world's source. Main thread.
/// </summary>
internal sealed class PrefabReads
{
    private readonly Dictionary<PrefabKey, Option<PrefabData>> _read = new();
    private int _next;

    /// <summary>How many prefabs are read.</summary>
    public int Count => _read.Count;

    /// <summary>
    /// Reads the source's structures not read yet, in order, until <paramref name="budget"/> has passed on
    /// <paramref name="clock"/> (at least one); true once all are.
    /// </summary>
    public bool ReadSome(IPrefabSource source, IClock clock, TimeSpan budget)
    {
        var structures = source.Structures;
        var until = clock.Now + budget;
        while (_next < structures.Count)
        {
            Read(source, structures[_next++]);
            if (clock.Now >= until)
                break;
        }

        return _next >= structures.Count;
    }

    /// <summary>A source that reads its prefabs from here, reading (and keeping) only what is not here yet.</summary>
    public IPrefabSource Over(IPrefabSource source) => new Kept(source, this);

    private Option<PrefabData> Read(IPrefabSource source, PrefabKey piece)
    {
        if (_read.TryGetValue(piece, out var data))
            return data;
        data = source.Read(piece);
        _read.Add(piece, data);
        return data;
    }

    private sealed class Kept(IPrefabSource source, PrefabReads reads) : IPrefabSource
    {
        public IReadOnlyList<KitData> Kits => source.Kits;

        public IReadOnlyList<PrefabKey> Structures => source.Structures;

        public IReadOnlyList<ListedPiece> Listed => source.Listed;

        public IReadOnlyList<ItemRef> BrokenTools => source.BrokenTools;

        public IReadOnlyList<SprayCanData> SprayCans => source.SprayCans;

        public IReadOnlyList<ItemRef> Labellers => source.Labellers;

        public IReadOnlyList<ListedItem> Items => source.Items;

        public Option<PrefabData> Read(PrefabKey piece) => reads.Read(source, piece);
    }
}
