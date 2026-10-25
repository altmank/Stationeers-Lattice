using System;

namespace Lattice.Api;

/// <summary>A ghost that cannot proceed, and why; told to the ghost's maker.</summary>
/// <param name="Ghost">The ghost.</param>
/// <param name="Why">The reason.</param>
public sealed record BlockedGhost(GhostId Ghost, Reason Why);

/// <summary>
/// What happened on the board. Every event is raised on the main thread, in revision order, batched to at most one
/// burst per frame, to each subscriber in subscription order. A command submitted from a handler is queued and applied
/// after the current burst, never nested; reads in a handler see the board as of the event. Handlers receive immutable
/// values. Subscriptions end with the world. A handler's failure is its own mod's to handle.
/// Lattice implements this; consumers call it. A minor may add members, so consumer fakes of it are not covered by
/// compatibility.
/// </summary>
public interface IGhostEvents
{
    /// <summary>
    /// A change was applied: one event per change set. It carries every change this process holds, whatever the access
    /// policy lets a viewer see; to show what a viewer may see, read through <see cref="IGhostSystem.Board"/> or
    /// <see cref="IGhostBoard.As"/>.
    /// </summary>
    event Action<ChangeSet> Changed;

    /// <summary>The world now matches a ghost.</summary>
    event Action<GhostId, Completion> Completed;

    /// <summary>An executor's status on a ghost changed.</summary>
    event Action<GhostId> StatusChanged;

    /// <summary>A command this client sent was applied or refused by the host.</summary>
    event Action<Ticket, Outcome> Answered;

    /// <summary>A plan's state changed.</summary>
    event Action<PlanId> PlanStateChanged;

    /// <summary>A maker's ghosts cannot proceed (the reason is on each).</summary>
    event Action<Maker, ValueList<BlockedGhost>> Blocked;

    /// <summary>The game moved structures (rocket parts detached and re-attached, for example).</summary>
    event Action<ValueList<StructureMove>> StructuresMoved;

    /// <summary>Answers that were pending for a revision (validation, plan facts, previews) are ready: ask again.</summary>
    event Action<Revision> AnswersReady;

    /// <summary>A world finished loading: whether the save had a record and which plans are ahead of it. Raised once per load.</summary>
    event Action<LoadReport> Loaded;

    /// <summary>
    /// The board was replaced or the world was left; every earlier answer is stale. When the world ends it is raised
    /// after <see cref="LatticeSystem.Current"/> is cleared: handlers see none, and the system they hold is stale.
    /// </summary>
    event Action BoardReset;
}
