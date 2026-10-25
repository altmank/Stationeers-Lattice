using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;

namespace Lattice.Core.Executors;

/// <summary>
/// Status changes and blocked ghosts waiting for the next burst. The first of either queues one event on the board's
/// events; when raised it drains what is pending: one <see cref="IGhostEvents.StatusChanged"/> per ghost, then one
/// <see cref="IGhostEvents.Blocked"/> per maker with that maker's ghosts in the order they were blocked. A ghost
/// blocked twice before the burst carries its latest reason; a ghost that left the board before the burst is not told.
/// What a handler causes waits for the next burst; a handler that throws leaves the burst, and what it had not raised
/// goes to the next one.
/// </summary>
internal sealed class ExecutorNotices(GhostBoard board)
{
    private readonly List<GhostId> _statusOrder = new(16);
    private readonly HashSet<GhostId> _status = new();
    private readonly List<BlockedGhost> _blocked = new(16);
    private readonly Dictionary<GhostId, int> _blockedAt = new();
    private bool _queued;

    public void StatusChanged(GhostId ghost)
    {
        if (_status.Add(ghost))
            _statusOrder.Add(ghost);
        Queue();
    }

    public void Blocked(GhostId ghost, Reason why)
    {
        var told = new BlockedGhost(ghost, why);
        if (_blockedAt.TryGetValue(ghost, out var at))
            _blocked[at] = told;
        else
        {
            _blockedAt.Add(ghost, _blocked.Count);
            _blocked.Add(told);
        }

        Queue();
    }

    private void Queue()
    {
        if (_queued)
            return;
        _queued = true;
        board.Events.Queue(new Burst(this));
    }

    private void Raise(BoardEvents events)
    {
        _queued = false;
        var changed = _statusOrder.ToArray();
        _statusOrder.Clear();
        _status.Clear();
        var blocked = Grouped();
        var raised = 0;
        try
        {
            for (; raised < changed.Length; raised++)
                events.RaiseStatusChanged(changed[raised]);
            for (; raised < changed.Length + blocked.Count; raised++)
            {
                var (maker, ghosts) = blocked[raised - changed.Length];
                events.RaiseBlocked(maker, ValueList.From(ghosts));
            }
        }
        finally
        {
            if (raised < changed.Length + blocked.Count)
                PutBack(changed, blocked, raised + 1);
        }
    }

    /// <summary>A handler threw: what the burst had not raised yet goes to the next burst.</summary>
    private void PutBack(GhostId[] changed, List<(Maker Maker, List<BlockedGhost> Ghosts)> blocked, int from)
    {
        for (var i = from; i < changed.Length; i++)
            StatusChanged(changed[i]);
        for (var i = from > changed.Length ? from - changed.Length : 0; i < blocked.Count; i++)
            foreach (var told in blocked[i].Ghosts)
                Blocked(told.Ghost, told.Why);
    }

    private List<(Maker Maker, List<BlockedGhost> Ghosts)> Grouped()
    {
        var grouped = new List<(Maker, List<BlockedGhost>)>(_blocked.Count == 0 ? 0 : 4);
        var byMaker = new Dictionary<Maker, List<BlockedGhost>>();
        foreach (var told in _blocked)
        {
            if (!board.Index.Ghosts.TryGetValue(told.Ghost, out var entry))
                continue;
            var maker = entry.Ghost.MadeBy;
            if (!byMaker.TryGetValue(maker, out var ghosts))
            {
                byMaker.Add(maker, ghosts = new List<BlockedGhost>(4));
                grouped.Add((maker, ghosts));
            }

            ghosts.Add(told);
        }

        _blocked.Clear();
        _blockedAt.Clear();
        return grouped;
    }

    private sealed class Burst(ExecutorNotices notices) : QueuedEvent
    {
        public override void Raise(BoardEvents events) => notices.Raise(events);
    }
}
