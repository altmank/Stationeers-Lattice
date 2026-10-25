using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Ports;
using Lattice.TestKit.Grid;

namespace Lattice.TestKit.World;

/// <summary>
/// The built world for tests: <see cref="IWorldView"/> over structures by id and by cell, networks derived from the
/// catalogue's ends (with the volume and contents the test sets on a network), rooms and face pressures the test sets; <see cref="IWorldChanges"/> raised by the scripted
/// changes below.
/// A structure occupies its own cell plus its piece's footprint cells, turned (the game's grid bounds: a long pipe
/// runs pivot..+z, a long chute pivot..-x); a piece with no footprint occupies every cell on the line to its ends.
/// Changes: handlers run in subscription order from a copied subscriber array; a change made
/// from inside a handler is queued and applied after the current raise, never nested, so reads in a handler see the
/// world as of the event. <see cref="End"/> ends every subscription.
/// Reads allocate nothing once the networks are derived (they are derived on the first network read after a change).
/// </summary>
internal sealed class FakeWorld : IWorldView, IWorldChanges
{
    private readonly ICatalogue _catalogue;
    private readonly Dictionary<ReferenceId, StructureInfo> _structures = [];
    private readonly Dictionary<ReferenceId, Cell[]> _occupies = [];
    private readonly Dictionary<Cell, List<ReferenceId>> _members = [];
    private readonly Dictionary<Cell, ValueList<StructureInfo>> _byCell = [];
    private readonly Dictionary<Cell, RoomRef> _roomAt = [];
    private readonly Dictionary<RoomRef, RoomInfo> _rooms = [];
    private readonly Dictionary<(Cell, Direction), Kilopascals> _pressure = [];
    private readonly HashSet<Cell> _terrain = new();
    private readonly Dictionary<NetworkRef, (Litres Volume, ValueList<GasHeld> Contents)> _gas = [];
    private readonly Queue<Action> _pending = new();
    private readonly Subscribers<Action<StructureInfo>> _registered = new();
    private readonly Subscribers<Action<StructureRef, BuildState>> _stateChanged = new();
    private readonly Subscribers<Action<StructureRef>> _removed = new();
    private readonly Subscribers<Action<StructureRef, PaintColour>> _painted = new();
    private readonly Subscribers<Action<StructureRef, ThingName>> _renamed = new();
    private readonly Subscribers<Action<StructureRef, Setting>> _settingChanged = new();
    private readonly Subscribers<Action<ValueList<StructureMove>>> _moved = new();
    private Networks? _networks;
    private bool _raising;

    public FakeWorld(ICatalogue catalogue) => _catalogue = catalogue;

    public int Count => _structures.Count;

    public IReadOnlyCollection<StructureInfo> All => _structures.Values;

    /// <summary>The derived networks.</summary>
    public IReadOnlyDictionary<NetworkRef, NetworkInfo> AllNetworks => CurrentNetworks.All;

    // IWorldView

    public Option<StructureInfo> Structure(ReferenceId id) =>
        _structures.TryGetValue(id, out var info) ? Option.Some(info) : Option<StructureInfo>.None;

    public ValueList<StructureInfo> At(Cell cell) =>
        _byCell.TryGetValue(cell, out var list) ? list : ValueList<StructureInfo>.Empty;

    public Option<NetworkRef> NetworkOf(ReferenceId id) => CurrentNetworks.Of(id);

    public Option<NetworkInfo> Network(NetworkRef network) =>
        _gas.TryGetValue(network, out var gas)
            ? CurrentNetworks.Info(network).Map(info => info with { Volume = Option.Some(gas.Volume), Contents = Option.Some(gas.Contents) })
            : CurrentNetworks.Info(network);

    public Option<RoomRef> RoomAt(Cell cell) => _roomAt.TryGetValue(cell, out var room) ? Option.Some(room) : Option<RoomRef>.None;

    public Option<RoomInfo> Room(RoomRef room) => _rooms.TryGetValue(room, out var info) ? Option.Some(info) : Option<RoomInfo>.None;

    public Kilopascals PressureDifference(Cell cell, Direction face) =>
        _pressure.TryGetValue((cell, face), out var kpa) ? kpa : default;

    public bool Terrain(Cell large) => _terrain.Contains(large);

    // IWorldChanges

    public event Action<StructureInfo> Registered { add => _registered.Add(value); remove => _registered.Remove(value); }

    public event Action<StructureRef, BuildState> StateChanged { add => _stateChanged.Add(value); remove => _stateChanged.Remove(value); }

    public event Action<StructureRef> Removed { add => _removed.Add(value); remove => _removed.Remove(value); }

    public event Action<StructureRef, PaintColour> Painted { add => _painted.Add(value); remove => _painted.Remove(value); }

    public event Action<StructureRef, ThingName> Renamed { add => _renamed.Add(value); remove => _renamed.Remove(value); }

    public event Action<StructureRef, Setting> SettingChanged { add => _settingChanged.Add(value); remove => _settingChanged.Remove(value); }

    public event Action<ValueList<StructureMove>> Moved { add => _moved.Add(value); remove => _moved.Remove(value); }

    // Scripted changes. Each applies, then raises its event. An unknown id or a second structure with one id is a bug in
    // the test and throws.

    /// <summary>Adds a structure without raising anything: the world as loaded.</summary>
    public void Load(StructureInfo info)
    {
        if (_structures.ContainsKey(info.Ref.Id))
            throw new InvalidOperationException($"Structure {info.Ref.Id} is already in the world.");
        Put(info);
    }

    public void Register(StructureInfo info) => Change(() =>
    {
        Load(info);
        Raise(_registered, h => h(info));
    });

    public void ChangeStep(ReferenceId id, StepIndex step) =>
        ChangeState(id, BuildStates.At(step, _catalogue.Piece(Existing(id).Ref.Piece).ToOption()));

    public void ChangeState(ReferenceId id, BuildState state) => Change(() =>
    {
        var info = Existing(id);
        Put(info with { State = state });
        Raise(_stateChanged, h => h(info.Ref, state));
    });

    public void Remove(ReferenceId id) => Change(() =>
    {
        var info = Existing(id);
        Take(id);
        Raise(_removed, h => h(info.Ref));
    });

    public void Paint(ReferenceId id, PaintColour colour) => Change(() =>
    {
        var info = Existing(id);
        Put(info with { Colour = Option.Some(colour) });
        Raise(_painted, h => h(info.Ref, colour));
    });

    public void Rename(ReferenceId id, ThingName name) => Change(() =>
    {
        var info = Existing(id);
        Put(info with { Name = Option.Some(name) });
        Raise(_renamed, h => h(info.Ref, name));
    });

    public void ChangeSetting(ReferenceId id, Setting setting) => Change(() =>
    {
        var info = Existing(id);
        Put(info with { Setting = Option.Some(setting) });
        Raise(_settingChanged, h => h(info.Ref, setting));
    });

    public void Move(ValueList<StructureMove> moves) => Change(() =>
    {
        foreach (var move in moves)
        {
            var info = Existing(move.From.Id);
            Put(info with { Ref = info.Ref with { At = move.To }, Turn = move.Turn });
        }

        Raise(_moved, h => h(moves));
    });

    /// <summary>Makes the room over its cells (replacing any room those cells were in).</summary>
    public void SetRoom(RoomInfo room)
    {
        _rooms[room.Ref] = room;
        foreach (var cell in room.Cells)
            _roomAt[cell] = room.Ref;
    }

    /// <summary>
    /// Gives the network with this ref (as derived now, its smallest piece id) a volume and contents. Kept by ref: a
    /// change that gives the network another ref leaves it without them.
    /// </summary>
    public void SetNetworkGas(NetworkRef network, Litres volume, ValueList<GasHeld> contents) => _gas[network] = (volume, contents);

    public void SetPressureDifference(Cell cell, Direction face, Kilopascals difference) => _pressure[(cell, face)] = difference;

    /// <summary>Terrain fills part of the large cell, or no longer does.</summary>
    public void SetTerrain(Cell large, bool filled = true)
    {
        if (filled)
            _terrain.Add(large);
        else
            _terrain.Remove(large);
    }

    /// <summary>The world ends: every subscription ends with it.</summary>
    public void End()
    {
        _registered.Clear();
        _stateChanged.Clear();
        _removed.Clear();
        _painted.Clear();
        _renamed.Clear();
        _settingChanged.Clear();
        _moved.Clear();
        _pending.Clear();
    }

    private Networks CurrentNetworks => _networks ??= Networks.Derive(_structures.Values, _catalogue, _occupies);

    private void Change(Action change)
    {
        if (_raising)
        {
            _pending.Enqueue(change);
            return;
        }

        change();
        while (_pending.Count > 0)
            _pending.Dequeue()();
    }

    private void Raise<THandler>(Subscribers<THandler> subscribers, Action<THandler> call) where THandler : Delegate
    {
        _raising = true;
        try
        {
            foreach (var handler in subscribers.Current)
                call(handler);
        }
        finally
        {
            _raising = false;
        }
    }

    private StructureInfo Existing(ReferenceId id) =>
        _structures.TryGetValue(id, out var info) ? info : throw new InvalidOperationException($"No structure {id} in the world.");

    private void Put(StructureInfo info)
    {
        if (_structures.ContainsKey(info.Ref.Id))
            Take(info.Ref.Id);
        _structures.Add(info.Ref.Id, info);
        var cells = Occupied(info);
        _occupies.Add(info.Ref.Id, cells);
        foreach (var cell in cells)
        {
            if (!_members.TryGetValue(cell, out var members))
                _members.Add(cell, members = []);
            members.Add(info.Ref.Id);
            Reindex(cell);
        }
        _networks = null;
    }

    private void Take(ReferenceId id)
    {
        _structures.Remove(id);
        if (_occupies.Remove(id, out var cells))
            foreach (var cell in cells)
            {
                _members[cell].Remove(id);
                Reindex(cell);
            }
        _networks = null;
    }

    private void Reindex(Cell cell)
    {
        var members = _members[cell];
        var here = new List<StructureInfo>(members.Count);
        foreach (var id in members)
            here.Add(_structures[id]);
        here.Sort(static (a, b) => a.Ref.Id.Value.CompareTo(b.Ref.Id.Value));
        if (here.Count == 0)
        {
            _byCell.Remove(cell);
            _members.Remove(cell);
        }
        else
            _byCell[cell] = ValueList.From(here);
    }

    private Cell[] Occupied(StructureInfo info)
    {
        var origin = info.Ref.At;
        if (!_catalogue.Piece(info.Ref.Piece).TryGet(out var facts))
            return [origin];
        var step = FakeGrid.Step(facts.Grid);
        var cells = new List<Cell> { origin };
        if (facts.Footprint.Count > 0)
        {
            foreach (var cell in facts.Footprint)
                AddOnce(cells, origin.Plus(FakeGrid.Instance.Apply(info.Turn, cell.At)));
            return [.. cells];
        }

        foreach (var end in facts.Ends)
        {
            var offset = FakeGrid.Instance.Apply(info.Turn, end.At);
            var axes = (offset.X != 0 ? 1 : 0) + (offset.Y != 0 ? 1 : 0) + (offset.Z != 0 ? 1 : 0);
            if (axes == 0)
                continue;
            if (axes > 1)
            {
                AddOnce(cells, origin.Plus(offset));
                continue;
            }

            var length = Math.Abs(offset.X + offset.Y + offset.Z);
            var unit = new CellOffset(Math.Sign(offset.X) * step, Math.Sign(offset.Y) * step, Math.Sign(offset.Z) * step);
            var cell = origin;
            for (var walked = step; walked <= length; walked += step)
                AddOnce(cells, cell = cell.Plus(unit));
        }

        return cells.ToArray();
    }

    private static void AddOnce(List<Cell> cells, Cell cell)
    {
        if (!cells.Contains(cell))
            cells.Add(cell);
    }
}
