using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Sync;

namespace Lattice.Core.Board;

/// <summary>
/// The board on a client, as sync drives it: the host's change sets are committed as they are (the host's ids,
/// revision, ghosts and plans; no access policy asked, the host asked it) and a join replaces the whole board.
/// </summary>
internal sealed partial class GhostBoard : IReplicaBoard
{
    Option<Ghost> IReplicaBoard.Ghost(GhostId id) =>
        Index.Ghosts.TryGetValue(id, out var entry) ? Option.Some(entry.Ghost) : Option<Ghost>.None;

    Option<Plan> IReplicaBoard.Plan(PlanId id) =>
        Index.Plans.TryGetValue(id, out var entry) ? Option.Some(entry.Plan) : Option<Plan>.None;

    /// <summary>
    /// Commits a host change set as it is: one revision, the set itself queued as the Changed event, the index, shapes
    /// and history updated. A set Lattice made from the world (by <see cref="LatticeIdentity.Actor"/>) leaves command
    /// revisions where they were and is not kept for undo, as on the host. Refused <see cref="ReasonCodes.InvalidValue"/>
    /// with nothing changed when its revision is not the board's next or it names a ghost or plan the board does not
    /// hold as the set expects. Linear in the set's size.
    /// </summary>
    public Result<ChangeSet, Reason> Mirror(ChangeSet set)
    {
        if (set.Revision.Value != Revision.Value + 1)
            return Mismatch("revision", set.Revision.Value);

        var pending = new Pending(set.Revision, set.At);
        var byCommand = set.By != LatticeIdentity.Actor;
        var leaving = new HashSet<GhostId>();
        foreach (var removal in set.Removed)
        {
            if (!Index.Ghosts.TryGetValue(removal.Ghost.Id, out var entry))
                return Mismatch("ghost", removal.Ghost.Id.Value);
            pending.Removed.Add((entry, removal.Cause));
            leaving.Add(entry.Id);
        }

        foreach (var change in set.Changed)
        {
            if (!Index.Ghosts.TryGetValue(change.After.Id, out var before) || leaving.Contains(before.Id))
                return Mismatch("ghost", change.After.Id.Value);
            pending.Changed.Add((before, EntryFor(change.After, byCommand ? set.Revision : before.CommandRevision)));
        }

        foreach (var ghost in set.Added)
        {
            if (Index.Ghosts.ContainsKey(ghost.Id) && !leaving.Contains(ghost.Id))
                return Mismatch("ghost", ghost.Id.Value);
            pending.Added.Add(EntryFor(ghost, set.Revision));
        }

        foreach (var plan in set.PlansOpened)
        {
            if (Index.Plans.ContainsKey(plan.Id))
                return Mismatch("plan", plan.Id.Value);
            pending.PlansOpened.Add(plan);
        }

        foreach (var change in set.PlansChanged)
        {
            if (!Index.Plans.ContainsKey(change.After.Id))
                return Mismatch("plan", change.After.Id.Value);
            pending.PlansChanged.Add((change.Before, change.After));
        }

        foreach (var id in set.PlansFinished)
        {
            if (!Index.Plans.TryGetValue(id, out var plan))
                return Mismatch("plan", id.Value);
            pending.PlansFinished.Add(plan);
        }

        Stage(pending, byCommand);
        Revision = set.Revision;
        Epoch++;
        TakeIdsOf(set);
        if (byCommand)
            History.Record(set);
        Events.Queue(new ChangedEvent(set));
        return Result<ChangeSet, Reason>.Ok(set);
    }

    /// <summary>
    /// Replaces every plan, ghost and counter with the host's, forgets the change history and queues the BoardReset
    /// event. Linear in the old and new boards.
    /// </summary>
    public void Reset(BoardSeed seed)
    {
        foreach (var entry in new List<GhostEntry>(Index.Ghosts.Values))
            Index.Remove(entry);
        Index.Plans.Clear();
        History.Clear();
        Load(seed);
        Epoch++;
        Events.Queue(new BoardResetEvent());
    }

    /// <summary>Keeps the counters above every id the host handed out, so the board's next ids stay unused.</summary>
    private void TakeIdsOf(ChangeSet set)
    {
        foreach (var ghost in set.Added)
            _nextGhost = Math.Max(_nextGhost, ghost.Id.Value + 1);
        foreach (var plan in set.PlansOpened)
            _nextPlan = Math.Max(_nextPlan, plan.Id.Value + 1);
        _nextChangeSet = Math.Max(_nextChangeSet, set.Id.Value + 1);
    }

    private static Result<ChangeSet, Reason> Mismatch(string what, long value) =>
        Result<ChangeSet, Reason>.Error(Reason.Of(
            ReasonCodes.InvalidValue, SyncRules.Sync, ValueMap.Of("problem", "not-the-host-board").With(what, value.ToString())));
}
