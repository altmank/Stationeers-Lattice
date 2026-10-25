using System;
using Lattice.Api;
using Lattice.Core.Commands;
using Lattice.Core.Runs;
using Lattice.Core.Slices;

namespace Lattice.Core.Routes;

/// <summary>
/// One <see cref="Command.PlanRoute"/> being expanded in slices: the route job's steps, then the run's ghosts as one
/// <see cref="Command.Place"/>. A request that cannot be searched at all (unknown kit, an end off the grid) finishes on
/// its first step with that refusal.
/// </summary>
internal sealed class RouteExpansion : IExpansionWork
{
    private readonly Option<RouteJob> _job;
    private readonly Command.PlanRoute? _command;
    private readonly RunReportBook? _book;
    private Option<Result<Command, Reason>> _found;

    public RouteExpansion(Result<RouteJob, Reason> started, Command.PlanRoute? command = null, RunReportBook? book = null)
    {
        _command = command;
        _book = book;
        if (started.TryGet(out var job))
            _job = Option.Some(job);
        else if (started.TryGetError(out var why))
            _found = Option.Some(Result<Command, Reason>.Error(why));
    }

    /// <summary>The expanded command, or why the route cannot be planned; read once a step has returned done.</summary>
    public Result<Command, Reason> Found => _found.TryGet(out var found)
        ? found
        : Result<Command, Reason>.Error(Reason.Of(ReasonCodes.UnknownKind, RouteCodes.Routes, ValueMap.Of("problem", "not-done")));

    public StepOutcome Step()
    {
        if (_found.IsSome || !_job.TryGet(out var job))
            return StepOutcome.Done;
        if (job.Step() == StepOutcome.More)
            return StepOutcome.More;
        _found = job.Outcome.Map(found => found.Map(plan =>
        {
            if (_command is not null)
                _book?.Keep(_command, plan.Run);
            return plan.ToCommand();
        }));
        return StepOutcome.Done;
    }
}

/// <summary>
/// The expander for <see cref="Command.PlanRoute"/>, registered sliced (<see cref="CommandExpanders.RegisterSliced{TCommand}"/>):
/// a route's search may take many frames, so it is found in the checking budget and the host answers the command once
/// it is done. Laid against the board as the submitting actor sees it and the world. A route with a failing cell is
/// still placed (its report names the cell).
/// </summary>
internal sealed class RouteExpander(RoutePlanner planner, RunReportBook? book = null)
{
    /// <summary>The module that owns routes.</summary>
    public static ModuleName Module { get; } = ModuleName.Create("routes")
        .Match(static name => name, static reason => throw new InvalidOperationException(reason.ToString()));

    /// <summary>The descriptor to register.</summary>
    public SlicedExpander<Command.PlanRoute> Descriptor => new(Module, Start);

    /// <summary>Sets up the work for one command; searches nothing yet. The route's run plan is kept in the book (when there is one) for the preview of the same command.</summary>
    public IExpansionWork Start(ExpansionInput<Command.PlanRoute> input) =>
        new RouteExpansion(planner.Start(input.Command.Request, input.Board, input.World), input.Command, book);
}
