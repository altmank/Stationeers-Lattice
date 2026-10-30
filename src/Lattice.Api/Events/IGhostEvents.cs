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
/// values. Subscriptions end with the world. A handler's failure is its own mod's to handle: one that throws is logged
/// and skipped, and every other handler of that event still runs. Lattice implements this;
/// consumers call it. A minor may add members: to fake it in a test, inherit
/// <c>Lattice.Testing.Stubs.StubGhostEvents</c>, which gains every added member in the same release.
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

    /// <summary>
    /// A ghost's claim or an executor's status on it changed: a claim taken, released, lapsed or dropped, or a changed
    /// report; not a renewal. On the host and on clients. Until contract 0.22 it was raised only for a report, or a drop
    /// of a claim that had one.
    /// </summary>
    event Action<GhostId> StatusChanged;

    /// <summary>
    /// A builder registered, went, or changed what it says (area, word, supply, an answer). One per builder per burst,
    /// on every seat. Added in contract 0.22.
    /// </summary>
    event Action<ExecutorId> BuilderChanged;

    /// <summary>
    /// A command this client sent was applied or refused by the host. Raised once for every command sent, also across a
    /// resync; a command whose outcome the host no longer has is refused <see cref="ReasonCodes.OutcomeLost"/>.
    /// </summary>
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
