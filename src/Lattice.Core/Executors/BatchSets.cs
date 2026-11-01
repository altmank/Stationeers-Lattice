using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;

namespace Lattice.Core.Executors;

/// <summary>
/// The builders' batch sets on the host, by builder id, whether or not the builder is registered now: the ghosts each
/// builder chose for its batch (<see cref="IExecutorHandle.SetBatch"/>), kept on the board as it moves. A ghost leaves
/// every set in the change set that takes it off the board; a ghost the change set replaces is followed by the ghosts
/// now holding its spots and target (<see cref="GhostBoard.HoldersOf"/>), the board's own replacement rule. Saved with
/// the world (<see cref="Saved"/>, <see cref="Restore"/>). Each set's size is told to <see cref="Sized"/> as it
/// changes. Main thread.
/// </summary>
internal sealed class BatchSets
{
    /// <summary>The most ghosts one set may hold.</summary>
    public const int MostGhosts = 16384;

    private readonly GhostBoard _board;
    private readonly Dictionary<ExecutorId, Set> _sets = new();
    private readonly Action<ChangeSet> _onCommitted;

    public BatchSets(GhostBoard board)
    {
        _board = board;
        _onCommitted = OnCommitted;
        board.Committed += _onCommitted;
    }

    /// <summary>Called with a builder and its set's size each time the size changes.</summary>
    public Action<ExecutorId, int>? Sized { get; set; }

    /// <summary>
    /// Sets a builder's batch: refused <see cref="ReasonCodes.TooLarge"/> past <see cref="MostGhosts"/>; ghosts not on
    /// the board are left out; empty ends it. Linear in the set.
    /// </summary>
    public Result<Unit, Reason> Put(ExecutorId id, ValueSet<GhostId> ghosts)
    {
        if (ghosts is null)
            return Result<Unit, Reason>.Error(Reason.InvalidValue("batch", "null", "not-null"));
        if (ghosts.Count > MostGhosts)
            return Result<Unit, Reason>.Error(Reason.Of(ReasonCodes.TooLarge, WorldExecutors.Rule, ValueMap
                .Of("list", "batch")
                .With("count", ghosts.Count.ToString())
                .With("most", MostGhosts.ToString())));
        var kept = new HashSet<GhostId>();
        foreach (var ghost in ghosts)
            if (_board.Index.Ghosts.ContainsKey(ghost))
                kept.Add(ghost);
        Replace(id, kept);
        return Result<Unit, Reason>.Ok(Unit.Value);
    }

    /// <summary>A builder's set now; empty with none. Allocates nothing while unchanged.</summary>
    public ValueSet<GhostId> Of(ExecutorId id) =>
        _sets.TryGetValue(id, out var set) ? set.Read ??= ValueSet.From(set.Ghosts) : ValueSet<GhostId>.Empty;

    /// <summary>The size of a builder's set; 0 with none.</summary>
    public int Left(ExecutorId id) => _sets.TryGetValue(id, out var set) ? set.Ghosts.Count : 0;

    /// <summary>Every set, by builder id, for a save. Linear in the sets.</summary>
    public ValueMap<ExecutorId, ValueSet<GhostId>> Saved()
    {
        if (_sets.Count == 0)
            return ValueMap<ExecutorId, ValueSet<GhostId>>.Empty;
        var saved = new Dictionary<ExecutorId, ValueSet<GhostId>>(_sets.Count);
        foreach (var pair in _sets)
            saved[pair.Key] = Of(pair.Key);
        return ValueMap<ExecutorId, ValueSet<GhostId>>.From(saved);
    }

    /// <summary>The sets a save kept, after a load: each as <see cref="Put"/> takes it, past the cap included.</summary>
    public void Restore(ValueMap<ExecutorId, ValueSet<GhostId>> saved)
    {
        foreach (var pair in saved)
        {
            var kept = new HashSet<GhostId>();
            foreach (var ghost in pair.Value)
                if (_board.Index.Ghosts.ContainsKey(ghost))
                    kept.Add(ghost);
            Replace(pair.Key, kept);
        }
    }

    /// <summary>Ends with the world: the board is no longer watched.</summary>
    public void End() => _board.Committed -= _onCommitted;

    private void Replace(ExecutorId id, HashSet<GhostId> ghosts)
    {
        var before = Left(id);
        if (ghosts.Count == 0)
            _sets.Remove(id);
        else
            _sets[id] = new Set(ghosts);
        if (before != ghosts.Count)
            Sized?.Invoke(id, ghosts.Count);
    }

    // Ghosts that left the board leave every set; a replaced one is followed by what holds its place now.
    private void OnCommitted(ChangeSet changes)
    {
        if (_sets.Count == 0 || changes.Removed.Count == 0)
            return;
        List<ExecutorId>? touched = null;
        foreach (var pair in _sets)
        {
            var set = pair.Value;
            var changed = false;
            foreach (var removal in changes.Removed)
            {
                if (!set.Ghosts.Remove(removal.Ghost.Id))
                    continue;
                changed = true;
                if (removal.Cause is RemovalCause.Replaced)
                    foreach (var holder in _board.HoldersOf(removal.Ghost))
                        set.Ghosts.Add(holder);
            }

            if (!changed)
                continue;
            set.Read = null;
            (touched ??= new List<ExecutorId>()).Add(pair.Key);
        }

        if (touched is null)
            return;
        foreach (var id in touched)
        {
            var left = _sets[id].Ghosts.Count;
            if (left == 0)
                _sets.Remove(id);
            Sized?.Invoke(id, left);
        }
    }

    private sealed class Set(HashSet<GhostId> ghosts)
    {
        public HashSet<GhostId> Ghosts { get; } = ghosts;

        public ValueSet<GhostId>? Read { get; set; }
    }
}
