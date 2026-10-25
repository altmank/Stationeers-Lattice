using System;
using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Core.Styles;

/// <summary>A 32 m cube of the world: what the renderer frustum-tests and labels are chosen from.</summary>
internal readonly record struct ChunkKey(int X, int Y, int Z)
{
    public const float Metres = 32f;

    /// <summary>The chunk holding a cell's centre (cell keys are decimetres, <c>GridController.LocalToWorld</c>).</summary>
    public static ChunkKey Of(Cell cell) => new(Floor(cell.X), Floor(cell.Y), Floor(cell.Z));

    public static ChunkKey Of(float x, float y, float z) => new((int)MathF.Floor(x / Metres), (int)MathF.Floor(y / Metres), (int)MathF.Floor(z / Metres));

    private static int Floor(int decimetres) => (int)MathF.Floor(decimetres * 0.1f / Metres);
}

/// <summary>
/// What a batch draws with one draw call per page: one piece, one part kind (a placement draws from the ghost copies,
/// an existing part from the overlay copies, so the two never share a call even when their looks are equal), one
/// signature (so one look), one chunk, and one start (things whose look began to ease or play in the same frame, so an
/// animation is one property-block update per batch). Keyed by signature, finer than the resolved look, so enabling or
/// disabling a layer re-resolves batches' looks and moves no instance.
/// </summary>
internal readonly record struct BatchKey(PrefabKey Piece, PartKind Part, Signature Signature, ChunkKey Chunk, long Start);

/// <summary>One instance: where the piece is drawn and how it is turned, and the thing it draws.</summary>
internal readonly record struct DrawInstance(Cell At, Turn Turn, Thing Thing);

/// <summary>
/// A batch: instances in preallocated pages of 1023 (the <c>DrawMeshInstanced</c> limit the game also uses,
/// <c>RenderBatch.cs:11</c>). Adding and removing are constant cost (removal moves the last instance into the gap);
/// <see cref="Version"/> rises with each edit so the renderer rebuilds its matrices only for edited batches.
/// </summary>
internal sealed class Batch
{
    public const int PageSize = 1023;

    private readonly List<DrawInstance[]> _pages = new(1);
    private readonly Func<Signature, ResolvedLook> _look;

    public Batch(BatchKey key, Func<Signature, ResolvedLook> look)
    {
        Key = key;
        _look = look;
        _pages.Add(new DrawInstance[PageSize]);
    }

    public BatchKey Key { get; private set; }

    /// <summary>Its place in the draw plan's list.</summary>
    public int Slot { get; set; }

    /// <summary>Takes a new key for an empty batch reused from the pool.</summary>
    public void Rekey(BatchKey key)
    {
        Key = key;
        Version++;
    }

    public PrefabKey Piece => Key.Piece;

    /// <summary>Which part of their ghosts its things are: placements or existing parts.</summary>
    public PartKind Part => Key.Part;

    public ChunkKey Chunk => Key.Chunk;

    /// <summary>The draw plan frame its look began to ease or play; 0 for a look that does not move.</summary>
    public long Start => Key.Start;

    /// <summary>Its look under the layers and palette in force; resolved once per signature after a change.</summary>
    public ResolvedLook Look => _look(Key.Signature);

    public int Count { get; private set; }

    public int Pages => (Count + PageSize - 1) / PageSize;

    public long Version { get; private set; }

    public DrawInstance this[int index] => _pages[index / PageSize][index % PageSize];

    /// <summary>A page's instances, to hand to one draw call.</summary>
    public ReadOnlySpan<DrawInstance> Page(int page)
    {
        var start = page * PageSize;
        return new ReadOnlySpan<DrawInstance>(_pages[page], 0, Math.Min(PageSize, Count - start));
    }

    public void Add(Thing thing)
    {
        if (Count == _pages.Count * PageSize)
            _pages.Add(new DrawInstance[PageSize]);
        thing.Batch = this;
        thing.BatchSlot = Count;
        Set(Count++, new DrawInstance(thing.At, thing.Turn, thing));
        Version++;
    }

    public void Remove(Thing thing)
    {
        var slot = thing.BatchSlot;
        var last = --Count;
        if (slot != last)
        {
            var moved = this[last];
            Set(slot, moved);
            moved.Thing.BatchSlot = slot;
        }

        Set(last, default);
        thing.Batch = null;
        Version++;
    }

    private void Set(int index, DrawInstance instance) => _pages[index / PageSize][index % PageSize] = instance;
}

/// <summary>
/// The read surface the renderer draws from: batches, labels, the clip in force and the frame count. A view over
/// preallocated arrays: reading it allocates nothing.
/// </summary>
internal interface IDrawPlan
{
    /// <summary>Counts up once per styles frame.</summary>
    long Frame { get; }

    int BatchCount { get; }

    Batch BatchAt(int index);

    Option<ClipShape> Clip { get; }

    int LabelCount { get; }

    ShownLabel LabelAt(int index);
}

/// <summary>
/// The batches every drawn thing sits in, by key. A batch emptied stays for reuse until its signature's generation ends,
/// then goes back to a pool; an emptied batch of one start (an animation's group) goes back at once, so groups that come
/// and go every hover allocate nothing once warm, and a new generation reuses the batches of the one before.
/// </summary>
internal sealed class DrawPlan(Func<Signature, ResolvedLook> look)
{
    private readonly Dictionary<BatchKey, Batch> _byKey = new();
    private readonly List<Batch> _batches = new(256);
    private readonly List<Batch> _pool = new(16);
    private Batch? _last;

    public IReadOnlyList<Batch> Batches => _batches;

    public long Frame { get; set; } = 1;

    /// <summary>Puts a thing in the batch its piece, part, signature, chunk and start say. Constant cost.</summary>
    public void Add(Thing thing)
    {
        var key = new BatchKey(thing.Piece, thing.Part, thing.Signature, ChunkKey.Of(thing.At), thing.StartFrame);
        if (_last is { } last && last.Key.Signature == key.Signature && last.Key.Equals(key))
        {
            last.Add(thing);
            return;
        }

        if (!_byKey.TryGetValue(key, out var batch))
        {
            if (_pool.Count > 0)
            {
                batch = _pool[_pool.Count - 1];
                _pool.RemoveAt(_pool.Count - 1);
                batch.Rekey(key);
            }
            else
                batch = new Batch(key, look);

            _byKey[key] = batch;
            batch.Slot = _batches.Count;
            _batches.Add(batch);
        }

        batch.Add(thing);
        _last = batch;
    }

    public void Remove(Thing thing)
    {
        if (thing.Batch is not { } batch)
            return;
        batch.Remove(thing);
        if (batch.Count == 0 && batch.Start != 0)
        {
            Unlist(batch);
            _pool.Add(batch);
        }
    }

    private void Unlist(Batch batch)
    {
        if (ReferenceEquals(_last, batch))
            _last = null;
        _byKey.Remove(batch.Key);
        var last = _batches.Count - 1;
        var moved = _batches[last];
        _batches[batch.Slot] = moved;
        moved.Slot = batch.Slot;
        _batches.RemoveAt(last);
    }

    /// <summary>Drops empty batches of a generation that ended into the pool, for the next generation's batches.</summary>
    public void DropEmpty(Generation ended)
    {
        for (var i = _batches.Count - 1; i >= 0; i--)
        {
            var batch = _batches[i];
            if (batch.Count == 0 && ReferenceEquals(batch.Key.Signature.Generation, ended))
            {
                Unlist(batch);
                _pool.Add(batch);
            }
        }
    }

    public void Clear()
    {
        foreach (var batch in _batches)
            for (var i = batch.Count - 1; i >= 0; i--)
                batch.Remove(batch[i].Thing);
        _byKey.Clear();
        _batches.Clear();
        _pool.Clear();
        _last = null;
    }
}
