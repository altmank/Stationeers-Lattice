using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Written;

namespace Lattice.Core.Saving;

/// <summary>A piece a plan built: the ghost as it was last written before it was done, and the structure that did it.</summary>
internal sealed record BuiltPiece(Ghost Ghost, StructureRef Structure);

/// <summary>
/// A finished plan as its file holds it at its finishing line: its record, every piece it built along the way (file
/// order), and the ghosts it still held when it finished (the finishing line removed them).
/// </summary>
internal sealed record FinishedContents(Plan Record, ValueList<BuiltPiece> Built, ValueList<Ghost> Left, long Line)
{
    /// <summary>The plan's ghosts done along its lines up to its finishing line.</summary>
    public int Done { get; init; }

    /// <summary>Of <see cref="Done"/>, how many each executor held the claim on.</summary>
    public ValueMap<ExecutorId, int> DoneBy { get; init; } = ValueMap<ExecutorId, int>.Empty;
}

/// <summary>
/// Replays a plan's lines from its first line, keeping what a finished plan offers: the pieces built (each ghost
/// removed as done with a structure) and the ghosts the last line removed with no other cause. Checkpoints hold only a
/// plan's current ghosts, so this always starts at the first line. Linear in the lines.
/// </summary>
internal sealed class FinishedReplay
{
    private readonly PlanId _plan;
    private readonly Dictionary<GhostId, Ghost> _ghosts = new();
    private readonly List<BuiltPiece> _built = new();
    private readonly List<Ghost> _left = new();

    public FinishedReplay(PlanId plan) => _plan = plan;

    public Option<Plan> Record { get; private set; }

    public bool Finished { get; private set; }

    /// <summary>Ghosts of the plan done along the lines applied.</summary>
    public int Done { get; private set; }

    /// <summary>Of <see cref="Done"/>, how many each executor held the claim on.</summary>
    public ValueMap<ExecutorId, int> DoneBy { get; private set; } = ValueMap<ExecutorId, int>.Empty;

    public IReadOnlyList<BuiltPiece> Built => _built;

    /// <summary>The ghosts the last line applied removed with <see cref="RemovalCause.Removed"/>.</summary>
    public IReadOnlyList<Ghost> Left => _left;

    public void Apply(ChangeLine line)
    {
        _left.Clear();
        foreach (var op in line.Ops)
            Apply(op);
    }

    private void Apply(ChangeOp op)
    {
        switch (op)
        {
            case ChangeOp.AddGhost { Ghost: GhostRecord.Known known }:
                Set(known.Ghost);
                break;
            case ChangeOp.ChangeGhost { Ghost: GhostRecord.Known known }:
                Set(known.Ghost);
                break;
            case ChangeOp.RemoveGhost remove when _ghosts.TryGetValue(remove.Ghost, out var ghost):
                _ghosts.Remove(remove.Ghost);
                if (remove.Cause is RemovalCause.Done done)
                {
                    Done++;
                    if (done.How.By.TryGet(out var by))
                        DoneBy = DoneBy.With(by, (DoneBy.TryGetValue(by, out var had) ? had : 0) + 1);
                }
                if (remove.Cause is RemovalCause.Done { How.Built: var built } && built.TryGet(out var structure))
                    _built.Add(new BuiltPiece(ghost, structure));
                else if (remove.Cause is RemovalCause.Removed)
                    _left.Add(ghost);
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
                break;
        }
    }

    private void Set(Ghost ghost)
    {
        if (ghost.Plan == _plan)
            _ghosts[ghost.Id] = ghost;
        else
            _ghosts.Remove(ghost.Id);
    }
}
