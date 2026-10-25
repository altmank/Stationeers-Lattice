using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Core.Board;

/// <summary>The last change a command made to a ghost or plan: at which revision, by whom.</summary>
internal readonly record struct LastChange(Revision At, Actor By);

/// <summary>
/// The change sets commands made on this board, for undo: each by its id, and per ghost and per plan the last command
/// change, so a revert can tell whether another actor changed them since. Marks Lattice makes from the world are not
/// command changes and are not kept here. Held in memory for the world; constant cost per lookup, linear in a change
/// set's size per record.
/// </summary>
internal sealed class ChangeHistory
{
    private readonly Dictionary<ChangeSetId, ChangeSet> _sets = new();
    private readonly Dictionary<GhostId, LastChange> _ghosts = new();
    private readonly Dictionary<PlanId, LastChange> _plans = new();

    public void Record(ChangeSet changes)
    {
        _sets[changes.Id] = changes;
        var last = new LastChange(changes.Revision, changes.By);
        foreach (var ghost in changes.Added)
            _ghosts[ghost.Id] = last;
        foreach (var change in changes.Changed)
            _ghosts[change.After.Id] = last;
        foreach (var removal in changes.Removed)
            _ghosts[removal.Ghost.Id] = last;
        foreach (var plan in changes.PlansOpened)
            _plans[plan.Id] = last;
        foreach (var change in changes.PlansChanged)
            _plans[change.After.Id] = last;
    }

    /// <summary>Forgets every change set (a client's board replaced by the host's at join).</summary>
    public void Clear()
    {
        _sets.Clear();
        _ghosts.Clear();
        _plans.Clear();
    }

    public Option<ChangeSet> Find(ChangeSetId id) => _sets.TryGetValue(id, out var changes) ? Option.Some(changes) : Option<ChangeSet>.None;

    public Option<LastChange> Last(GhostId ghost) => _ghosts.TryGetValue(ghost, out var last) ? Option.Some(last) : Option<LastChange>.None;

    public Option<LastChange> Last(PlanId plan) => _plans.TryGetValue(plan, out var last) ? Option.Some(last) : Option<LastChange>.None;
}

/// <summary>An actor as refusal facts name it: <c>player:&lt;client&gt;</c>, <c>agent:&lt;id&gt;</c>, <c>mod:&lt;id&gt;</c>.</summary>
internal static class ActorText
{
    public static string Of(Actor actor) => actor switch
    {
        Actor.Player player => "player:" + player.Client.Value,
        Actor.Agent agent => "agent:" + agent.Id.Value,
        Actor.Mod mod => "mod:" + mod.Id.Value,
        _ => actor.GetType().Name,
    };
}
