using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Ports;

namespace Lattice.Core.BuiltWorld;

/// <summary>
/// Core's copy of the built world's structures. <see cref="Start"/> subscribes to the world's change signals, then
/// scans the world once; after that the mirror follows the signals alone and never scans again. A signal naming a
/// structure the mirror does not hold is the one point read it makes (<see cref="IWorldView.Structure"/> of that id).
/// A structure seen by the scan and by a signal is one structure: duplicates are merged by id.
/// <para>
/// Each structure is indexed by id, by piece, by piece and cell (a ghost's target survives the game giving it a new id), by the
/// spots its catalogue shape takes, and by every cell those spots lie in. A structure whose piece the catalogue does
/// not hold has no shape: it is indexed by its own cell only, and <see cref="InSpots"/> counts it in every spot of
/// that cell, so nothing unknown is ever taken for empty space.
/// </para>
/// Main thread. Reads are constant cost and allocate nothing; <see cref="Changed"/> is raised after each change is
/// applied, so a handler reads the mirror as it now stands. <see cref="Snapshot"/> is the immutable copy for the worker.
/// </summary>
internal sealed class WorldMirror
{
    private readonly Shapes _shapes;
    private readonly IWorldView _world;
    private readonly Dictionary<ReferenceId, Held> _byId = new();
    private readonly Dictionary<(PrefabKey, Cell), List<ReferenceId>> _byPlace = new();
    private readonly Dictionary<PrefabKey, HashSet<ReferenceId>> _byPiece = new();
    private readonly Dictionary<SpotKey, List<ReferenceId>> _bySpot = new();
    private readonly Dictionary<Cell, List<ReferenceId>> _unshaped = new();
    private readonly Dictionary<Cell, List<ReferenceId>> _members = new();
    private readonly Dictionary<Cell, ValueList<StructureInfo>> _byCell = new();
    private readonly Subscribers<Action<MirrorChange>> _changed = new();
    private readonly HashSet<ReferenceId> _seen = new();
    private readonly Action<StructureInfo> _registered;
    private readonly Action<StructureRef, BuildState> _stateChanged;
    private readonly Action<StructureRef> _removed;
    private readonly Action<StructureRef, PaintColour> _painted;
    private readonly Action<StructureRef, ThingName> _renamed;
    private readonly Action<StructureRef, Setting> _settingChanged;
    private readonly Action<ValueList<StructureMove>> _moved;
    private IWorldChanges? _changes;
    private MirrorSnapshot? _snapshot;
    private Held? _taken;

    public WorldMirror(Shapes shapes, IWorldView world)
    {
        _shapes = shapes;
        _world = world;
        _registered = OnRegistered;
        _stateChanged = OnStateChanged;
        _removed = OnRemoved;
        _painted = OnPainted;
        _renamed = OnRenamed;
        _settingChanged = OnSettingChanged;
        _moved = OnMoved;
    }

    /// <summary>Rises with every change the mirror applies; a merged duplicate changes nothing and leaves it.</summary>
    public long Revision { get; private set; }

    /// <summary>How many structures the mirror holds.</summary>
    public int Count => _byId.Count;

    /// <summary>Raised after each change is applied, in signal order. Handlers are called plainly.</summary>
    public event Action<MirrorChange> Changed { add => _changed.Add(value); remove => _changed.Remove(value); }

    /// <summary>
    /// Subscribes to the world's signals, then takes in one scan of the world (nothing registered between
    /// the two is lost). Once per world; a second start is a bug and throws. Linear in the world's structures.
    /// </summary>
    public void Start(IWorldChanges changes, IWorldScan scan)
    {
        if (_changes is not null)
            throw new InvalidOperationException("The world mirror is already started.");
        _changes = changes;
        changes.Registered += _registered;
        changes.StateChanged += _stateChanged;
        changes.Removed += _removed;
        changes.Painted += _painted;
        changes.Renamed += _renamed;
        changes.SettingChanged += _settingChanged;
        changes.Moved += _moved;

        foreach (var info in scan.Structures())
            if (!_byId.ContainsKey(info.Ref.Id))
                Put(info);
        Revision++;
        _snapshot = null;
    }

    /// <summary>The world ends: every subscription ends and the mirror empties.</summary>
    public void End()
    {
        if (_changes is { } changes)
        {
            changes.Registered -= _registered;
            changes.StateChanged -= _stateChanged;
            changes.Removed -= _removed;
            changes.Painted -= _painted;
            changes.Renamed -= _renamed;
            changes.SettingChanged -= _settingChanged;
            changes.Moved -= _moved;
        }

        _changes = null;
        _byId.Clear();
        _byPlace.Clear();
        _byPiece.Clear();
        _bySpot.Clear();
        _unshaped.Clear();
        _members.Clear();
        _byCell.Clear();
        _snapshot = null;
        _taken = null;
    }

    /// <summary>A structure by id. Constant cost.</summary>
    public Option<StructureInfo> Structure(ReferenceId id) =>
        _byId.TryGetValue(id, out var held) ? Option.Some(held.Info) : Option<StructureInfo>.None;

    /// <summary>The structure a reference names: by id, else the lowest id of its piece in its cell. Constant cost.</summary>
    public Option<StructureInfo> Find(StructureRef structure)
    {
        if (_byId.TryGetValue(structure.Id, out var held))
            return Option.Some(held.Info);
        return _byPlace.TryGetValue((structure.Piece, structure.At), out var ids) && ids.Count > 0
            ? Option.Some(_byId[Lowest(ids)].Info)
            : Option<StructureInfo>.None;
    }

    /// <summary>The structures any of whose spots lie in a cell, in id order. Constant cost; allocates nothing.</summary>
    public ValueList<StructureInfo> At(Cell cell) =>
        _byCell.TryGetValue(cell, out var here) ? here : ValueList<StructureInfo>.Empty;

    /// <summary>The ids of the structures of a piece, in no set order. Constant cost; allocates nothing.</summary>
    public IReadOnlyCollection<ReferenceId> OfPiece(PrefabKey piece) =>
        _byPiece.TryGetValue(piece, out var ids) ? ids : Array.Empty<ReferenceId>();

    /// <summary>Adds every structure held to <paramref name="into"/>, in no set order. Linear in the structures held.</summary>
    public void All(List<StructureInfo> into)
    {
        foreach (var held in _byId.Values)
            into.Add(held.Info);
    }

    /// <summary>
    /// Adds to <paramref name="into"/>, once each and in id order, every structure taking one of the spots, and every
    /// structure without a shape in one of their cells. Linear in the spots and what holds them.
    /// </summary>
    public void InSpots(IReadOnlyList<SpotKey> spots, List<StructureInfo> into)
    {
        _seen.Clear();
        var start = into.Count;
        for (var i = 0; i < spots.Count; i++)
        {
            if (_bySpot.TryGetValue(spots[i], out var holders))
                Collect(holders, into);
            if (_unshaped.TryGetValue(spots[i].Cell, out var unknown))
                Collect(unknown, into);
        }

        into.Sort(start, into.Count - start, ById.Instance);
    }

    /// <summary>
    /// The spots a structure takes as the catalogue shapes its piece; none for a piece the catalogue does not hold.
    /// A held structure at its held place and turn, or the one last taken out (the before of a change just raised),
    /// answers the spots it is held by: constant cost, allocates nothing. Any other is shaped anew.
    /// </summary>
    public Option<IReadOnlyList<SpotKey>> SpotsOf(StructureInfo info)
    {
        if (_byId.TryGetValue(info.Ref.Id, out var held) && held.SameShapeAs(info))
            return held.Spots;
        return _taken is { } taken && taken.SameShapeAs(info) ? taken.Spots : Shape(info);
    }

    /// <summary>
    /// The mirror as it stands now, immutable, for the worker. The same snapshot until the next change; building one
    /// is linear in the structures held.
    /// </summary>
    public MirrorSnapshot Snapshot() =>
        _snapshot ??= new MirrorSnapshot(Revision, Copy(_byId), new Dictionary<Cell, ValueList<StructureInfo>>(_byCell));

    private static Dictionary<ReferenceId, StructureInfo> Copy(Dictionary<ReferenceId, Held> held)
    {
        var copy = new Dictionary<ReferenceId, StructureInfo>(held.Count);
        foreach (var pair in held)
            copy.Add(pair.Key, pair.Value.Info);
        return copy;
    }

    private void OnRegistered(StructureInfo info)
    {
        if (!_byId.TryGetValue(info.Ref.Id, out var held))
            Appear(info);
        else if (held.Info != info)
            Alter(held.Info, info);
    }

    private void OnStateChanged(StructureRef structure, BuildState state) =>
        Update(structure, info => info with { State = state });

    private void OnPainted(StructureRef structure, PaintColour colour) =>
        Update(structure, info => info with { Colour = Option.Some(colour) });

    private void OnRenamed(StructureRef structure, ThingName name) =>
        Update(structure, info => info with { Name = Option.Some(name) });

    private void OnSettingChanged(StructureRef structure, Setting setting) =>
        Update(structure, info => info with { Setting = Option.Some(setting) });

    private void OnRemoved(StructureRef structure)
    {
        if (!_byId.TryGetValue(structure.Id, out var held))
            return;
        Take(structure.Id);
        Raise(new MirrorChange.Gone(held.Info));
    }

    private void OnMoved(ValueList<StructureMove> moves)
    {
        foreach (var move in moves)
            if (_byId.TryGetValue(move.From.Id, out var held))
                Alter(held.Info, held.Info with { Ref = held.Info.Ref with { At = move.To }, Turn = move.Turn });
    }

    private void Update(StructureRef structure, Func<StructureInfo, StructureInfo> change)
    {
        if (_byId.TryGetValue(structure.Id, out var held))
        {
            var now = change(held.Info);
            if (now != held.Info)
                Alter(held.Info, now);
        }
        else if (_world.Structure(structure.Id).TryGet(out var read))
            Appear(read);
    }

    private void Appear(StructureInfo info)
    {
        Put(info);
        Raise(new MirrorChange.Appeared(info));
    }

    private void Alter(StructureInfo before, StructureInfo now)
    {
        Take(before.Ref.Id);
        Put(now);
        Raise(new MirrorChange.Altered(before, now));
    }

    private void Raise(MirrorChange change)
    {
        Revision++;
        _snapshot = null;
        var handlers = _changed.Current;
        for (var i = 0; i < handlers.Length; i++)
            handlers[i](change);
    }

    private Option<IReadOnlyList<SpotKey>> Shape(StructureInfo info) =>
        _shapes.Of(new Placement(info.Ref.Piece, info.Ref.At, info.Turn))
            .Match(static shape => Option.Some(shape.Spots), static _ => Option<IReadOnlyList<SpotKey>>.None);

    private void Put(StructureInfo info)
    {
        var id = info.Ref.Id;
        var spots = SpotsOf(info);
        _byId.Add(id, new Held(info, spots));
        Add(_byPlace, (info.Ref.Piece, info.Ref.At), id);
        if (!_byPiece.TryGetValue(info.Ref.Piece, out var ofPiece))
            _byPiece[info.Ref.Piece] = ofPiece = new HashSet<ReferenceId>();
        ofPiece.Add(id);
        if (spots.TryGet(out var taken))
        {
            for (var i = 0; i < taken.Count; i++)
            {
                Add(_bySpot, taken[i], id);
                Join(taken[i].Cell, id);
            }
        }
        else
            Add(_unshaped, info.Ref.At, id);
        Join(info.Ref.At, id);
    }

    private void Take(ReferenceId id)
    {
        var held = _byId[id];
        _byId.Remove(id);
        _taken = held;
        var info = held.Info;
        Drop(_byPlace, (info.Ref.Piece, info.Ref.At), id);
        if (_byPiece.TryGetValue(info.Ref.Piece, out var ofPiece) && ofPiece.Remove(id) && ofPiece.Count == 0)
            _byPiece.Remove(info.Ref.Piece);
        if (held.Spots.TryGet(out var taken))
        {
            for (var i = 0; i < taken.Count; i++)
            {
                Drop(_bySpot, taken[i], id);
                Leave(taken[i].Cell, id);
            }
        }
        else
            Drop(_unshaped, info.Ref.At, id);
        Leave(info.Ref.At, id);
    }

    private void Join(Cell cell, ReferenceId id)
    {
        if (!_members.TryGetValue(cell, out var members))
            _members[cell] = members = new List<ReferenceId>(2);
        if (members.Contains(id))
            return;
        members.Add(id);
        Reindex(cell, members);
    }

    private void Leave(Cell cell, ReferenceId id)
    {
        if (!_members.TryGetValue(cell, out var members) || !members.Remove(id))
            return;
        if (members.Count == 0)
        {
            _members.Remove(cell);
            _byCell.Remove(cell);
        }
        else
            Reindex(cell, members);
    }

    private void Reindex(Cell cell, List<ReferenceId> members)
    {
        var here = new StructureInfo[members.Count];
        for (var i = 0; i < here.Length; i++)
            here[i] = _byId[members[i]].Info;
        Array.Sort(here, ById.Instance);
        _byCell[cell] = Lists.Own(here);
    }

    private void Collect(List<ReferenceId> ids, List<StructureInfo> into)
    {
        for (var i = 0; i < ids.Count; i++)
            if (_seen.Add(ids[i]))
                into.Add(_byId[ids[i]].Info);
    }

    private static ReferenceId Lowest(List<ReferenceId> ids)
    {
        var lowest = ids[0];
        for (var i = 1; i < ids.Count; i++)
            if (ids[i].Value < lowest.Value)
                lowest = ids[i];
        return lowest;
    }

    private static void Add<TKey>(Dictionary<TKey, List<ReferenceId>> index, TKey key, ReferenceId id) where TKey : notnull
    {
        if (!index.TryGetValue(key, out var ids))
            index[key] = ids = new List<ReferenceId>(1);
        if (!ids.Contains(id))
            ids.Add(id);
    }

    private static void Drop<TKey>(Dictionary<TKey, List<ReferenceId>> index, TKey key, ReferenceId id) where TKey : notnull
    {
        if (index.TryGetValue(key, out var ids) && ids.Remove(id) && ids.Count == 0)
            index.Remove(key);
    }

    /// <summary>A held structure and the spots its shape takes (none when the catalogue does not hold its piece).</summary>
    private readonly record struct Held(StructureInfo Info, Option<IReadOnlyList<SpotKey>> Spots)
    {
        /// <summary>Whether <paramref name="info"/> takes these spots: the shape follows the piece, cell and turn alone.</summary>
        public bool SameShapeAs(StructureInfo info) => info.Ref == Info.Ref && info.Turn == Info.Turn;
    }

    private sealed class ById : IComparer<StructureInfo>
    {
        public static readonly ById Instance = new();

        public int Compare(StructureInfo? a, StructureInfo? b) => a!.Ref.Id.Value.CompareTo(b!.Ref.Id.Value);
    }
}
