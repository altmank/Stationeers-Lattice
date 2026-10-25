using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Grid;
using Lattice.Core.Ports;

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

    /// <summary>The run's report, its costs from the given bill (the bills module's).</summary>
    public RunReport Report(Func<ValueList<Placement>, Bill> bill) => new(Cells, Pieces, FirstFailure, bill(Pieces), bill(Singles));
}

/// <summary>
/// Lays a run of cable, pipe or chute between drawn points. Cost: linear in the run's cells times the kit's long
/// straights (one game placement check per piece tried); main thread, as the placement check is.
/// <list type="bullet">
/// <item>Each cell gets the kit's piece for the ways the run leaves it plus the neighbours' ends that point in (the piece chooser).</item>
/// <item>A cell already holding the kit's piece merges: same ways, left as is; a one-cell piece is swapped (real) or replaced (ghost) by the merged shape; a piece over several cells is split where the run joins it (the split planner): a real one gets a removal ghost and build ghosts for its cells, a ghost is replaced by them.</item>
/// <item>Free straight stretches take the kit's longest straights first, as the game's cable gun lays them.</item>
/// <item>Every new piece in a free or ghost-held cell goes through the game's placement check; a long straight it refuses is tried shorter, down to single pieces, so a failure names one cell and the game's reason. Pieces over a real piece being replaced are not checked: the game's cursor would see the old piece. A failing cell still gets its ghost: validation reports it, and a planned removal may clear the cell first.</item>
/// <item>A cell the kit has no piece for gets none and fails with <see cref="ReasonCodes.NoPiece"/>.</item>
/// </list>
/// </summary>
internal sealed class RunPlanner(ICatalogue catalogue, IGrid grid, IPlacementCheck placementCheck)
{
    private readonly Shapes _shapes = new(catalogue, grid);
    private readonly PieceChooser _chooser = new(catalogue, grid);

    /// <summary>The run, or why it cannot be planned at all (unknown kit, a kit with no straight, a point off the grid, a leg off one axis).</summary>
    public Result<RunPlan, Reason> Plan(RunRequest request, IGhostBoard board, IWorldQuery world)
    {
        if (!_chooser.Kit(request.Kit).TryGet(out var kit))
            return Result<RunPlan, Reason>.Error(_chooser.Kit(request.Kit).TryGetError(out var why) ? why : Reason.Of(ReasonCodes.NoPiece, RunCodes.Runs));
        var walked = RunPath.Walk(request, kit, grid);
        if (!walked.TryGet(out var path))
            return Result<RunPlan, Reason>.Error(walked.TryGetError(out var bad) ? bad : Reason.Of(ReasonCodes.InvalidValue, RunCodes.Runs));
        var around = new Neighbourhood(_shapes, grid, board, world);
        return Result<RunPlan, Reason>.Ok(new Laying(kit, path, around, grid, placementCheck).Lay());
    }

    private sealed class Laying(KitShapes kit, RunPath path, Neighbourhood around, IGrid grid, IPlacementCheck check)
    {
        private readonly List<Intent> _changes = [];
        private readonly List<Placement> _pieces = [];
        private readonly List<Placement> _singles = [];
        private readonly HashSet<Cell> _done = [];
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
                ValueList.From(path.Cells), ValueList.From(_changes), ValueList.From(_pieces), ValueList.From(_singles), _failure);
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
                onPiece[cell] = kit.For(withNeighbours).IsSome ? withNeighbours : ways;
            }

            return joins;
        }

        private void SplitOrKeep(Occupant occupant, Dictionary<Cell, Directions> joins)
        {
            foreach (var cell in occupant.Cells)
                _done.Add(cell);
            if (Keeps(occupant, joins))
                return;

            var split = SplitPlanner.Split(kit, occupant, joins, grid);
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
            var needed = had.Union(path.WaysAt(cell));
            var chosen = Pick(cell, needed);
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

        /// <summary>Lays the free cells from visit <paramref name="start"/> on; returns the index of the first visit not laid.</summary>
        private int LayFree(int start)
        {
            var visits = path.Visits;
            var cell = visits[start].Cell;
            _done.Add(cell);
            var heading = visits[start].Heading;
            var straight = Directions.Along(heading.AxisOf());
            if (!Lies(Ways(cell), straight) || kit.Longs.Count == 0)
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
            var single = PieceChooser.Exactly(kit, stretch[0], Directions.Along(heading.AxisOf()))!;
            for (var at = 0; at < stretch.Count;)
            {
                var laid = 0;
                foreach (var line in kit.Longs)
                {
                    if (line.Cells > stretch.Count - at
                        || !kit.Lay(line, stretch[at], stretch[at + line.Cells - 1], heading).TryGet(out var placement)
                        || !check.Check(placement).IsOk)
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
            if (checkIt && check.Check(placement).TryGetError(out var refused))
                Fail(placement.At, refused);
            _changes.Add(new Intent.Build(placement, Option<Finish>.None));
            _pieces.Add(placement);
        }

        /// <summary>The ways a free cell must leave: the run's, plus the neighbours pointing in when the kit has that shape.</summary>
        private Directions Ways(Cell cell)
        {
            var run = path.WaysAt(cell);
            var all = run.Union(around.EndsInto(cell, kit));
            return kit.For(all).IsSome ? all : run;
        }

        /// <summary>Whether a free cell's ways fit a straight: both ways along it, or one (a run's end), or none.</summary>
        private static bool Lies(Directions ways, Directions straight) => ways == straight || (ways.Count <= 1 && straight.Covers(ways));

        private Choice? Pick(Cell cell, Directions needed)
        {
            var chosen = PieceChooser.Choose(kit, cell, needed, around);
            if (!chosen.TryGet(out var choice))
            {
                if (chosen.TryGetError(out var why))
                    Fail(cell, why);
                return null;
            }

            return choice;
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
