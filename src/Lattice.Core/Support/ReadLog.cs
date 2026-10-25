using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Core.Support;

/// <summary>What a judgement read. Values are internal and may change.</summary>
internal enum ReadKind : byte
{
    /// <summary>What stands in a cell.</summary>
    Cell = 1,

    /// <summary>A structure by id (an assumed one has the negated ghost id).</summary>
    Structure = 2,

    /// <summary>Assumed changes in a fine bucket (one large cell: <see cref="ReadKey.FineSize"/> decimetres a side).</summary>
    FineBucket = 3,

    /// <summary>Assumed changes in a coarse bucket (<see cref="ReadKey.CoarseSize"/> decimetres a side).</summary>
    CoarseBucket = 4,

    /// <summary>The room a cell is in, a main-thread fact.</summary>
    Room = 5,

    /// <summary>The network a structure is on, a main-thread fact.</summary>
    Network = 6,

    /// <summary>The game's placement check for a placement, a main-thread fact.</summary>
    Game = 7,

    /// <summary>A main-thread rule's answer for one ghost.</summary>
    MainRule = 8,
}

/// <summary>
/// One thing a judgement read, so a change to it finds the judgements to redo. Constructed through the factory
/// members only; the fields a kind does not use are default.
/// </summary>
internal readonly record struct ReadKey(ReadKind Kind, Cell Cell, long Id, Placement? Placement, RuleId Rule)
{
    /// <summary>A fine bucket's side in decimetres: one large cell.</summary>
    public const int FineSize = 20;

    /// <summary>A coarse bucket's side in decimetres: eight large cells, the reach of the outside rule.</summary>
    public const int CoarseSize = 160;

    public static ReadKey At(Cell cell) => new(ReadKind.Cell, cell, 0, null, default);

    public static ReadKey StructureOf(ReferenceId id) => new(ReadKind.Structure, default, id.Value, null, default);

    public static ReadKey Fine(Cell bucket) => new(ReadKind.FineBucket, bucket, 0, null, default);

    public static ReadKey Coarse(Cell bucket) => new(ReadKind.CoarseBucket, bucket, 0, null, default);

    public static ReadKey RoomAt(Cell cell) => new(ReadKind.Room, cell, 0, null, default);

    public static ReadKey NetworkOf(ReferenceId id) => new(ReadKind.Network, default, id.Value, null, default);

    public static ReadKey Game(Placement placement) => new(ReadKind.Game, default, 0, placement, default);

    public static ReadKey MainRule(RuleId rule, GhostId ghost) => new(ReadKind.MainRule, default, ghost.Value, null, rule);

    /// <summary>The fine bucket a cell lies in.</summary>
    public static Cell FineBucketOf(Cell cell) => BucketOf(cell, FineSize);

    /// <summary>The coarse bucket a cell lies in.</summary>
    public static Cell CoarseBucketOf(Cell cell) => BucketOf(cell, CoarseSize);

    public static Cell BucketOf(Cell cell, int size) => new(Floor(cell.X, size), Floor(cell.Y, size), Floor(cell.Z, size));

    private static int Floor(int value, int size) => value >= 0 ? value / size : -((-value + size - 1) / size);
}

/// <summary>A main-thread fact or answer a judgement needed and did not have yet.</summary>
internal abstract record Ask
{
    private Ask() { }

    /// <summary>The key the answer is filed under.</summary>
    public abstract ReadKey Key { get; }

    /// <summary>The game's placement check.</summary>
    public sealed record Game(Placement At) : Ask
    {
        public override ReadKey Key => ReadKey.Game(At);
    }

    /// <summary>The room a cell is in.</summary>
    public sealed record Room(Cell At) : Ask
    {
        public override ReadKey Key => ReadKey.RoomAt(At);
    }

    /// <summary>The network a structure is on.</summary>
    public sealed record Network(ReferenceId Of) : Ask
    {
        public override ReadKey Key => ReadKey.NetworkOf(Of);
    }

    /// <summary>A mod's main-thread support rule, asked with the world the ghost is judged on.</summary>
    public sealed record MainSupport(SupportRules.Entry Entry, GhostId Ghost, SupportInput Input, int Epoch) : Ask
    {
        public override ReadKey Key => ReadKey.MainRule(Entry.Rule.Id, Ghost);
    }

    /// <summary>A mod's main-thread validation rule, asked with the world the ghost is judged on.</summary>
    public sealed record MainRule(Validation.ValidationRules.Entry Entry, RuleInput Input, int Epoch) : Ask
    {
        public override ReadKey Key => ReadKey.MainRule(Entry.Rule.Id, Input.Ghost.Id);
    }
}

/// <summary>
/// What one judgement read, and what it asked for and did not get. A judgement with asks is incomplete: its answer
/// stands only until the asks are answered. Not thread-safe: one judgement owns it.
/// </summary>
internal sealed class ReadLog
{
    private readonly HashSet<ReadKey> _keys = [];
    private readonly List<Ask> _asks = [];

    public IReadOnlyCollection<ReadKey> Keys => _keys;

    public IReadOnlyList<Ask> Asks => _asks;

    public bool Complete => _asks.Count == 0;

    /// <summary>Empties the log for the next judgement, keeping its room.</summary>
    public void Clear()
    {
        _keys.Clear();
        _asks.Clear();
    }

    public void Read(ReadKey key) => _keys.Add(key);

    public void Need(Ask ask)
    {
        _keys.Add(ask.Key);
        foreach (var existing in _asks)
            if (existing.Key == ask.Key)
                return;
        _asks.Add(ask);
    }

    /// <summary>Takes in what another log read (a rule's call that answered in time).</summary>
    public void Merge(ReadLog other)
    {
        foreach (var key in other._keys)
            _keys.Add(key);
        foreach (var ask in other._asks)
            Need(ask);
    }
}
