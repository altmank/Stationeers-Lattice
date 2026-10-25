using System;
using Lattice.Api;
using Lattice.Core.Board;

namespace Lattice.Core.Runs;

/// <summary>
/// The expander for <see cref="Command.PlanRun"/>: the run planner's ghosts as one <see cref="Command.Place"/>, laid
/// against the board as the submitting actor sees it and the world. Refused with the planner's reason when the run
/// cannot be planned at all; a run with a failing cell is still placed (its report names the cell).
/// </summary>
internal sealed class RunExpander(RunPlanner planner, RunReportBook? book = null)
{
    /// <summary>The module that owns runs.</summary>
    public static ModuleName Module { get; } = ModuleName.Create("runs")
        .Match(static name => name, static reason => throw new InvalidOperationException(reason.ToString()));

    /// <summary>The descriptor to register.</summary>
    public CommandExpander<Command.PlanRun> Descriptor => new(Module, Expand);

    /// <summary>Lays the run; the plan is kept in the book (when there is one) for the preview of the same command.</summary>
    public Result<Command, Reason> Expand(ExpansionInput<Command.PlanRun> input)
    {
        var planned = planner.Plan(input.Command.Request, input.Board, input.World);
        if (!planned.TryGet(out var plan))
            return Result<Command, Reason>.Error(planned.ErrorOf());
        book?.Keep(input.Command, plan);
        return Result<Command, Reason>.Ok(plan.ToCommand(input.Command.Request));
    }
}
