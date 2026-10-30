using System;
using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Testing.Stubs;

/// <summary>
/// A base for a test fake of <see cref="IGhostEvents"/>: inherit it and override what your test uses. Every
/// member is virtual and throws <see cref="NotSupportedException"/> naming itself until overridden; its events
/// keep their subscribers, and each has a <c>Raise</c> method a test fires it with. A minor that adds a member to
/// <see cref="IGhostEvents"/> adds it here in the same release, so a fake that inherits this keeps compiling and
/// loading.
/// </summary>
public class StubGhostEvents : IGhostEvents
{
    /// <inheritdoc />
    public virtual event Action<ChangeSet> Changed = delegate { };

    /// <summary>Raises <see cref="Changed"/> to its subscribers, as Lattice would.</summary>
    public void RaiseChanged(ChangeSet changes) => Changed.Invoke(changes);

    /// <inheritdoc />
    public virtual event Action<GhostId, Completion> Completed = delegate { };

    /// <summary>Raises <see cref="Completed"/> to its subscribers, as Lattice would.</summary>
    public void RaiseCompleted(GhostId ghost, Completion completion) => Completed.Invoke(ghost, completion);

    /// <inheritdoc />
    public virtual event Action<GhostId> StatusChanged = delegate { };

    /// <summary>Raises <see cref="StatusChanged"/> to its subscribers, as Lattice would.</summary>
    public void RaiseStatusChanged(GhostId ghost) => StatusChanged.Invoke(ghost);

    /// <inheritdoc />
    public virtual event Action<ExecutorId> BuilderChanged = delegate { };

    /// <summary>Raises <see cref="BuilderChanged"/> to its subscribers, as Lattice would. Added in contract 0.22.</summary>
    public void RaiseBuilderChanged(ExecutorId builder) => BuilderChanged.Invoke(builder);

    /// <inheritdoc />
    public virtual event Action<Ticket, Outcome> Answered = delegate { };

    /// <summary>Raises <see cref="Answered"/> to its subscribers, as Lattice would.</summary>
    public void RaiseAnswered(Ticket ticket, Outcome outcome) => Answered.Invoke(ticket, outcome);

    /// <inheritdoc />
    public virtual event Action<PlanId> PlanStateChanged = delegate { };

    /// <summary>Raises <see cref="PlanStateChanged"/> to its subscribers, as Lattice would.</summary>
    public void RaisePlanStateChanged(PlanId plan) => PlanStateChanged.Invoke(plan);

    /// <inheritdoc />
    public virtual event Action<Maker, ValueList<BlockedGhost>> Blocked = delegate { };

    /// <summary>Raises <see cref="Blocked"/> to its subscribers, as Lattice would.</summary>
    public void RaiseBlocked(Maker maker, ValueList<BlockedGhost> ghosts) => Blocked.Invoke(maker, ghosts);

    /// <inheritdoc />
    public virtual event Action<ValueList<StructureMove>> StructuresMoved = delegate { };

    /// <summary>Raises <see cref="StructuresMoved"/> to its subscribers, as Lattice would.</summary>
    public void RaiseStructuresMoved(ValueList<StructureMove> moves) => StructuresMoved.Invoke(moves);

    /// <inheritdoc />
    public virtual event Action<Revision> AnswersReady = delegate { };

    /// <summary>Raises <see cref="AnswersReady"/> to its subscribers, as Lattice would.</summary>
    public void RaiseAnswersReady(Revision revision) => AnswersReady.Invoke(revision);

    /// <inheritdoc />
    public virtual event Action<LoadReport> Loaded = delegate { };

    /// <summary>Raises <see cref="Loaded"/> to its subscribers, as Lattice would.</summary>
    public void RaiseLoaded(LoadReport report) => Loaded.Invoke(report);

    /// <inheritdoc />
    public virtual event Action BoardReset = delegate { };

    /// <summary>Raises <see cref="BoardReset"/> to its subscribers, as Lattice would.</summary>
    public void RaiseBoardReset() => BoardReset.Invoke();
}
