using System;

namespace Lattice.Api;

/// <summary>What one applied command did: the unit of sync, events and undo.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Command">The command that made it.</param>
/// <param name="By">The actor.</param>
/// <param name="Revision">The board revision after it.</param>
/// <param name="At">When it was applied, real time.</param>
/// <param name="Added">New ghosts.</param>
/// <param name="Changed">Ghosts before and after.</param>
/// <param name="Removed">Ghosts that left the board, and why.</param>
/// <param name="PlansOpened">Plans created or reopened.</param>
/// <param name="PlansChanged">Plans before and after.</param>
/// <param name="PlansFinished">Plans finished.</param>
public sealed record ChangeSet(
    ChangeSetId Id,
    CommandId Command,
    Actor By,
    Revision Revision,
    DateTimeOffset At,
    ValueList<Ghost> Added,
    ValueList<GhostChange> Changed,
    ValueList<GhostRemoval> Removed,
    ValueList<Plan> PlansOpened,
    ValueList<PlanChange> PlansChanged,
    ValueList<PlanId> PlansFinished)
{
    /// <summary>
    /// The done counts the set gives plans outright (a catch-up or a reopen), each plan's count after the set; a ghost
    /// removed as done is counted, not listed here. Lattice's own: sync carries it so every seat counts the same.
    /// </summary>
    internal ValueMap<PlanId, int> DoneSet { get; init; } = ValueMap<PlanId, int>.Empty;

    /// <summary>
    /// Of <see cref="DoneSet"/>, each plan's done ghosts per executor that held their claim; a plan in it not listed here
    /// has none.
    /// </summary>
    internal ValueMap<PlanId, ValueMap<ExecutorId, int>> DoneBySet { get; init; } = ValueMap<PlanId, ValueMap<ExecutorId, int>>.Empty;
}

/// <summary>A ghost before and after a change.</summary>
/// <param name="Before">As it was.</param>
/// <param name="After">As it is.</param>
public sealed record GhostChange(Ghost Before, Ghost After);

/// <summary>A plan before and after a change.</summary>
/// <param name="Before">As it was.</param>
/// <param name="After">As it is.</param>
public sealed record PlanChange(Plan Before, Plan After);

/// <summary>A ghost that left the board, and why.</summary>
/// <param name="Ghost">The ghost as it was.</param>
/// <param name="Cause">Why it left.</param>
public sealed record GhostRemoval(Ghost Ghost, RemovalCause Cause);

/// <summary>Why a ghost left the board.</summary>
public abstract record RemovalCause
{
    private RemovalCause() { }

    /// <summary>A command removed it.</summary>
    public sealed record Removed : RemovalCause;

    /// <summary>A new ghost in its spot replaced it.</summary>
    /// <param name="By">The new ghost.</param>
    public sealed record Replaced(GhostId By) : RemovalCause;

    /// <summary>The world now matches it.</summary>
    /// <param name="How">The completion.</param>
    public sealed record Done(Completion How) : RemovalCause;

    /// <summary>The structure it pointed at is gone (destroyed while playing, or missing at load).</summary>
    public sealed record TargetGone : RemovalCause;
}

/// <summary>The world matching a ghost.</summary>
/// <param name="Built">The structure that matches it, for a build, swap or finish ghost.</param>
/// <param name="At">When the match was seen, real time.</param>
public sealed record Completion(Option<StructureRef> Built, DateTimeOffset At)
{
    /// <summary>The executor that held the ghost's claim when it completed; none: unclaimed (a player). Added in contract 0.22.</summary>
    public Option<ExecutorId> By { get; init; }
}

/// <summary>A command's outcome: applied, or refused with reasons.</summary>
public abstract record Outcome
{
    private Outcome() { }

    /// <summary>Applied.</summary>
    /// <param name="Changes">What it did.</param>
    public sealed record Applied(ChangeSet Changes) : Outcome;

    /// <summary>Refused; nothing changed.</summary>
    /// <param name="Why">The refusals.</param>
    public sealed record Refused(ValueList<Refusal> Why) : Outcome;
}

/// <summary>One reason a command was refused, and the ghosts it concerns.</summary>
/// <param name="Why">The reason.</param>
/// <param name="Ghosts">The ghosts it concerns; empty when it concerns the whole command.</param>
public sealed record Refusal(Reason Why, ValueList<GhostId> Ghosts);

/// <summary>What submitting a command gave back right away.</summary>
public abstract record Submission
{
    private Submission() { }

    /// <summary>On the host: applied or refused at once.</summary>
    /// <param name="Ticket">The command's ticket.</param>
    /// <param name="Outcome">The outcome.</param>
    public sealed record Answered(Ticket Ticket, Outcome Outcome) : Submission;

    /// <summary>On a client: sent to the host; the outcome arrives as <see cref="IGhostEvents.Answered"/>.</summary>
    /// <param name="Ticket">The command's ticket.</param>
    public sealed record Sent(Ticket Ticket) : Submission;
}

/// <summary>
/// Changes the board. Main thread. A command submitted from inside a Lattice callback is queued and applied after the
/// current one, never nested. A command still being worked out in slices when the world ends is dropped with the world:
/// its <see cref="IGhostEvents.Answered"/> never comes. Lattice implements this; consumers call it. A minor may add
/// members: to fake it in a test, inherit <c>Lattice.Testing.Stubs.StubCommands</c>, which gains every added member in
/// the same release.
/// </summary>
public interface ICommands
{
    /// <summary>
    /// Submits a command for an actor. On the host it is checked against the access policy and applied at once; on a
    /// client it is sent to the host. The same id sent twice applies once. Cost: the command's expansion and checks.
    /// </summary>
    Submission Submit(CommandId id, Command command, Actor actor);
}
