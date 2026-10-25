using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Ports;
using Lattice.Core.Validation;

namespace Lattice.Core.Support;

/// <summary>What an assumed change does.</summary>
internal enum ChangeKind : byte
{
    /// <summary>A build or swap ghost's placement stands, fully built.</summary>
    Put = 1,

    /// <summary>A removal or swap ghost takes a structure away.</summary>
    Take = 2,
}

/// <summary>One assumed change: the ghost, the stage it is done in, and what it puts or takes where.</summary>
internal readonly record struct AssumedChange(GhostId Ghost, int Stage, ChangeKind Kind, Cell At, StructureInfo? Put, ReferenceId Target);

/// <summary>
/// The ghosts a judging pass assumes done, each tagged with its stage, indexed by cell, id and bucket. Built once per
/// pass; after that it is only read, so views on it (<see cref="SupportWorld"/>) can be handed to other threads while
/// nothing builds on it. Not thread-safe while it is being built.
/// <para>
/// A pass over a large plan builds two of these, so the changes of a cell or bucket are a chain through an array
/// parallel to the changes (first and last per key, next per change) rather than a list per key: a 10,000-ghost index
/// is a few arrays and dictionaries of value pairs, not tens of thousands of small lists.
/// </para>
/// </summary>
internal sealed class AssumedIndex
{
    /// <summary>The stage bound that sees every change.</summary>
    public const int Every = int.MaxValue;

    private readonly List<AssumedChange> _changes;
    private readonly Chains _putsAt;
    private readonly Dictionary<ReferenceId, int> _puts;
    private readonly Dictionary<ReferenceId, int> _takes = [];
    private readonly Chains _fine;
    private readonly Chains _coarse;

    /// <summary>An empty index with room for about <paramref name="changes"/> changes, so building it copies nothing.</summary>
    public AssumedIndex(int changes = 0)
    {
        _changes = new List<AssumedChange>(changes);
        _putsAt = new Chains(changes, changes);
        _puts = new Dictionary<ReferenceId, int>(changes);
        _fine = new Chains(changes, changes);
        _coarse = new Chains(0, changes);
    }

    public int Count => _changes.Count;

    public AssumedChange this[int index] => _changes[index];

    /// <summary>
    /// The ghost's placement stands from the end of its stage, as <paramref name="info"/> when given (it must be
    /// <see cref="SupportWorld.AssumedInfo"/> of the ghost and placement).
    /// </summary>
    public void Put(GhostId ghost, int stage, Placement put, StructureInfo? info = null)
    {
        info ??= SupportWorld.AssumedInfo(ghost, put);
        var index = Add(new AssumedChange(ghost, stage, ChangeKind.Put, put.At, info, info.Ref.Id));
        _putsAt.Append(put.At, index);
        _puts[info.Ref.Id] = index;
    }

    /// <summary>The ghost takes the structure away at the end of its stage.</summary>
    public void Take(GhostId ghost, int stage, StructureRef target)
    {
        var index = Add(new AssumedChange(ghost, stage, ChangeKind.Take, target.At, null, target.Id));
        if (!_takes.TryGetValue(target.Id, out var earlier) || _changes[earlier].Stage > stage)
            _takes[target.Id] = index;
    }

    /// <summary>Indices of the puts whose own cell is the cell, in the order put.</summary>
    public Chain PutsAt(Cell cell) => _putsAt.Of(cell);

    public bool TryPut(ReferenceId id, out AssumedChange put)
    {
        if (_puts.TryGetValue(id, out var index))
        {
            put = _changes[index];
            return true;
        }

        put = default;
        return false;
    }

    /// <summary>The earliest stage a structure is taken in, if any change takes it.</summary>
    public bool TryTakenAt(ReferenceId id, out int stage)
    {
        if (_takes.TryGetValue(id, out var index))
        {
            stage = _changes[index].Stage;
            return true;
        }

        stage = 0;
        return false;
    }

    /// <summary>Indices of the changes in a fine bucket, in the order added.</summary>
    public Chain InFine(Cell bucket) => _fine.Of(bucket);

    /// <summary>Indices of the changes in a coarse bucket, in the order added.</summary>
    public Chain InCoarse(Cell bucket) => _coarse.Of(bucket);

    private int Add(AssumedChange change)
    {
        var index = _changes.Count;
        _changes.Add(change);
        _fine.Append(ReadKey.FineBucketOf(change.At), index);
        _coarse.Append(ReadKey.CoarseBucketOf(change.At), index);
        return index;
    }

    /// <summary>
    /// Change indices grouped by cell, in the order appended: the first and last per cell, and each index's next
    /// (-1 at the end). An index is appended to one cell at most.
    /// </summary>
    private sealed class Chains(int cells, int indices)
    {
        private readonly Dictionary<Cell, (int First, int Last)> _ends = new(cells);
        private readonly List<int> _next = new(indices);

        public void Append(Cell cell, int index)
        {
            while (_next.Count <= index)
                _next.Add(-1);
            if (_ends.TryGetValue(cell, out var ends))
            {
                _next[ends.Last] = index;
                _ends[cell] = (ends.First, index);
            }
            else
                _ends.Add(cell, (index, index));
        }

        public Chain Of(Cell cell) => _ends.TryGetValue(cell, out var ends) ? new Chain(_next, ends.First) : default;
    }
}

/// <summary>The change indices of one cell or bucket of an <see cref="AssumedIndex"/>, walked without allocating.</summary>
internal readonly struct Chain
{
    private readonly List<int>? _next;
    private readonly int _first;

    internal Chain(List<int> next, int first) => (_next, _first) = (next, first);

    public Enumerator GetEnumerator() => new(_next, _next is null ? -1 : _first);

    /// <summary>Steps through the indices of a chain in order.</summary>
    public struct Enumerator
    {
        private readonly List<int>? _next;
        private int _at;
        private bool _started;

        internal Enumerator(List<int>? next, int first) => (_next, _at, _started) = (next, first, false);

        public readonly int Current => _at;

        public bool MoveNext()
        {
            if (_at < 0)
                return false;
            if (_started)
                _at = _next![_at];
            _started = true;
            return _at >= 0;
        }
    }
}

/// <summary>
/// The built world with some ghosts assumed done, as one judgement sees it: build and swap ghosts of earlier stages
/// stand, fully built, in their placement; the structures removal and swap ghosts of earlier stages take are gone.
/// The all-builds view (<see cref="Takes"/> false, bound <see cref="AssumedIndex.Every"/>) shows every placement and
/// takes nothing away, with every change counted near. An assumed structure's id is the negated ghost id, so it never
/// meets a real one (the game's ids are positive). Rules from mods read it through <see cref="IWorldQuery"/>.
/// <para>
/// Every read goes into the view's <see cref="ReadLog"/>, so a change to what was read finds the judgement; a fact the
/// main thread has not answered yet is asked for and reads as none. A view is only read; it is not thread-safe
/// against a pass still building its index.
/// </para>
/// </summary>
internal sealed class SupportWorld : IWorldQuery
{
    private readonly AssumedIndex _index;
    private readonly IJudgeFacts _facts;
    private SupportWorld? _carried;

    public SupportWorld(AssumedIndex index, int bound, bool takes, IJudgeFacts facts, ICatalogue catalogue, ReadLog log)
    {
        _index = index;
        Bound = bound;
        Takes = takes;
        _facts = facts;
        Catalogue = catalogue;
        Log = log;
    }

    public ICatalogue Catalogue { get; }

    /// <summary>Changes of stages below it are seen.</summary>
    public int Bound { get; }

    /// <summary>Whether takes take structures away (false for the all-builds view).</summary>
    public bool Takes { get; }

    public ReadLog Log { get; }

    /// <summary>The same view with a fresh log (a mod's rule call, whose reads count only when it answers in time).</summary>
    public SupportWorld Fork() => new(_index, Bound, Takes, _facts, Catalogue, new ReadLog());

    // The view a main-thread ask carries to the main thread: this view with a log of its own, so this view's log is
    // free for the next judgement once this one is kept.
    private SupportWorld Carried => _carried ??= Fork();

    /// <summary>The structure a ghost's placement would be once built: fully built, with the ghost's id negated.</summary>
    public static StructureInfo AssumedInfo(GhostId ghost, Placement put) =>
        new(new StructureRef(IdOf(ghost), put.Piece, put.At), put.Turn, new BuildState.Built(), Option<PaintColour>.None, Option<ThingName>.None, Option<Setting>.None);

    /// <summary>The id an assumed structure of the ghost has.</summary>
    public static ReferenceId IdOf(GhostId ghost) => new(-ghost.Value);

    public Option<StructureInfo> Structure(ReferenceId id)
    {
        Log.Read(ReadKey.StructureOf(id));
        if (id.Value < 0)
            return _index.TryPut(id, out var put) && Sees(put.Stage) && !Taken(id) ? Option.Some(put.Put!) : Option<StructureInfo>.None;
        return Taken(id) ? Option<StructureInfo>.None : _facts.Structure(id);
    }

    public ValueList<StructureInfo> At(Cell cell)
    {
        Log.Read(ReadKey.At(cell));
        var real = _facts.At(cell);
        List<StructureInfo>? here = null;
        foreach (var index in _index.PutsAt(cell))
        {
            var put = _index[index];
            if (!Sees(put.Stage) || Taken(put.Target))
                continue;
            here ??= Standing(real, real.Count + 1);
            here.Add(put.Put!);
        }

        if (here is not null)
            return ValueList.From(here);
        foreach (var info in real)
            if (Taken(info.Ref.Id))
                return ValueList.From(Standing(real, real.Count));
        return real;
    }

    // The real structures in a cell that no change seen takes.
    private List<StructureInfo> Standing(ValueList<StructureInfo> real, int room)
    {
        var here = new List<StructureInfo>(room);
        foreach (var info in real)
            if (!Taken(info.Ref.Id))
                here.Add(info);
        return here;
    }

    public Result<ValueList<StructureRef>, Reason> Select(StructureSelector selector) =>
        Result<ValueList<StructureRef>, Reason>.Error(
            Reason.Of(ReasonCodes.ModuleMissing, SupportCodes.Order, ValueMap<string, string>.Of("module", "selectors")));

    public Option<NetworkRef> NetworkOf(ReferenceId id)
    {
        Log.Read(ReadKey.StructureOf(id));
        if (id.Value < 0 || Taken(id))
            return Option<NetworkRef>.None;
        if (_facts.NetworkOf(id).TryGet(out var network))
        {
            Log.Read(ReadKey.NetworkOf(id));
            return network;
        }

        Log.Need(new Ask.Network(id));
        return Option<NetworkRef>.None;
    }

    public Option<RoomRef> RoomAt(Cell cell)
    {
        if (_facts.RoomAt(cell).TryGet(out var room))
        {
            Log.Read(ReadKey.RoomAt(cell));
            return room;
        }

        Log.Need(new Ask.Room(cell));
        return Option<RoomRef>.None;
    }

    /// <summary>The game's placement check on the built world; none, and asked for, while the main thread has not answered.</summary>
    public Option<GameAnswer> Game(Placement placement)
    {
        if (_facts.Game(placement).TryGet(out var answer))
        {
            Log.Read(ReadKey.Game(placement));
            return Option.Some(answer);
        }

        Log.Need(new Ask.Game(placement));
        return Option<GameAnswer>.None;
    }

    public Option<GhostId> AssumedFrom(StructureRef structure)
    {
        Log.Read(ReadKey.StructureOf(structure.Id));
        return _index.TryPut(structure.Id, out var put) && Sees(put.Stage) && !Taken(structure.Id)
            ? Option.Some(put.Ghost)
            : Option<GhostId>.None;
    }

    /// <summary>Whether the structure is real and still standing (not assumed, not taken).</summary>
    public bool IsReal(StructureRef structure)
    {
        Log.Read(ReadKey.StructureOf(structure.Id));
        return structure.Id.Value >= 0 && !Taken(structure.Id);
    }

    /// <summary>
    /// The ghosts with an assumed change in the box (corners included), other than <paramref name="self"/>: of earlier
    /// stages, or of the same stage too with <paramref name="sameStage"/>. The all-builds view counts every change.
    /// Cost: the buckets the box covers (fine ones, or coarse ones past 64 fine) and the changes in them; allocates
    /// only when it finds a ghost.
    /// </summary>
    public ValueList<GhostId> ChangesIn(Cell low, Cell high, GhostId self, bool sameStage = false)
    {
        List<GhostId>? near = null;
        Scan(low, high, self, sameStage, ref near, list: true);
        return near is null ? ValueList<GhostId>.Empty : ValueList.From(near);
    }

    /// <summary>
    /// Whether <see cref="ChangesIn"/> would name any ghost: the same buckets read, so the same changes find the
    /// judgement, with no list made.
    /// </summary>
    public bool AnyChangeIn(Cell low, Cell high, GhostId self, bool sameStage = false)
    {
        List<GhostId>? near = null;
        return Scan(low, high, self, sameStage, ref near, list: false) > 0;
    }

    // Reads every bucket the box covers; counts the changes in the box seen, and lists their ghosts once each when asked.
    private int Scan(Cell low, Cell high, GhostId self, bool sameStage, ref List<GhostId>? near, bool list)
    {
        var found = 0;
        var fineLow = ReadKey.FineBucketOf(low);
        var fineHigh = ReadKey.FineBucketOf(high);
        var fine = (long)(fineHigh.X - fineLow.X + 1) * (fineHigh.Y - fineLow.Y + 1) * (fineHigh.Z - fineLow.Z + 1);
        var coarse = fine > 64;
        var size = coarse ? ReadKey.CoarseSize : ReadKey.FineSize;
        var from = ReadKey.BucketOf(low, size);
        var to = ReadKey.BucketOf(high, size);
        for (var x = from.X; x <= to.X; x++)
            for (var y = from.Y; y <= to.Y; y++)
                for (var z = from.Z; z <= to.Z; z++)
                {
                    var bucket = new Cell(x, y, z);
                    Log.Read(coarse ? ReadKey.Coarse(bucket) : ReadKey.Fine(bucket));
                    foreach (var index in coarse ? _index.InCoarse(bucket) : _index.InFine(bucket))
                    {
                        var change = _index[index];
                        if (change.Ghost == self || !(Sees(change.Stage) || (sameStage && change.Stage == Bound)) || (near is not null && near.Contains(change.Ghost)))
                            continue;
                        var at = change.At;
                        if (at.X < low.X || at.X > high.X || at.Y < low.Y || at.Y > high.Y || at.Z < low.Z || at.Z > high.Z)
                            continue;
                        found++;
                        if (list)
                            (near ??= new List<GhostId>(4)).Add(change.Ghost);
                    }
                }

        return found;
    }

    /// <summary>A mod's main-thread support rule's answer; none, and asked for, while the main thread has not run it.</summary>
    public Option<Guarded<SupportVerdict>> MainSupport(SupportRules rules, SupportRules.Entry entry, GhostId ghost, SupportInput input)
    {
        if (_facts.MainSupport(rules, entry, ghost, input).TryGet(out var answer))
        {
            Log.Read(ReadKey.MainRule(entry.Rule.Id, ghost));
            return Option.Some(answer);
        }

        Log.Need(new Ask.MainSupport(entry, ghost, input with { World = Carried }, _facts.EpochOf(ghost)));
        return Option<Guarded<SupportVerdict>>.None;
    }

    /// <summary>A mod's main-thread validation rule's answer; none, and asked for, while the main thread has not run it.</summary>
    public Option<Guarded<RuleVerdict>> MainRule(ValidationRules rules, ValidationRules.Entry entry, RuleInput input)
    {
        if (_facts.MainRule(rules, entry, input).TryGet(out var answer))
        {
            Log.Read(ReadKey.MainRule(entry.Rule.Id, input.Ghost.Id));
            return Option.Some(answer);
        }

        Log.Need(new Ask.MainRule(entry, input with { World = Carried }, _facts.EpochOf(input.Ghost.Id)));
        return Option<Guarded<RuleVerdict>>.None;
    }

    private bool Sees(int stage) => stage < Bound;

    private bool Taken(ReferenceId id) => Takes && _index.TryTakenAt(id, out var stage) && Sees(stage);
}
