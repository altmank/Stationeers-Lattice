using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Ports;

namespace Lattice.Core.World;

/// <summary>
/// The world's change signals for one world. The bridge reports what the game does as it happens
/// (<see cref="Report"/>); once a frame <see cref="Flush"/> raises the frame's changes in the order they happened.
/// Main thread only.
/// <list type="bullet">
/// <item>Subscribers are called in subscription order from an array copied on change: unsubscribing
/// during a raise takes effect from the next raise.</item>
/// <item>A change reported from inside a handler is raised after the current flush's changes, never nested.</item>
/// <item>A structure removed in the frame gives only <see cref="Removed"/>: its state changes, paint and names of that
/// frame are dropped (settings too). The game's last take-apart step sets the build index below 0 just before it destroys the
/// structure, which is not a broken structure.</item>
/// <item>A broken state is raised only for a structure that still stands at the flush (the bridge's
/// <c>stands</c> answer): the game marks a structure it is about to destroy before it leaves the grid.</item>
/// <item>Several state changes of one structure in a frame give one <see cref="StateChanged"/>, with the last state;
/// several setting changes give one <see cref="SettingChanged"/>, with the last value (logic writes it every tick).</item>
/// <item>A structure registered in the frame and not removed in it gives one <see cref="Registered"/> where it was
/// registered, holding the frame's last state, paint, name and setting set after the registration; none of those is
/// raised apart. A broken state folds in by the rule above: only if the structure stands, else it keeps the state it
/// was registered with.</item>
/// <item>A structure detached and attached again (in the same frame or later) gives one <see cref="Moved"/> entry; the
/// frame's moves are raised together, after its other changes.</item>
/// <item><see cref="End"/> drops every subscription and every change not yet raised.</item>
/// </list>
/// Exceptions from handlers are the handlers' own: they leave <see cref="Flush"/> and the changes not
/// yet raised are raised at the next flush.
/// </summary>
internal sealed class WorldChangeRelay : IWorldChanges
{
    private readonly Subscribers<Action<StructureInfo>> _registered = new();
    private readonly Subscribers<Action<StructureRef, BuildState>> _stateChanged = new();
    private readonly Subscribers<Action<StructureRef>> _removed = new();
    private readonly Subscribers<Action<StructureRef, PaintColour>> _painted = new();
    private readonly Subscribers<Action<StructureRef, ThingName>> _renamed = new();
    private readonly Subscribers<Action<ValueList<StructureMove>>> _moved = new();
    private readonly Subscribers<Action<StructureRef, Setting>> _settingChanged = new();

    private readonly Func<ReferenceId, bool> _stands;
    private readonly Dictionary<ReferenceId, StructureRef> _detached = new();
    private readonly HashSet<ReferenceId> _removedInBatch = new();
    private readonly Dictionary<ReferenceId, int> _lastStateInBatch = new();
    private readonly Dictionary<ReferenceId, int> _lastSettingInBatch = new();
    private readonly Dictionary<ReferenceId, int> _registeredInBatch = new();
    private readonly Dictionary<ReferenceId, StructureInfo> _folded = new();
    private readonly List<StructureMove> _moves = new(8);
    private List<WorldChange> _pending = new(64);
    private List<WorldChange> _batch = new(64);
    private int _next;
    private bool _flushing;
    private bool _ended;

    /// <summary>A relay whose structures all stand until they are reported removed.</summary>
    public WorldChangeRelay()
        : this(static _ => true)
    {
    }

    /// <summary>A relay that asks <paramref name="stands"/> at the flush before raising a broken state.</summary>
    public WorldChangeRelay(Func<ReferenceId, bool> stands) => _stands = stands;

    public event Action<StructureInfo> Registered { add => _registered.Add(value); remove => _registered.Remove(value); }

    public event Action<StructureRef, BuildState> StateChanged { add => _stateChanged.Add(value); remove => _stateChanged.Remove(value); }

    public event Action<StructureRef> Removed { add => _removed.Add(value); remove => _removed.Remove(value); }

    public event Action<StructureRef, PaintColour> Painted { add => _painted.Add(value); remove => _painted.Remove(value); }

    public event Action<StructureRef, ThingName> Renamed { add => _renamed.Add(value); remove => _renamed.Remove(value); }

    public event Action<ValueList<StructureMove>> Moved { add => _moved.Add(value); remove => _moved.Remove(value); }

    public event Action<StructureRef, Setting> SettingChanged { add => _settingChanged.Add(value); remove => _settingChanged.Remove(value); }

    /// <summary>Changes reported and not yet raised.</summary>
    public int Waiting => _pending.Count + _batch.Count - _next;

    /// <summary>Holds a change for the next flush. Ignored once the world has ended. Constant cost.</summary>
    public void Report(WorldChange change)
    {
        if (!_ended)
            _pending.Add(change);
    }

    /// <summary>
    /// Raises every change reported so far, and those reported by handlers while it runs. Does nothing when called
    /// from inside a handler (the running flush raises them). Linear in the changes raised; allocates nothing when
    /// there are none.
    /// </summary>
    public void Flush()
    {
        if (_flushing)
            return;
        _flushing = true;
        try
        {
            while (_next < _batch.Count || _pending.Count > 0)
            {
                if (_next >= _batch.Count)
                    TakePending();
                RaiseBatch();
            }
        }
        finally
        {
            _flushing = false;
        }
    }

    /// <summary>Ends the world: every subscription and every change not yet raised is dropped.</summary>
    public void End()
    {
        _ended = true;
        _pending.Clear();
        _batch.Clear();
        _next = 0;
        _detached.Clear();
        _moves.Clear();
        foreach (var handler in _registered.Current) _registered.Remove(handler);
        foreach (var handler in _stateChanged.Current) _stateChanged.Remove(handler);
        foreach (var handler in _removed.Current) _removed.Remove(handler);
        foreach (var handler in _painted.Current) _painted.Remove(handler);
        foreach (var handler in _renamed.Current) _renamed.Remove(handler);
        foreach (var handler in _moved.Current) _moved.Remove(handler);
        foreach (var handler in _settingChanged.Current) _settingChanged.Remove(handler);
    }

    private void TakePending()
    {
        (_batch, _pending) = (_pending, _batch);
        _pending.Clear();
        _next = 0;
        _removedInBatch.Clear();
        _lastStateInBatch.Clear();
        _lastSettingInBatch.Clear();
        _registeredInBatch.Clear();
        _folded.Clear();
        for (var i = 0; i < _batch.Count; i++)
            switch (_batch[i])
            {
                case WorldChange.Registered registered:
                    _registeredInBatch[registered.Info.Ref.Id] = i;
                    _folded[registered.Info.Ref.Id] = registered.Info;
                    break;
                case WorldChange.Deregistered gone:
                    _removedInBatch.Add(gone.Structure.Id);
                    break;
                case WorldChange.StateSet set:
                    _lastStateInBatch[set.Structure.Id] = i;
                    break;
                case WorldChange.Painted painted:
                    if (_folded.TryGetValue(painted.Structure.Id, out var unpainted))
                        _folded[painted.Structure.Id] = unpainted with { Colour = Option.Some(painted.Colour) };
                    break;
                case WorldChange.Renamed renamed:
                    if (_folded.TryGetValue(renamed.Structure.Id, out var unnamed))
                        _folded[renamed.Structure.Id] = unnamed with { Name = Option.Some(renamed.Name) };
                    break;
                case WorldChange.SettingSet setting:
                    _lastSettingInBatch[setting.Structure.Id] = i;
                    if (_folded.TryGetValue(setting.Structure.Id, out var unset))
                        _folded[setting.Structure.Id] = unset with { Setting = Option.Some(setting.Setting) };
                    break;
            }
    }

    /// <summary>Whether the change at <paramref name="at"/> folds into a registration of the same batch.</summary>
    private bool Folds(ReferenceId id, int at) =>
        _registeredInBatch.TryGetValue(id, out var registered) && registered < at && !_removedInBatch.Contains(id);

    private StructureInfo Registration(WorldChange.Registered registered, int at)
    {
        var id = registered.Info.Ref.Id;
        if (_removedInBatch.Contains(id) || _registeredInBatch[id] != at)
            return registered.Info;
        var info = _folded[id];
        if (_lastStateInBatch.TryGetValue(id, out var last) && last > at
            && _batch[last] is WorldChange.StateSet set
            && (set.State is not BuildState.Broken || _stands(id)))
            info = info with { State = set.State };
        return info;
    }

    private void RaiseBatch()
    {
        while (_next < _batch.Count && !_ended)
        {
            var at = _next++;
            Raise(_batch[at], at);
        }

        if (_moves.Count > 0 && !_ended)
        {
            var moves = ValueList.From(_moves);
            _moves.Clear();
            foreach (var handler in _moved.Current)
                handler(moves);
        }

        if (_next >= _batch.Count)
        {
            _batch.Clear();
            _next = 0;
        }
    }

    private void Raise(WorldChange change, int at)
    {
        switch (change)
        {
            case WorldChange.Registered registered:
                var info = Registration(registered, at);
                foreach (var handler in _registered.Current)
                    handler(info);
                break;
            case WorldChange.StateSet set:
                if (_removedInBatch.Contains(set.Structure.Id) || Folds(set.Structure.Id, at)
                    || !_lastStateInBatch.TryGetValue(set.Structure.Id, out var last) || last != at
                    || (set.State is BuildState.Broken && !_stands(set.Structure.Id)))
                    break;
                foreach (var handler in _stateChanged.Current)
                    handler(set.Structure, set.State);
                break;
            case WorldChange.Deregistered gone:
                _detached.Remove(gone.Structure.Id);
                foreach (var handler in _removed.Current)
                    handler(gone.Structure);
                break;
            case WorldChange.Painted painted:
                if (_removedInBatch.Contains(painted.Structure.Id) || Folds(painted.Structure.Id, at))
                    break;
                foreach (var handler in _painted.Current)
                    handler(painted.Structure, painted.Colour);
                break;
            case WorldChange.Renamed renamed:
                if (_removedInBatch.Contains(renamed.Structure.Id) || Folds(renamed.Structure.Id, at))
                    break;
                foreach (var handler in _renamed.Current)
                    handler(renamed.Structure, renamed.Name);
                break;
            case WorldChange.SettingSet setting:
                if (_removedInBatch.Contains(setting.Structure.Id) || Folds(setting.Structure.Id, at)
                    || !_lastSettingInBatch.TryGetValue(setting.Structure.Id, out var lastSetting) || lastSetting != at)
                    break;
                foreach (var handler in _settingChanged.Current)
                    handler(setting.Structure, setting.Setting);
                break;
            case WorldChange.Detached detached:
                _detached[detached.Structure.Id] = detached.Structure;
                break;
            case WorldChange.Attached attached:
                if (_detached.TryGetValue(attached.Id, out var from))
                {
                    _detached.Remove(attached.Id);
                    _moves.Add(new StructureMove(from, attached.To, attached.Turn));
                }

                break;
            default:
                break;
        }
    }
}
