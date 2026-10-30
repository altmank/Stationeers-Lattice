using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Catalogue;
using Lattice.Core.Grid;
using Lattice.Core.Openings;
using Lattice.Core.Ports;
using Lattice.Core.Routes;
using Lattice.Core.Support;

namespace Lattice.Core.Runs;

/// <summary>
/// A planned run: its cells, the changes that lay it (build ghosts, swaps of real pieces it merges with, removals of
/// real long pieces it splits), the new pieces, the same cells as single pieces, and the first cell that fails.
/// </summary>
internal sealed record RunPlan(
    ValueList<Cell> Cells,
    ValueList<Intent> Changes,
    ValueList<Placement> Pieces,
    ValueList<Placement> Singles,
    Option<CellFailure> FirstFailure)
{
    /// <summary>Each port end of the request, the from end first, and the piece that joins it.</summary>
    public ValueList<PortJoin> PortEnds { get; init; } = ValueList<PortJoin>.Empty;

    /// <summary>The kit the run was laid with when matching the grade chose another than the request's.</summary>
    public Option<KitKey> LaidWith { get; init; }

    /// <summary>The first cell that breaks the request's rules (<see cref="RunRequest.Rules"/>); the run lays there all the same.</summary>
    public Option<CellFailure> FirstBreak { get; init; }

    /// <summary>Each cell's flow along the run, from its From to its To, for the runs laid after it.</summary>
    public IReadOnlyDictionary<Cell, Flow> Flows { get; init; } = new Dictionary<Cell, Flow>();

    /// <summary>The cells where the run met a line whose flow it could not read, and the way it chose for the items there.</summary>
    public ValueList<FlowChoice> FlowChosen { get; init; } = ValueList<FlowChoice>.Empty;

    /// <summary>The command that places the run's ghosts into the request's plan, made by its maker, seen by everyone.</summary>
    public Command ToCommand(RunRequest request)
    {
        var drafts = new List<GhostDraft>(Changes.Count);
        foreach (var change in Changes)
            drafts.Add(new GhostDraft(
                change, request.Into, request.MadeBy, new Visibility.Everyone(), Option<Label>.None, Option<Tint>.None,
                ValueSet<GroupName>.Empty, ValueMap<AttachmentKey, Attachment>.Empty));
        return new Command.Place(ValueList.From(drafts));
    }

    /// <summary>
    /// Runs laid one after another as one plan: each run's changes and pieces in order, an earlier run's new piece left
    /// out where a later one takes a spot of it (as placing the later run would replace it), the cells of all of them
    /// once each, the first failure and the first break of the first run that has one, every port end, and the first run's kit.
    /// </summary>
    public static RunPlan Joined(IReadOnlyList<RunPlan> runs, Shapes shapes)
    {
        if (runs.Count == 1)
            return runs[0];
        var changes = new List<Intent>();
        var pieces = new List<Placement>();
        var singles = new List<Placement>();
        var cells = new List<Cell>();
        var seen = new HashSet<Cell>();
        var ports = new List<PortJoin>();
        var flows = new Dictionary<Cell, Flow>();
        var chosen = new List<FlowChoice>();
        var failure = Option<CellFailure>.None;
        var broken = Option<CellFailure>.None;
        foreach (var run in runs)
        {
            var spots = new HashSet<SpotKey>();
            foreach (var change in run.Changes)
                if (PieceOf(change) is { } placement)
                    AddSpots(spots, placement, shapes);
            changes.RemoveAll(change => PieceOf(change) is { } placement && Overlaps(spots, placement, shapes));
            pieces.RemoveAll(piece => Overlaps(spots, piece, shapes));
            singles.RemoveAll(piece => Overlaps(spots, piece, shapes));
            changes.AddRange(run.Changes);
            pieces.AddRange(run.Pieces);
            singles.AddRange(run.Singles);
            foreach (var cell in run.Cells)
                if (seen.Add(cell))
                    cells.Add(cell);
            ports.AddRange(run.PortEnds);
            foreach (var (cell, flow) in run.Flows)
                flows[cell] = flows.TryGetValue(cell, out var had) ? had.Union(flow) : flow;
            chosen.AddRange(run.FlowChosen);
            if (failure.IsNone)
                failure = run.FirstFailure;
            if (broken.IsNone)
                broken = run.FirstBreak;
        }

        return new RunPlan(ValueList.From(cells), ValueList.From(changes), ValueList.From(pieces), ValueList.From(singles), failure)
        {
            PortEnds = ValueList.From(ports),
            LaidWith = runs[0].LaidWith,
            FirstBreak = broken,
            Flows = flows,
            FlowChosen = ValueList.From(chosen),
        };
    }

    private static Placement? PieceOf(Intent change) => change switch
    {
        Intent.Build build => build.Target,
        Intent.Swap swap => swap.New,
        _ => null,
    };

    private static void AddSpots(HashSet<SpotKey> spots, Placement placement, Shapes shapes)
    {
        if (shapes.Of(placement).TryGet(out var shape))
            foreach (var spot in shape.Spots)
                spots.Add(spot);
    }

    private static bool Overlaps(HashSet<SpotKey> spots, Placement placement, Shapes shapes)
    {
        if (!shapes.Of(placement).TryGet(out var shape))
            return false;
        foreach (var spot in shape.Spots)
            if (spots.Contains(spot))
                return true;
        return false;
    }

    /// <summary>The run's report, its costs from the given bill (the bills module's).</summary>
    public RunReport Report(Func<ValueList<Placement>, Bill> bill) =>
        new(Cells, Pieces, FirstFailure, bill(Pieces), bill(Singles)) { PortEnds = PortEnds, LaidWith = LaidWith, FirstBreak = FirstBreak, FlowChosen = FlowChosen };
}

/// <summary>
/// Lays a run of cable, pipe or chute between drawn points. Cost: linear in the run's cells times the kit's long
/// straights (one game placement check per piece tried); main thread, as the placement check is.
/// <list type="bullet">
/// <item>Each cell gets the kit's piece for the ways the run leaves it plus the neighbours' ends that point in (the piece chooser).</item>
/// <item>A cell already holding the kit's piece merges: same ways, left as is; a one-cell piece is swapped (real) or replaced (ghost) by the merged shape; a piece over several cells is split where the run joins it (the split planner): a real one gets a removal ghost and build ghosts for its cells, a ghost is replaced by them.</item>
/// <item>Free straight stretches take the kit's longest straights first, as the game's cable gun lays them; with <see cref="LayOptions.SinglePiecesOnly"/>, single pieces.</item>
/// <item>Every new piece in a free or ghost-held cell goes through the game's placement check; a long straight it refuses is tried shorter, down to single pieces, so a failure names one cell and the game's reason. Pieces over a real piece being replaced are not checked: the game's cursor would see the old piece. A failing cell still gets its ghost: validation reports it, and a planned removal may clear the cell first.</item>
/// <item>A cell the kit has no piece for gets none and fails with <see cref="ReasonCodes.NoPiece"/>.</item>
/// <item>With <see cref="LayOptions.MatchGrade"/>, the run is laid with the kit of a run piece it joins at its from end,
/// else at its to end (a piece in the end cell, or a neighbour's end pointing into it), when that kit lays the same
/// network kinds on the same grid (<see cref="KitShapes.LaysTheSameNetworks"/>).</item>
/// <item>With <see cref="RunRequest.Rules"/>, each drawn cell but the two ends is judged by the route's cell rules
/// (<see cref="CellRules.Breaks"/>) on the world as planned before the run, in run order; the first that breaks one is
/// <see cref="RunPlan.FirstBreak"/>, and the run is laid as drawn. Openings are read from <c>openings</c>; without it,
/// no cell is in an opening's keep-out. Cost: one judgement per cell, each reading the cell and its six neighbours.</item>
/// </list>
/// </summary>
internal sealed class RunPlanner(ICatalogue catalogue, IGrid grid, IPlacementCheck placementCheck, IOpeningFacts? openings = null)
{
    private readonly Shapes _shapes = new(catalogue, grid);
    private readonly PieceChooser _chooser = new(catalogue, grid);
    private readonly PortEnds _ports = new(new PiecePorts(catalogue, new Shapes(catalogue, grid)), RunCodes.Runs);

    /// <summary>
    /// The run, or why it cannot be planned at all (unknown kit, a kit with no straight, a point off the grid, a leg off
    /// one axis, a port end that does not resolve: <see cref="PortEnds"/>).
    /// </summary>
    public Result<RunPlan, Reason> Plan(RunRequest request, IGhostBoard board, IWorldQuery world) => Plan(request, board, world, null);

    /// <summary>
    /// The run laid after <paramref name="laid"/>, the changes of earlier runs of the same request: it meets their pieces
    /// as pieces already planned, and their cells' <paramref name="flows"/> as the flow of the lines they make.
    /// </summary>
    public Result<RunPlan, Reason> Plan(
        RunRequest request, IGhostBoard board, IWorldQuery world, IReadOnlyList<Intent>? laid, IReadOnlyDictionary<Cell, Flow>? flows = null)
    {
        if (!_chooser.Kit(request.Kit).TryGet(out var asked))
            return Result<RunPlan, Reason>.Error(_chooser.Kit(request.Kit).TryGetError(out var why) ? why : Reason.Of(ReasonCodes.NoPiece, RunCodes.Runs));
        var around = new Neighbourhood(_shapes, grid, board, world, laid);
        var kit = request.Lay.MatchGrade ? Grade(asked, request, around) : asked;
        var resolved = _ports.Resolve(request.FromPort, request.From, request.ToPort, request.To, kit, board, world);
        if (!resolved.TryGet(out var ports))
            return Result<RunPlan, Reason>.Error(resolved.ErrorOf());
        var walked = RunPath.Walk(request, kit, grid, ports);
        if (!walked.TryGet(out var path))
            return Result<RunPlan, Reason>.Error(walked.TryGetError(out var bad) ? bad : Reason.Of(ReasonCodes.InvalidValue, RunCodes.Runs));
        var lines = new LineFlows(around, grid, kit, flows);
        var plan = new Laying(kit, path, around, lines, grid, _shapes, new SnapFirst(catalogue, placementCheck), request.Lay.SinglePiecesOnly).Lay();
        if (kit != asked)
            plan = plan with { LaidWith = Option.Some(kit.Kit) };
        if (request.Rules.TryGet(out var rules))
            plan = plan with { FirstBreak = FirstBreak(request, rules, kit, path, ports, board, world) };
        return Result<RunPlan, Reason>.Ok(ports.Count == 0 ? plan : plan with { PortEnds = Joined(plan, ports, around, kit) });
    }

    /// <summary>The first drawn cell, ends left out, that breaks a rule.</summary>
    private Option<CellFailure> FirstBreak(
        RunRequest request, RouteToggles rules, KitShapes kit, RunPath path, IReadOnlyList<PortTarget> ports, IGhostBoard board, IWorldQuery world)
    {
        var planned = new PlannedWorld(board, world, shapes: _shapes);
        var (min, max) = (path.Cells[0], path.Cells[0]);
        foreach (var cell in path.Cells)
        {
            min = new Cell(Math.Min(min.X, cell.X), Math.Min(min.Y, cell.Y), Math.Min(min.Z, cell.Z));
            max = new Cell(Math.Max(max.X, cell.X), Math.Max(max.Y, cell.Y), Math.Max(max.Z, cell.Z));
        }

        var reach = kit.Step;
        var networks = new RouteNetworks(
            planned, _shapes, grid, kit.Grid, new Cell(min.X - reach, min.Y - reach, min.Z - reach), new Cell(max.X + reach, max.Y + reach, max.Z + reach));
        var judge = new CellRules(
            kit, grid, _shapes, planned, rules with { OnFrames = false }, request.From, request.To,
            new OpeningKeepOuts(planned, _shapes, openings ?? NoOpenings.Instance, OpeningBands.Default), new CatalogueSupportFacts(catalogue),
            networks, ports);
        foreach (var cell in path.Cells)
        {
            if (cell == request.From || cell == request.To)
                continue;
            if (judge.Breaks(cell).Word() is { } rule)
                return Option.Some(new CellFailure(cell, Reason.Of(ReasonCodes.RuleBroken, RunCodes.Runs, ValueMap
                    .Of("cell", $"{cell.X},{cell.Y},{cell.Z}")
                    .With("rule", rule))));
        }

        return Option<CellFailure>.None;
    }

    private sealed class NoOpenings : IOpeningFacts
    {
        public static NoOpenings Instance { get; } = new();

        public Option<OpeningKind> Of(PrefabKey piece) => Option<OpeningKind>.None;
    }

    /// <summary>The kit of the run the request joins at its from end, else its to end, when it lays the asked kit's networks; else the asked kit.</summary>
    private KitShapes Grade(KitShapes asked, RunRequest request, Neighbourhood around)
    {
        foreach (var end in new[] { request.From, request.To })
        {
            foreach (var occupant in around.At(end))
                if (Same(asked, occupant.Shape.Piece) is { } kit)
                    return kit;
            for (var direction = Direction.XPlus; direction <= Direction.ZMinus; direction++)
            {
                var next = grid.Neighbour(end, direction, asked.Grid);
                foreach (var occupant in around.At(next))
                {
                    var ends = occupant.Shape.Ends;
                    for (var i = 0; i < ends.Count; i++)
                        if (ends[i].Cell == next && ends[i].Facing == direction.Opposite()
                            && asked.Joins(occupant.Shape.Piece.Ends[i]) && Same(asked, occupant.Shape.Piece) is { } kit)
                            return kit;
                }
            }
        }

        return asked;
    }

    // A kit other than the asked one that places the piece and lays the asked kit's networks.
    private KitShapes? Same(KitShapes asked, PieceFacts piece)
    {
        foreach (var place in piece.Kits)
            if (place.Kit != asked.Kit && _chooser.Kit(place.Kit).TryGet(out var other) && other.Places(piece) && asked.LaysTheSameNetworks(other))
                return other;
        return null;
    }

    /// <summary>For each port end, the piece that leaves its cell back into the port: one the run lays, else the kit's piece already there.</summary>
    private ValueList<PortJoin> Joined(RunPlan plan, ValueList<PortTarget> ports, Neighbourhood around, KitShapes kit)
    {
        var joins = new PortJoin[ports.Count];
        for (var i = 0; i < ports.Count; i++)
        {
            var port = ports[i];
            var by = Option<Placement>.None;
            foreach (var piece in plan.Pieces)
                if (by.IsNone && Leaves(piece, port))
                    by = Option.Some(piece);
            if (by.IsNone && around.OfKit(port.Cell, kit) is { } standing && PieceCells.At(standing, port.Cell, grid).Contains(port.Back))
                by = Option.Some(standing.Shape.Placement);
            joins[i] = new PortJoin(port.End, port.Port, by);
        }

        return ValueList.From(joins);
    }

    private bool Leaves(Placement piece, PortTarget port)
    {
        if (!_shapes.Of(piece).TryGet(out var shape))
            return false;
        foreach (var end in shape.Ends)
            if (end.Cell == port.Cell && end.Facing == port.Back)
                return true;
        return false;
    }

    private sealed class Laying(
        KitShapes kit, RunPath path, Neighbourhood around, LineFlows lines, IGrid grid, Shapes shapes, IPlacementCheck check, bool singlesOnly)
    {
        private readonly List<Intent> _changes = [];
        private readonly List<FlowChoice> _chosen = [];
        private readonly List<Placement> _pieces = [];
        private readonly List<Placement> _singles = [];
        private readonly HashSet<Cell> _done = [];
        private HashSet<Cell>? _path;
        private Option<CellFailure> _failure;

        public RunPlan Lay()
        {
            var joins = JoinsOnLongPieces();
            var visits = path.Visits;
            for (var i = 0; i < visits.Count; i++)
            {
                var cell = visits[i].Cell;
                if (_done.Contains(cell))
                    continue;
                if (around.OfKit(cell, kit) is not { } occupant)
                    i = LayFree(i) - 1;
                else if (occupant.Cells.Count > 1)
                    SplitOrKeep(occupant, joins[occupant.Holder]);
                else
                    Merge(occupant, cell);
            }

            return new RunPlan(
                ValueList.From(path.Cells), ValueList.From(_changes), ValueList.From(_pieces), ValueList.From(_singles), _failure)
            {
                Flows = path.Flows,
                FlowChosen = ValueList.From(_chosen),
            };
        }

        /// <summary>
        /// A cell's flow on the given ways: the run's own (in the way it came, out the way it goes on), and on the ways
        /// it meets a line, the line's where it is readable (<see cref="LineFlows"/>). Read only for a shape the kit
        /// has a piece with roled ends for.
        /// </summary>
        private Flow FlowOf(Cell cell, Directions ways)
        {
            if (!kit.Steers(ways))
                return Flow.None;
            var run = path.FlowAt(cell);
            return run.Union(lines.At(cell, ways.Without(path.WaysAt(cell))));
        }

        /// <summary>The cell's flow on given ways, for a kit with pieces that steer items; none for any other kit.</summary>
        private Func<Directions, Flow>? FlowsAt(Cell cell) => kit.HasSteering ? Steered(cell) : null;

        private Func<Directions, Flow> Steered(Cell cell) => ways => FlowOf(cell, ways);

        /// <summary>
        /// Notes the flow the chosen piece gives a line whose flow could not be read: the way items leave the cell
        /// through the piece's output.
        /// </summary>
        private Choice Noted(Cell cell, Choice choice)
        {
            if (!kit.Steers(choice.Ways))
                return choice;
            var unknown = choice.Ways.Without(path.WaysAt(cell)).Without(FlowOf(cell, choice.Ways).Known);
            if (unknown.IsEmpty || !shapes.Of(choice.Placement).TryGet(out var shape))
                return choice;
            var outs = Steering.Of(shape.Piece, choice.Placement.Turn, grid).Outs;
            for (var way = Direction.XPlus; way <= Direction.ZMinus; way++)
                if (outs.Contains(way))
                {
                    _chosen.Add(new FlowChoice(cell, way.ToFace()));
                    break;
                }

            return choice;
        }

        /// <summary>The run's ways on each cell of every multi-cell piece of the kit it reaches, by holder.</summary>
        private Dictionary<Holder, Dictionary<Cell, Directions>> JoinsOnLongPieces()
        {
            var joins = new Dictionary<Holder, Dictionary<Cell, Directions>>();
            foreach (var cell in path.Cells)
            {
                if (around.OfKit(cell, kit) is not { Cells.Count: > 1 } occupant)
                    continue;
                if (!joins.TryGetValue(occupant.Holder, out var onPiece))
                    joins[occupant.Holder] = onPiece = [];
                var ways = PieceCells.At(occupant, cell, grid).Union(path.WaysAt(cell));
                var withNeighbours = ways.Union(around.EndsInto(cell, kit));
                onPiece[cell] = kit.For(withNeighbours, FlowOf(cell, withNeighbours)).IsSome ? withNeighbours : ways;
            }

            return joins;
        }

        private void SplitOrKeep(Occupant occupant, Dictionary<Cell, Directions> joins)
        {
            foreach (var cell in occupant.Cells)
                _done.Add(cell);
            if (Keeps(occupant, joins))
                return;

            var split = SplitPlanner.Split(kit, occupant, joins, grid, kit.HasSteering ? FlowOf : null);
            if (!split.TryGet(out var pieces))
            {
                if (split.TryGetError(out var why))
                    Fail(FirstCell(joins), why);
                return;
            }

            var standing = occupant.Holder.Standing;
            if (standing.TryGet(out var old))
                _changes.Add(new Intent.Remove(old));
            foreach (var piece in pieces)
            {
                Noted(piece.Placement.At, piece);
                Build(piece.Placement, checkIt: standing.IsNone);
                _singles.Add(piece.Placement);
            }
        }

        private bool Keeps(Occupant occupant, Dictionary<Cell, Directions> joins)
        {
            foreach (var (cell, ways) in joins)
                if (PieceCells.At(occupant, cell, grid) != ways)
                    return false;
            return true;
        }

        private void Merge(Occupant occupant, Cell cell)
        {
            _done.Add(cell);
            var had = PieceCells.At(occupant, cell, grid);
            var run = path.WaysAt(cell);
            var chosen = Turned(cell, had, run) ?? Pick(cell, had.Union(run));
            if (chosen is null || chosen.Ways == had)
                return;

            if (occupant.Holder.Standing.TryGet(out var old))
            {
                _changes.Add(new Intent.Swap(old, chosen.Placement, Option<Finish>.None));
                _pieces.Add(chosen.Placement);
            }
            else
                Build(chosen.Placement, checkIt: true);

            _singles.Add(chosen.Placement);
        }

        /// <summary>
        /// The piece for a line's end the run leaves: the occupant's faces that join nothing (no neighbour's end points
        /// in and the run lays no cell there) are dropped, so the run turns the end instead of leaving an open stub.
        /// Null when nothing is dropped, when the occupant joins no neighbour (it is no line's end), or when the kit has
        /// no piece for what is left.
        /// </summary>
        private Choice? Turned(Cell cell, Directions had, Directions run)
        {
            var into = around.EndsInto(cell, kit);
            if (had.Intersect(into).IsEmpty)
                return null;
            var kept = run;
            for (var direction = Direction.XPlus; direction <= Direction.ZMinus; direction++)
                if (had.Contains(direction) && (into.Contains(direction) || OnPath(grid.Neighbour(cell, direction, kit.Grid))))
                    kept = kept.With(direction);
            return !kept.Covers(had) && PieceChooser.Choose(kit, cell, kept, around, FlowsAt(cell)).TryGet(out var choice)
                ? Noted(cell, choice)
                : null;
        }

        private bool OnPath(Cell cell) => (_path ??= [.. path.Cells]).Contains(cell);

        /// <summary>Lays the free cells from visit <paramref name="start"/> on; returns the index of the first visit not laid.</summary>
        private int LayFree(int start)
        {
            var visits = path.Visits;
            var cell = visits[start].Cell;
            _done.Add(cell);
            var heading = visits[start].Heading;
            var straight = Directions.Along(heading.AxisOf());
            if (!Lies(Ways(cell), straight) || kit.Longs.Count == 0 || singlesOnly)
            {
                if (Pick(cell, path.WaysAt(cell)) is { } single)
                    BuildChecked(single);
                return start + 1;
            }

            var stretch = new List<Cell>(8) { cell };
            var next = start + 1;
            while (next < visits.Count && visits[next].Heading == heading
                && visits[next].Cell == grid.Neighbour(stretch[stretch.Count - 1], heading, kit.Grid)
                && !_done.Contains(visits[next].Cell) && around.OfKit(visits[next].Cell, kit) is null
                && Lies(Ways(visits[next].Cell), straight))
            {
                stretch.Add(visits[next].Cell);
                _done.Add(visits[next].Cell);
                next++;
            }

            LayStretch(stretch, heading);
            return next;
        }

        private void LayStretch(List<Cell> stretch, Direction heading)
        {
            var along = Directions.Along(heading.AxisOf());
            var single = PieceChooser.Exactly(kit, stretch[0], along, new Flow(Directions.Of(heading.Opposite()), Directions.Of(heading)))
                ?? PieceChooser.Exactly(kit, stretch[0], along)!;
            for (var at = 0; at < stretch.Count;)
            {
                var laid = 0;
                foreach (var line in kit.Longs)
                {
                    if (line.Cells > stretch.Count - at
                        || !kit.Lay(line, stretch[at], stretch[at + line.Cells - 1], heading).TryGet(out var placement)
                        || !Judge(placement).IsOk)
                        continue;
                    _changes.Add(new Intent.Build(placement, Option<Finish>.None));
                    _pieces.Add(placement);
                    laid = line.Cells;
                    break;
                }

                if (laid == 0)
                {
                    BuildChecked(single with { Placement = single.Placement with { At = stretch[at] } });
                    laid = 1;
                }
                else
                    for (var cell = at; cell < at + laid; cell++)
                        _singles.Add(single.Placement with { At = stretch[cell] });

                at += laid;
            }
        }

        private void BuildChecked(Choice choice)
        {
            Build(choice.Placement, checkIt: true);
            _singles.Add(choice.Placement);
        }

        private void Build(Placement placement, bool checkIt)
        {
            if (checkIt && Judge(placement).TryGetError(out var refused))
                Fail(placement.At, refused);
            _changes.Add(new Intent.Build(placement, Option<Finish>.None));
            _pieces.Add(placement);
        }

        /// <summary>
        /// Whether the piece may stand here: first the built pieces in its cells, judged by the small-cell clash rule
        /// (<see cref="Shapes.Conflict"/>) as a ghost's are, then the game's placement check.
        /// </summary>
        private Result<Unit, Reason> Judge(Placement placement) =>
            BuiltClash(placement) is { } clash ? Result<Unit, Reason>.Error(clash) : check.Check(placement);

        /// <summary>The first built piece of another kit the piece may not share a small cell with, as a reason naming it.</summary>
        private Reason? BuiltClash(Placement placement)
        {
            if (!shapes.Of(placement).TryGet(out var mine))
                return null;
            foreach (var cell in PieceCells.Of(mine, grid))
                foreach (var occupant in around.At(cell))
                    if (occupant.Holder is Holder.Real built && !kit.Places(occupant.Shape.Piece)
                        && Shapes.Conflict(mine, occupant.Shape).TryGet(out var code))
                        return Reason.Of(code, RunCodes.Runs, ValueMap
                            .Of("piece", placement.Piece.Hash.ToString())
                            .With("with", built.Structure.Piece.Hash.ToString())
                            .With("structure", built.Structure.Id.Value.ToString()));
            return null;
        }

        /// <summary>The ways a free cell must leave: the run's, plus the neighbours pointing in when the kit has that shape.</summary>
        private Directions Ways(Cell cell)
        {
            var run = path.WaysAt(cell);
            var all = run.Union(around.EndsInto(cell, kit));
            return kit.For(all, FlowOf(cell, all)).IsSome ? all : run;
        }

        /// <summary>Whether a free cell's ways fit a straight: both ways along it, or one (a run's end), or none.</summary>
        private static bool Lies(Directions ways, Directions straight) => ways == straight || (ways.Count <= 1 && straight.Covers(ways));

        private Choice? Pick(Cell cell, Directions needed)
        {
            var chosen = PieceChooser.Choose(kit, cell, needed, around, FlowsAt(cell));
            if (!chosen.TryGet(out var choice))
            {
                if (chosen.TryGetError(out var why))
                    Fail(cell, why);
                return null;
            }

            return Noted(cell, choice);
        }

        private Cell FirstCell(Dictionary<Cell, Directions> joins)
        {
            foreach (var cell in path.Cells)
                if (joins.ContainsKey(cell))
                    return cell;
            return path.Cells[0];
        }

        private void Fail(Cell cell, Reason why)
        {
            if (_failure.IsNone)
                _failure = Option.Some(new CellFailure(cell, why));
        }
    }
}
