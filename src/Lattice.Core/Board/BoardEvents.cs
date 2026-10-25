using System;
using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Core.Board;

/// <summary>
/// One event's subscribers as an array copied on every add or remove, so a raise reads the array once, allocates
/// nothing, and is not disturbed by a handler that subscribes or unsubscribes.
/// </summary>
internal sealed class Subscribers<T> where T : Delegate
{
    public T[] Current { get; private set; } = Array.Empty<T>();

    public void Add(T? handler)
    {
        if (handler is null)
            return;
        var next = new T[Current.Length + 1];
        Array.Copy(Current, next, Current.Length);
        next[Current.Length] = handler;
        Current = next;
    }

    /// <summary>Removes the last subscription of the handler, as a multicast delegate would.</summary>
    public void Remove(T? handler)
    {
        if (handler is null)
            return;
        var at = Array.LastIndexOf(Current, handler);
        if (at < 0)
            return;
        var next = new T[Current.Length - 1];
        Array.Copy(Current, 0, next, 0, at);
        Array.Copy(Current, at + 1, next, at, Current.Length - at - 1);
        Current = next;
    }
}

/// <summary>An event waiting for the next burst.</summary>
internal abstract class QueuedEvent
{
    public abstract void Raise(BoardEvents events);
}

/// <summary>
/// The world's events. Changes queue their events as they commit; <see cref="Flush"/>, called once per frame on the
/// main thread, raises in revision order every event queued before it began. Events queued while it raises wait for
/// the next flush, so nothing a handler causes is raised inside the burst that caused it. Handlers are called plainly:
/// an exception leaves <see cref="Flush"/>, and the events after it are raised by the next flush.
/// </summary>
internal sealed class BoardEvents : IGhostEvents
{
    private readonly Subscribers<Action<ChangeSet>> _changed = new();
    private readonly Subscribers<Action<GhostId, Completion>> _completed = new();
    private readonly Subscribers<Action<GhostId>> _statusChanged = new();
    private readonly Subscribers<Action<Ticket, Outcome>> _answered = new();
    private readonly Subscribers<Action<PlanId>> _planStateChanged = new();
    private readonly Subscribers<Action<Maker, ValueList<BlockedGhost>>> _blocked = new();
    private readonly Subscribers<Action<ValueList<StructureMove>>> _structuresMoved = new();
    private readonly Subscribers<Action<Revision>> _answersReady = new();
    private readonly Subscribers<Action<LoadReport>> _loaded = new();
    private readonly Subscribers<Action> _boardReset = new();

    private readonly List<QueuedEvent> _queue = new(64);
    private int _next;

    public event Action<ChangeSet> Changed { add => _changed.Add(value); remove => _changed.Remove(value); }

    public event Action<GhostId, Completion> Completed { add => _completed.Add(value); remove => _completed.Remove(value); }

    public event Action<GhostId> StatusChanged { add => _statusChanged.Add(value); remove => _statusChanged.Remove(value); }

    public event Action<Ticket, Outcome> Answered { add => _answered.Add(value); remove => _answered.Remove(value); }

    public event Action<PlanId> PlanStateChanged { add => _planStateChanged.Add(value); remove => _planStateChanged.Remove(value); }

    public event Action<Maker, ValueList<BlockedGhost>> Blocked { add => _blocked.Add(value); remove => _blocked.Remove(value); }

    public event Action<ValueList<StructureMove>> StructuresMoved { add => _structuresMoved.Add(value); remove => _structuresMoved.Remove(value); }

    public event Action<Revision> AnswersReady { add => _answersReady.Add(value); remove => _answersReady.Remove(value); }

    public event Action<LoadReport> Loaded { add => _loaded.Add(value); remove => _loaded.Remove(value); }

    public event Action BoardReset { add => _boardReset.Add(value); remove => _boardReset.Remove(value); }

    /// <summary>Events waiting for the next flush.</summary>
    public int Waiting => _queue.Count - _next;

    public void Queue(QueuedEvent queued) => _queue.Add(queued);

    /// <summary>Raises every event queued before this call, in the order queued. Linear in them; allocates nothing.</summary>
    public void Flush()
    {
        var end = _queue.Count;
        while (_next < end)
            _queue[_next++].Raise(this);
        _queue.RemoveRange(0, _next);
        _next = 0;
    }

    internal void RaiseChanged(ChangeSet changes)
    {
        var handlers = _changed.Current;
        for (var i = 0; i < handlers.Length; i++)
            handlers[i](changes);
    }

    internal void RaiseCompleted(GhostId ghost, Completion how)
    {
        var handlers = _completed.Current;
        for (var i = 0; i < handlers.Length; i++)
            handlers[i](ghost, how);
    }

    internal void RaisePlanStateChanged(PlanId plan)
    {
        var handlers = _planStateChanged.Current;
        for (var i = 0; i < handlers.Length; i++)
            handlers[i](plan);
    }

    internal void RaiseAnswered(Ticket ticket, Outcome outcome)
    {
        var handlers = _answered.Current;
        for (var i = 0; i < handlers.Length; i++)
            handlers[i](ticket, outcome);
    }

    internal void RaiseAnswersReady(Revision at)
    {
        var handlers = _answersReady.Current;
        for (var i = 0; i < handlers.Length; i++)
            handlers[i](at);
    }

    internal void RaiseStatusChanged(GhostId ghost)
    {
        var handlers = _statusChanged.Current;
        for (var i = 0; i < handlers.Length; i++)
            handlers[i](ghost);
    }

    internal void RaiseBlocked(Maker maker, ValueList<BlockedGhost> ghosts)
    {
        var handlers = _blocked.Current;
        for (var i = 0; i < handlers.Length; i++)
            handlers[i](maker, ghosts);
    }

    internal void RaiseBoardReset()
    {
        var handlers = _boardReset.Current;
        for (var i = 0; i < handlers.Length; i++)
            handlers[i]();
    }
}

internal sealed class ChangedEvent(ChangeSet changes) : QueuedEvent
{
    public override void Raise(BoardEvents events) => events.RaiseChanged(changes);
}

internal sealed class CompletedEvent(GhostId ghost, Completion how) : QueuedEvent
{
    public override void Raise(BoardEvents events) => events.RaiseCompleted(ghost, how);
}

internal sealed class BoardResetEvent : QueuedEvent
{
    public override void Raise(BoardEvents events) => events.RaiseBoardReset();
}

internal sealed class AnsweredEvent(Ticket ticket, Outcome outcome) : QueuedEvent
{
    public override void Raise(BoardEvents events) => events.RaiseAnswered(ticket, outcome);
}

internal sealed class PlanStateChangedEvent(PlanId plan) : QueuedEvent
{
    public override void Raise(BoardEvents events) => events.RaisePlanStateChanged(plan);
}

internal sealed class AnswersReadyEvent(Revision at) : QueuedEvent
{
    public override void Raise(BoardEvents events) => events.RaiseAnswersReady(at);
}
