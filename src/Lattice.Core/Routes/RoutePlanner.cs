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
/// One route being planned, stepped in slices: first the main route's search (<see cref="RouteSearch"/>, a bounded
/// number of states a step), one leg after another when the request has <see cref="RouteRequest.Via"/> cells, then, in
/// the step the last leg ends, the run planner over the legs' cells joined end to end; then each branch in order,
/// searched to the nearest cell of the run so far and laid against the earlier runs' pieces. Its outcome is the route,
/// every run joined into one plan (<see cref="RunPlan.Joined"/>), or why there is none: a search's
/// <see cref="ReasonCodes.NoRoute"/> or <see cref="ReasonCodes.SearchLimit"/> with the nearest cell reached (fact
/// <c>leg</c> naming a leg of a route with via cells, <c>branch</c> a branch's index). Main thread, as the board and the
/// placement check are.
/// </summary>
internal sealed class RouteJob : ISlicedWork
{
    private readonly RouteRequest _request;
    private readonly Func<int, IReadOnlyCollection<Cell>, RouteSearch> _leg;
    private readonly Func<int, IReadOnlyCollection<Cell>, RouteSearch> _branch;
    private readonly Func<RunRequest, IReadOnlyList<Intent>, Result<RunPlan, Reason>> _lay;
    private readonly Func<IReadOnlyList<RunPlan>, RunPlan> _join;
    private readonly List<RunPlan> _runs = [];
    private readonly List<Intent> _laid = [];
    private readonly List<Cell> _cells = [];
    private readonly List<Cell> _legs = [];
    private RouteSearch _search;
    private RunRequest? _main;
    private int _legAt;
    private int _branchAt = -1;
    private int _bends;
    private int _expanded;

    /// <param name="request">The request.</param>
    /// <param name="search">The main route's search: its first leg's, when it has via cells.</param>
    /// <param name="leg">The search of leg k (from 1), given the main route's cells so far.</param>
    /// <param name="branch">The search of branch k, given the run's cells so far.</param>
    /// <param name="lay">Lays a run after the changes of the runs before it.</param>
    /// <param name="join">Joins the runs into one plan.</param>
    internal RouteJob(
        RouteRequest request, RouteSearch search, Func<int, IReadOnlyCollection<Cell>, RouteSearch> leg,
        Func<int, IReadOnlyCollection<Cell>, RouteSearch> branch, Func<RunRequest, IReadOnlyList<Intent>, Result<RunPlan, Reason>> lay,
        Func<IReadOnlyList<RunPlan>, RunPlan> join)
    {
        _request = request;
        _search = search;
        _leg = leg;
        _branch = branch;
        _lay = lay;
        _join = join;
    }

    /// <summary>The route once the job is done; none while it runs.</summary>
    public Option<Result<RoutePlan, Reason>> Outcome { get; private set; }

    /// <summary>The search under way: the main route's (each leg's in turn), then each branch's.</summary>
    public RouteSearch Search => _search;

    public StepOutcome Step()
    {
        if (Outcome.IsSome)
            return StepOutcome.Done;
        if (_search.Step() == StepOutcome.More)
            return StepOutcome.More;

        if (!_search.Outcome.TryGet(out var found))
            return Finish(Refused(Reason.Of(ReasonCodes.UnknownKind, RouteCodes.Routes)));
        if (found is RouteOutcome.NotFound none)
            return Finish(Refused(Named(none.Why)));
        if (found is not RouteOutcome.Found route)
            return Finish(Refused(Reason.Of(ReasonCodes.UnknownKind, RouteCodes.Routes)));

        _expanded += route.Expanded;
        var cells = route.Cells;
        if (_branchAt < 0 && _request.Via.Count > 0)
        {
            for (var i = _legs.Count == 0 ? 0 : 1; i < route.Cells.Count; i++)
                _legs.Add(route.Cells[i]);
            if (++_legAt <= _request.Via.Count)
            {
                _search = _leg(_legAt, _legs);
                return StepOutcome.More;
            }

            cells = ValueList.From(_legs);
        }

        var run = RunOf(cells);
        var laid = _lay(run, _laid);
        if (!laid.TryGet(out var plan))
            return Finish(Refused(Named(laid.ErrorOf(), searched: false)));
        if (_branchAt < 0)
            (_main, _bends) = (run, _request.Via.Count == 0 ? route.Bends : run.Via.Count);
        _runs.Add(plan);
        _laid.Clear();
        _laid.AddRange(_runs.Count == 1 ? plan.Changes : _join(_runs).Changes);
        _cells.AddRange(plan.Cells);

        if (++_branchAt < _request.Branches.Count)
        {
            _search = _branch(_branchAt, _cells);
            return StepOutcome.More;
        }

        return Finish(Result<RoutePlan, Reason>.Ok(new RoutePlan(_join(_runs), _main!, _bends, _expanded)));
    }

    /// <summary>Runs the job to its end in one call.</summary>
    public Result<RoutePlan, Reason> Run()
    {
        while (Step() == StepOutcome.More)
        {
        }

        return Outcome.TryGet(out var outcome) ? outcome : Refused(Reason.Of(ReasonCodes.UnknownKind, RouteCodes.Routes));
    }

    private StepOutcome Finish(Result<RoutePlan, Reason> outcome)
    {
        Outcome = Option.Some(outcome);
        return StepOutcome.Done;
    }

    // The run the found cells lay: the main route between the request's ends, a branch from its start to the run cell it reached.
    private RunRequest RunOf(ValueList<Cell> cells)
    {
        if (_branchAt < 0)
            return new RunRequest(_request.Kit, _request.From, Bends(cells), _request.To, _request.Into, _request.MadeBy)
            {
                FromPort = _request.FromPort,
                ToPort = _request.ToPort,
                Lay = _request.Lay,
            };
        var branch = _request.Branches[_branchAt];
        return new RunRequest(_request.Kit, branch.From, Bends(cells), cells[cells.Count - 1], _request.Into, _request.MadeBy)
        {
            FromPort = branch.Port,
            Lay = _request.Lay,
        };
    }

    // A search's refusal names the leg (a route with via cells) or the branch it was; laying the whole main route names neither.
    private Reason Named(Reason why, bool searched = true)
    {
        if (_branchAt >= 0)
            return why with { Facts = why.Facts.With("branch", _branchAt.ToString(System.Globalization.CultureInfo.InvariantCulture)) };
        if (searched && _request.Via.Count > 0)
            return why with { Facts = why.Facts.With("leg", _legAt.ToString(System.Globalization.CultureInfo.InvariantCulture)) };
        return why;
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
/// Finds a run between two ends under the request's toggles and lays it with the run planner, then each branch of the
/// request to the nearest cell of the run so far. The search reads the planned world (every ghost the board shows
/// done) and catalogue facts only, never the game; the run planner then lays and checks the pieces as for a drawn run.
/// Refused at once for an unknown kit, a kit with no straight, an end, via cell or branch start off the kit's grid, a
/// via cell equal to an end or another via cell, a request bound below 1, more than <see cref="RouteLimits.MaxVia"/>
/// via cells or <see cref="RouteLimits.MaxBranches"/> branches, or a port end (a branch's too) that does not resolve; a
/// route that cannot be found is refused with the search's reason.
/// With via cells, the main route is one search per leg, each in its own box (its two cells plus the margin), entering
/// its two cells as ends and never the cells of an earlier leg, the request's other ends or the other via cells; the
/// legs are greedy, so an earlier leg may box in a later one. The legs' cells are laid as one run.
/// The request's <see cref="RouteRequest.SearchBound"/>, when given, takes the place of <see cref="RouteLimits.Expansions"/>
/// for each search.
/// Cost: each search is bounded by its box and expansions (<see cref="RouteLimits"/>) and runs in steps of
/// <see cref="RouteLimits.PerStep"/> states; laying is the run planner's cost over the cells found.
/// </summary>
internal sealed class RoutePlanner(
    ICatalogue catalogue, IGrid grid, IPlacementCheck placementCheck, IOpeningFacts openings, ISupportFacts support,
    OpeningBands bands, RouteLimits limits)
{
    private readonly Shapes _shapes = new(catalogue, grid);
    private readonly PieceChooser _chooser = new(catalogue, grid);
    private readonly RunPlanner _runs = new(catalogue, grid, placementCheck, openings);
    private readonly PortEnds _ports = new(new PiecePorts(catalogue, new Shapes(catalogue, grid)), RouteCodes.Routes);

    /// <summary>The route as a job to step; refused when it cannot be searched at all.</summary>
    public Result<RouteJob, Reason> Start(RouteRequest request, IGhostBoard board, IWorldQuery world)
    {
        var known = _chooser.Kit(request.Kit);
        if (!known.TryGet(out var kit))
            return Result<RouteJob, Reason>.Error(known.TryGetError(out var why) ? why : Reason.Of(ReasonCodes.NoPiece, RouteCodes.Routes));
        if (request.Branches.Count > RouteLimits.MaxBranches)
            return Result<RouteJob, Reason>.Error(Reason.Of(ReasonCodes.TooLarge, RouteCodes.Routes, ValueMap
                .Of("branches", request.Branches.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .With("most", RouteLimits.MaxBranches.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        if (request.Via.Count > RouteLimits.MaxVia)
            return Result<RouteJob, Reason>.Error(Reason.Of(ReasonCodes.TooLarge, RouteCodes.Routes, ValueMap
                .Of("via", request.Via.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .With("most", RouteLimits.MaxVia.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        var ends = new List<Cell>(2 + request.Branches.Count) { request.From, request.To };
        foreach (var branch in request.Branches)
            ends.Add(branch.From);
        for (var i = 0; i < ends.Count; i++)
        {
            var end = ends[i];
            if (OnGrid(end, kit))
                continue;
            var facts = ValueMap.Of("cell", $"{end.X},{end.Y},{end.Z}").With("problem", "off-grid");
            return Result<RouteJob, Reason>.Error(Reason.Of(ReasonCodes.InvalidValue, RouteCodes.Routes,
                i < 2 ? facts : facts.With("branch", (i - 2).ToString(System.Globalization.CultureInfo.InvariantCulture))));
        }

        var points = new List<Cell>(2 + request.Via.Count) { request.From };
        for (var i = 0; i < request.Via.Count; i++)
        {
            var via = request.Via[i];
            var problem = !OnGrid(via, kit) ? "off-grid" : via == request.From || via == request.To || points.Contains(via) ? "via-repeats" : null;
            if (problem is not null)
                return Result<RouteJob, Reason>.Error(Reason.Of(ReasonCodes.InvalidValue, RouteCodes.Routes, ValueMap
                    .Of("cell", $"{via.X},{via.Y},{via.Z}")
                    .With("problem", problem)
                    .With("via", i.ToString(System.Globalization.CultureInfo.InvariantCulture))));
            points.Add(via);
        }

        points.Add(request.To);
        if (request.SearchBound.TryGet(out var bound) && bound < 1)
            return Result<RouteJob, Reason>.Error(Reason.Of(ReasonCodes.InvalidValue, RouteCodes.Routes, ValueMap
                .Of("problem", "search-bound")
                .With("bound", bound.ToString(System.Globalization.CultureInfo.InvariantCulture))));

        var resolved = _ports.Resolve(request.FromPort, request.From, request.ToPort, request.To, kit, board, world);
        if (!resolved.TryGet(out var named))
            return Result<RouteJob, Reason>.Error(resolved.ErrorOf());
        var allNamed = new List<PortTarget>(named);
        for (var i = 0; i < request.Branches.Count; i++)
        {
            var branch = request.Branches[i];
            var port = _ports.Resolve(branch.Port, branch.From, Option<PortEnd>.None, branch.From, kit, board, world);
            if (!port.TryGet(out var targets))
                return Result<RouteJob, Reason>.Error(port.ErrorOf() with
                {
                    Facts = port.ErrorOf().Facts.With("branch", i.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                });
            allNamed.AddRange(targets);
        }

        var planned = new PlannedWorld(board, world);
        var reach = kit.Step * limits.Margin;
        var (min, max) = (points[0], points[0]);
        foreach (var point in points)
            (min, max) = (Min(min, point), Max(max, point));
        min = new Cell(min.X - reach, min.Y - reach, min.Z - reach);
        max = new Cell(max.X + reach, max.Y + reach, max.Z + reach);
        var openingsNear = new OpeningKeepOuts(planned, _shapes, openings, bands);
        var networks = new RouteNetworks(planned, _shapes, grid, kit.Grid, min, max);
        IReadOnlyList<PortTarget> mainNamed = request.Branches.Count == 0 ? named : allNamed;
        var bounded = request.SearchBound.IsSome ? limits with { Expansions = request.SearchBound } : limits;

        RouteSearch Leg(int index, IReadOnlyCollection<Cell> earlier)
        {
            var (from, to) = (points[index], points[index + 1]);
            HashSet<Cell>? closed = null;
            if (points.Count > 2)
            {
                closed = new HashSet<Cell>(earlier);
                for (var i = 0; i < points.Count; i++)
                    if (i != index + 1)
                        closed.Add(points[i]);
                closed.Remove(from);
            }

            var rules = new CellRules(
                kit, grid, _shapes, planned, request.Toggles, request.From, request.To, openingsNear, support, networks, mainNamed, ends,
                points.Count > 2 ? new[] { from, to } : null, closed);
            return new RouteSearch(grid, kit.Grid, from, to, rules, request.Toggles.FewestBends, bounded, Options(request, rules, null));
        }

        var search = Leg(0, Array.Empty<Cell>());

        RouteSearch Branch(int index, IReadOnlyCollection<Cell> run)
        {
            var from = request.Branches[index].From;
            var nearest = Nearest(from, run);
            var low = new Cell(Math.Min(from.X, nearest.X) - reach, Math.Min(from.Y, nearest.Y) - reach, Math.Min(from.Z, nearest.Z) - reach);
            var high = new Cell(Math.Max(from.X, nearest.X) + reach, Math.Max(from.Y, nearest.Y) + reach, Math.Max(from.Z, nearest.Z) + reach);
            var rules = new CellRules(
                kit, grid, _shapes, planned, request.Toggles, from, from, openingsNear, support,
                new RouteNetworks(planned, _shapes, grid, kit.Grid, low, high), allNamed, ends);
            return new RouteSearch(grid, kit.Grid, from, nearest, rules, request.Toggles.FewestBends, bounded, Options(request, rules, run));
        }

        return Result<RouteJob, Reason>.Ok(new RouteJob(
            request, search, Leg, Branch, (run, laid) => _runs.Plan(run, board, world, laid), runs => RunPlan.Joined(runs, _shapes)));
    }

    private bool OnGrid(Cell cell, KitShapes kit) => grid.Snap(grid.CentreOf(cell, kit.Grid), kit.Grid) == cell;

    private static Cell Min(Cell a, Cell b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));

    private static Cell Max(Cell a, Cell b) => new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));

    private static SearchOptions Options(RouteRequest request, CellRules cells, IReadOnlyCollection<Cell>? goals) =>
        new(request.Toggles.First, request.Toggles.HugFramesAndWalls ? cells.InOpenAir : null, goals);

    // The run cell nearest the branch's start along the axes; the first such in run order.
    private static Cell Nearest(Cell from, IReadOnlyCollection<Cell> run)
    {
        var (best, distance) = (from, long.MaxValue);
        foreach (var cell in run)
        {
            var d = (long)Math.Abs(cell.X - from.X) + Math.Abs(cell.Y - from.Y) + Math.Abs(cell.Z - from.Z);
            if (d < distance)
                (best, distance) = (cell, d);
        }

        return best;
    }

    /// <summary>The route planned in one call: for tests and for callers that bound the work themselves.</summary>
    internal Result<RoutePlan, Reason> Plan(RouteRequest request, IGhostBoard board, IWorldQuery world) =>
        Start(request, board, world).Bind(job => job.Run());
}
