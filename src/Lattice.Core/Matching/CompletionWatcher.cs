using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.BuiltWorld;
using Lattice.Core.Ports;
using Lattice.Core.Slices;

namespace Lattice.Core.Matching;

/// <summary>What the pass at load found: how many ghosts it judged and what became of them, and the pieces that match in any turn.</summary>
internal sealed record MatchReport(int Judged, int Done, int Started, int Stale, int TargetGone, int Unjudged, ValueList<PrefabKey> TurnFree);

/// <summary>
/// Tells from the world when a ghost is done, started or stale, whoever did the work. <see cref="Load"/> makes one
/// pass over every ghost against the world mirror; after it only changes are judged: each mirror change re-judges the
/// ghosts on that structure or in its spots (as it was and as it is), and each board change made by a command judges
/// the ghosts it added or changed. Nothing here reads the world: the mirror is the only source.
/// <para>
/// The ghosts a board change or a mirror change touches are judged in the frame scheduler's checking slices,
/// <see cref="JudgedPerStep"/> a step, in the order their changes came and by id within one (a
/// 5,000-ghost plan landing, or a base's worth of structures appearing, never stalls the frame); the handlers
/// only note them, and a ghost noted again before its turn is judged once, against the mirror as it then stands.
/// <see cref="CatchUp"/> judges every noted ghost at once. Without a scheduler they are judged in the handler.
/// </para>
/// <para>
/// A verdict reaches the board as Lattice's own mark (<see cref="GhostBoard.MarkStarted"/>, <see cref="GhostBoard.MarkStale"/>,
/// <see cref="GhostBoard.MarkPlanned"/>, <see cref="GhostBoard.Complete"/>, <see cref="GhostBoard.DropTargetGone"/>), only
/// when it changes the ghost's state, so each is one change set and its event. Main thread; a mirror change costs time
/// linear in the ghosts it touches, a board change constant time per ghost in the handler.
/// </para>
/// </summary>
internal sealed class CompletionWatcher
{
    private readonly GhostBoard _board;
    private readonly WorldMirror _world;
    private readonly Matcher _matcher;
    private readonly TurnFreePieces _turnFree;
    private readonly ITimeSource _time;
    private readonly Action<MirrorChange> _onWorld;
    private readonly Action<ChangeSet> _onBoard;
    private readonly HashSet<GhostId> _touched = new();
    private readonly List<GhostId> _order = new(8);
    private readonly FrameScheduler? _scheduler;
    private readonly Queue<GhostId> _waiting = new();
    private readonly HashSet<GhostId> _waits = new();
    private readonly Work _work;
    private bool _working;
    private bool _watching;

    public CompletionWatcher(GhostBoard board, WorldMirror world, TurnFreePieces turnFree, ITimeSource time, FrameScheduler? scheduler = null)
    {
        _board = board;
        _world = world;
        _turnFree = turnFree;
        _time = time;
        _scheduler = scheduler;
        _work = new Work(this);
        _matcher = new Matcher(world, turnFree);
        _onWorld = OnWorld;
        _onBoard = OnBoard;
    }

    /// <summary>
    /// The pass at load: every ghost on the board judged once, in id order, then watching starts. Call it once the
    /// mirror is started. Once per world; a second call is a bug and throws. Linear in the ghosts.
    /// </summary>
    public MatchReport Load()
    {
        if (_watching)
            throw new InvalidOperationException("Matching is already watching this world.");

        var ids = new List<GhostId>(_board.Index.Ghosts.Keys);
        ids.Sort(static (a, b) => a.Value.CompareTo(b.Value));
        var (done, started, stale, gone, unjudged) = (0, 0, 0, 0, 0);
        foreach (var id in ids)
        {
            var verdict = Judge(id, Moment.Load);
            switch (verdict)
            {
                case Verdict.Done: done++; break;
                case Verdict.Started: started++; break;
                case Verdict.Stale: stale++; break;
                case Verdict.TargetGone: gone++; break;
                case Verdict.Unjudged: unjudged++; break;
            }
        }

        _world.Changed += _onWorld;
        _board.Events.Changed += _onBoard;
        _watching = true;
        return new MatchReport(ids.Count, done, started, stale, gone, unjudged, _turnFree.Report());
    }

    /// <summary>Ghosts of a board change judged in one checking step.</summary>
    public const int JudgedPerStep = 16;

    /// <summary>Ghosts a board or mirror change touched that wait to be judged.</summary>
    public int Waiting => _waiting.Count;

    /// <summary>Judges every waiting ghost now, as the slices would have. Linear in the ghosts waiting.</summary>
    public void CatchUp()
    {
        while (_watching && _waiting.Count > 0)
            JudgeWaiting();
    }

    /// <summary>The world ends: no more judging.</summary>
    public void End()
    {
        _world.Changed -= _onWorld;
        _board.Events.Changed -= _onBoard;
        _watching = false;
        _waiting.Clear();
        _waits.Clear();
    }

    private void OnWorld(MirrorChange change)
    {
        switch (change)
        {
            case MirrorChange.Appeared appeared:
                Touch(appeared.Now);
                break;
            case MirrorChange.Altered altered:
                Touch(altered.Before);
                Touch(altered.Now);
                break;
            case MirrorChange.Gone gone:
                Touch(gone.Before);
                break;
        }

        Noted();
    }

    private void OnBoard(ChangeSet changes)
    {
        if (changes.By == LatticeIdentity.Actor)
            return;
        foreach (var ghost in changes.Added)
            Touch(ghost.Id);
        foreach (var change in changes.Changed)
            Touch(change.After.Id);
        Noted();
    }

    // The touched ghosts join the waiting ones, by id, for the checking slices; judged here when there are no slices.
    private void Noted()
    {
        if (_scheduler is null)
        {
            JudgeTouched();
            return;
        }

        _order.Sort(static (a, b) => a.Value.CompareTo(b.Value));
        for (var i = 0; i < _order.Count; i++)
            if (_waits.Add(_order[i]))
                _waiting.Enqueue(_order[i]);
        _order.Clear();
        _touched.Clear();
        if (_working || _waiting.Count == 0)
            return;
        _working = true;
        _scheduler.Schedule(WorkKind.Checking, _work, Priority.Normal);
    }

    private StepOutcome JudgeWaiting()
    {
        for (var i = 0; i < JudgedPerStep && _watching && _waiting.Count > 0; i++)
        {
            var ghost = _waiting.Dequeue();
            _waits.Remove(ghost);
            Judge(ghost, Moment.Live);
        }

        if (_watching && _waiting.Count > 0)
            return StepOutcome.More;
        _working = false;
        return StepOutcome.Done;
    }

    private void Touch(StructureInfo structure)
    {
        foreach (var ghost in _board.Index.OnStructure(structure.Ref))
            Touch(ghost);
        if (_world.SpotsOf(structure).TryGet(out var spots))
        {
            for (var i = 0; i < spots.Count; i++)
                if (_board.Index.TryOccupant(spots[i], out var ghost))
                    Touch(ghost);
        }
        else
            foreach (var ghost in _board.Index.InCell(structure.Ref.At))
                Touch(ghost);
    }

    private void Touch(GhostId ghost)
    {
        if (_touched.Add(ghost))
            _order.Add(ghost);
    }

    private void JudgeTouched()
    {
        _order.Sort(static (a, b) => a.Value.CompareTo(b.Value));
        for (var i = 0; i < _order.Count; i++)
            Judge(_order[i], Moment.Live);
        _order.Clear();
        _touched.Clear();
    }

    private Verdict? Judge(GhostId id, Moment moment)
    {
        if (!_board.Index.Ghosts.TryGetValue(id, out var entry))
            return null;
        var verdict = _matcher.Judge(entry, moment);
        var state = entry.Ghost.State;
        switch (verdict)
        {
            case Verdict.Planned when state is not GhostState.Planned:
                _board.MarkPlanned(id);
                break;
            case Verdict.Started started when state != new GhostState.Started(started.On):
                _board.MarkStarted(id, started.On);
                break;
            case Verdict.Stale stale when state != new GhostState.Stale(stale.Why):
                _board.MarkStale(id, stale.Why);
                break;
            case Verdict.Done done:
                _board.Complete(id, new Completion(done.Built, _time.Now));
                break;
            case Verdict.TargetGone:
                _board.DropTargetGone(id);
                break;
        }

        return verdict;
    }

    private sealed class Work(CompletionWatcher watcher) : ISlicedWork
    {
        public StepOutcome Step() => watcher.JudgeWaiting();
    }
}
