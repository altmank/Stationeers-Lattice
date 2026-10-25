using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Catalogue;
using Lattice.Core.Openings;
using Lattice.Core.Ports;
using Lattice.Core.Runs;
using Lattice.Core.Slices;
using Lattice.Core.Support;

namespace Lattice.Core.Routes;

/// <summary>
/// A planned route: the run laid along the cells the search found (the run planner's plan, so its cost and failing
/// cell read as a drawn run's), the run request it was laid as (the bends as its waypoints), and the search's counts.
/// </summary>
internal sealed record RoutePlan(RunPlan Run, RunRequest AsRun, int Bends, int Expanded)
{
    /// <summary>The command that places the route's ghosts, as <see cref="RunPlan.ToCommand"/> does for a drawn run.</summary>
    public Command ToCommand() => Run.ToCommand(AsRun);

    /// <summary>The route's report, its costs from the given bill (the bills module's).</summary>
    public RunReport Report(Func<ValueList<Placement>, Bill> bill) => Run.Report(bill);
}

/// <summary>
/// One route being planned, stepped in slices: first the search (<see cref="RouteSearch"/>, a bounded number of
/// states a step), then, in its last step, the run planner over the cells found. Its outcome is the route, or why
/// there is none: the search's <see cref="ReasonCodes.NoRoute"/> or <see cref="ReasonCodes.SearchLimit"/> with the
/// nearest cell reached. Main thread, as the board and the placement check are.
/// </summary>
internal sealed class RouteJob : ISlicedWork
{
    private readonly RouteRequest _request;
    private readonly RouteSearch _search;
    private readonly Func<RunRequest, Result<RunPlan, Reason>> _lay;

    internal RouteJob(RouteRequest request, RouteSearch search, Func<RunRequest, Result<RunPlan, Reason>> lay)
    {
        _request = request;
        _search = search;
        _lay = lay;
    }

    /// <summary>The route once the job is done; none while it runs.</summary>
    public Option<Result<RoutePlan, Reason>> Outcome { get; private set; }

    /// <summary>The search, for its counts.</summary>
    public RouteSearch Search => _search;

    public StepOutcome Step()
    {
        if (Outcome.IsSome)
            return StepOutcome.Done;
        if (_search.Step() == StepOutcome.More)
            return StepOutcome.More;

        Outcome = Option.Some(_search.Outcome.TryGet(out var found) ? Finish(found) : Refused(Reason.Of(ReasonCodes.UnknownKind, RouteCodes.Routes)));
        return StepOutcome.Done;
    }

    /// <summary>Runs the job to its end in one call.</summary>
    public Result<RoutePlan, Reason> Run()
    {
        while (Step() == StepOutcome.More)
        {
        }

        return Outcome.TryGet(out var outcome) ? outcome : Refused(Reason.Of(ReasonCodes.UnknownKind, RouteCodes.Routes));
    }

    private Result<RoutePlan, Reason> Finish(RouteOutcome outcome) => outcome switch
    {
        RouteOutcome.Found found => Lay(found),
        RouteOutcome.NotFound none => Refused(none.Why),
        _ => Refused(Reason.Of(ReasonCodes.UnknownKind, RouteCodes.Routes)),
    };

    private Result<RoutePlan, Reason> Lay(RouteOutcome.Found found)
    {
        var run = new RunRequest(_request.Kit, _request.From, Bends(found.Cells), _request.To, _request.Into, _request.MadeBy)
        {
            FromPort = _request.FromPort,
            ToPort = _request.ToPort,
        };
        return _lay(run).Map(plan => new RoutePlan(plan, run, found.Bends, found.Expanded));
    }

    /// <summary>The cells where the route turns, in order: the run's waypoints.</summary>
    private static ValueList<Cell> Bends(ValueList<Cell> cells)
    {
        var bends = new List<Cell>(4);
        for (var i = 1; i + 1 < cells.Count; i++)
        {
            var (a, b, c) = (cells[i - 1], cells[i], cells[i + 1]);
            if (b.X - a.X != c.X - b.X || b.Y - a.Y != c.Y - b.Y || b.Z - a.Z != c.Z - b.Z)
                bends.Add(b);
        }

        return ValueList.From(bends);
    }

    private static Result<RoutePlan, Reason> Refused(Reason why) => Result<RoutePlan, Reason>.Error(why);
}

/// <summary>
/// Finds a run between two ends under the request's toggles and lays it with the run planner. The search reads the
/// planned world (every ghost the board shows done) and catalogue facts only, never the game; the run planner then
/// lays and checks the pieces as for a drawn run. Refused at once for an unknown kit, a kit with no straight, or an
/// end off the kit's grid or a request bound below 1; a route that cannot be found is refused with the search's reason.
/// The request's <see cref="RouteRequest.SearchBound"/>, when given, takes the place of <see cref="RouteLimits.Expansions"/>.
/// Cost: the search is bounded by its box and expansions (<see cref="RouteLimits"/>) and runs in steps of
/// <see cref="RouteLimits.PerStep"/> states; laying is the run planner's cost over the cells found.
/// </summary>
internal sealed class RoutePlanner(
    ICatalogue catalogue, IGrid grid, IPlacementCheck placementCheck, IOpeningFacts openings, ISupportFacts support,
    OpeningBands bands, RouteLimits limits)
{
    private readonly Shapes _shapes = new(catalogue, grid);
    private readonly PieceChooser _chooser = new(catalogue, grid);
    private readonly RunPlanner _runs = new(catalogue, grid, placementCheck);
    private readonly PortEnds _ports = new(new PiecePorts(catalogue, new Shapes(catalogue, grid)), RouteCodes.Routes);

    /// <summary>The route as a job to step; refused when it cannot be searched at all.</summary>
    public Result<RouteJob, Reason> Start(RouteRequest request, IGhostBoard board, IWorldQuery world)
    {
        var known = _chooser.Kit(request.Kit);
        if (!known.TryGet(out var kit))
            return Result<RouteJob, Reason>.Error(known.TryGetError(out var why) ? why : Reason.Of(ReasonCodes.NoPiece, RouteCodes.Routes));
        foreach (var end in new[] { request.From, request.To })
            if (grid.Snap(grid.CentreOf(end, kit.Grid), kit.Grid) != end)
                return Result<RouteJob, Reason>.Error(Reason.Of(ReasonCodes.InvalidValue, RouteCodes.Routes, ValueMap
                    .Of("cell", $"{end.X},{end.Y},{end.Z}")
                    .With("problem", "off-grid")));
        if (request.SearchBound.TryGet(out var bound) && bound < 1)
            return Result<RouteJob, Reason>.Error(Reason.Of(ReasonCodes.InvalidValue, RouteCodes.Routes, ValueMap
                .Of("problem", "search-bound")
                .With("bound", bound.ToString(System.Globalization.CultureInfo.InvariantCulture))));

        var resolved = _ports.Resolve(request.FromPort, request.From, request.ToPort, request.To, kit, board);
        if (!resolved.TryGet(out var named))
            return Result<RouteJob, Reason>.Error(resolved.ErrorOf());

        var planned = new PlannedWorld(board, world);
        var reach = kit.Step * limits.Margin;
        var min = new Cell(Math.Min(request.From.X, request.To.X) - reach, Math.Min(request.From.Y, request.To.Y) - reach, Math.Min(request.From.Z, request.To.Z) - reach);
        var max = new Cell(Math.Max(request.From.X, request.To.X) + reach, Math.Max(request.From.Y, request.To.Y) + reach, Math.Max(request.From.Z, request.To.Z) + reach);
        var cells = new CellRules(
            kit, grid, _shapes, planned, request.Toggles, request.From, request.To,
            new OpeningKeepOuts(planned, _shapes, openings, bands), support,
            new RouteNetworks(planned, _shapes, grid, kit.Grid, min, max), named);
        var bounded = request.SearchBound.IsSome ? limits with { Expansions = request.SearchBound } : limits;
        var search = new RouteSearch(grid, kit.Grid, request.From, request.To, cells, request.Toggles.FewestBends, bounded);
        return Result<RouteJob, Reason>.Ok(new RouteJob(request, search, run => _runs.Plan(run, board, world)));
    }

    /// <summary>The route planned in one call: for tests and for callers that bound the work themselves.</summary>
    internal Result<RoutePlan, Reason> Plan(RouteRequest request, IGhostBoard board, IWorldQuery world) =>
        Start(request, board, world).Bind(job => job.Run());
}
