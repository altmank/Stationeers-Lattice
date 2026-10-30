using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Written;

namespace Lattice.Core.Saving;

/// <summary>
/// One plan's state as its lines leave it: its plan record, its ghosts, whether it is finished. Applying a line runs
/// its operations in order: a ghost added or changed is set (one whose plan is now another has left this plan), a
/// removed ghost goes, an opened plan is active again, a finished plan is finished. Records and operations this
/// version cannot read are counted and skipped. Linear in the line's operations.
/// </summary>
internal sealed class PlanReplay
{
    private readonly Dictionary<GhostId, Ghost> _ghosts = new();

    public PlanReplay(PlanId plan) => Plan = plan;

    public PlanId Plan { get; }

    /// <summary>The plan as last written, if the lines gave it.</summary>
    public Option<Plan> Record { get; private set; }

    public bool Finished { get; private set; }

    /// <summary>The ghosts the plan holds now, in no particular order.</summary>
    public IReadOnlyCollection<Ghost> Ghosts => _ghosts.Values;

    /// <summary>Records and operations skipped because this version cannot read them.</summary>
    public int Unread { get; private set; }

    /// <summary>Ghosts done with a built structure along the plan's lines up to here: the pieces it built.</summary>
    public int Built { get; private set; }

    /// <summary>Ghosts of the plan done along its lines up to here (built, removed or finished as asked; they left the plan).</summary>
    public int Done { get; private set; }

    /// <summary>Of <see cref="Done"/>, how many each executor held the claim on (the done line's <c>by</c>).</summary>
    public ValueMap<ExecutorId, int> DoneBy { get; private set; } = ValueMap<ExecutorId, int>.Empty;

    /// <summary>The box around the structures it built; none before the first.</summary>
    public Option<(Cell Min, Cell Max)> Bounds { get; private set; }

    /// <summary>When the line the state is at was written.</summary>
    public DateTimeOffset LineAt { get; private set; }

    /// <summary>The line the state is at; 0 before any line.</summary>
    public long At { get; private set; }

    /// <summary>
    /// A state from a checkpoint: the plan, its ghosts and whether it was finished at that line. A checkpoint written
    /// before done counts were kept gives no <paramref name="done"/>; its built count stands in (every built ghost is done).
    /// </summary>
    public static PlanReplay From(PlanId plan, long at, Option<Plan> record, bool finished, IEnumerable<Ghost> ghosts, int built = 0, Option<(Cell Min, Cell Max)> bounds = default, int? done = null, ValueMap<ExecutorId, int>? doneBy = null)
    {
        var replay = new PlanReplay(plan) { At = at, Record = record, Finished = finished, Built = built, Bounds = bounds, Done = done ?? built, DoneBy = doneBy ?? ValueMap<ExecutorId, int>.Empty };
        foreach (var ghost in ghosts)
            replay._ghosts[ghost.Id] = ghost;
        return replay;
    }

    public void Apply(ChangeLine line)
    {
        foreach (var op in line.Ops)
            Apply(op);
        Reached(line);
    }

    /// <summary>The state is at <paramref name="line"/>, whose operations were applied one by one.</summary>
    public void Reached(ChangeLine line)
    {
        At = line.Number.Value;
        LineAt = line.At;
    }

    /// <summary>Applies one operation of a line; <see cref="Reached"/> follows the line's last.</summary>
    public void Apply(ChangeOp op)
    {
        switch (op)
        {
            case ChangeOp.AddGhost { Ghost: GhostRecord.Known known }:
                Set(known.Ghost);
                break;
            case ChangeOp.ChangeGhost { Ghost: GhostRecord.Known known }:
                Set(known.Ghost);
                break;
            case ChangeOp.RemoveGhost remove:
                _ghosts.Remove(remove.Ghost);
                if (remove.Cause is RemovalCause.Done done)
                {
                    Done++;
                    if (done.How.By.TryGet(out var by))
                        DoneBy = DoneBy.With(by, (DoneBy.TryGetValue(by, out var had) ? had : 0) + 1);
                }
                if (remove.Cause is RemovalCause.Done { How.Built: var built } && built.TryGet(out var structure))
                    Count(structure.At);
                break;
            case ChangeOp.OpenPlan { Plan: PlanRecord.Known known }:
                Record = Option.Some(known.Plan);
                Finished = false;
                break;
            case ChangeOp.EditPlan { Plan: PlanRecord.Known known }:
                Record = Option.Some(known.Plan);
                break;
            case ChangeOp.FinishPlan:
                Finished = true;
                break;
            default:
                Unread++;
                break;
        }
    }

    private void Count(Cell at)
    {
        Built++;
        Bounds = Bounds.TryGet(out var box)
            ? Option.Some((
                new Cell(Math.Min(box.Min.X, at.X), Math.Min(box.Min.Y, at.Y), Math.Min(box.Min.Z, at.Z)),
                new Cell(Math.Max(box.Max.X, at.X), Math.Max(box.Max.Y, at.Y), Math.Max(box.Max.Z, at.Z))))
            : Option.Some((at, at));
    }

    private void Set(Ghost ghost)
    {
        if (ghost.Plan == Plan)
            _ghosts[ghost.Id] = ghost;
        else
            _ghosts.Remove(ghost.Id);
    }
}
