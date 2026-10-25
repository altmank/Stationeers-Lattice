using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Grid;
using Lattice.Core.Ports;

namespace Lattice.Core.Runs;

/// <summary>One step of a run: the cell, and the way the run travels through it (the way it entered, for the last cell).</summary>
internal readonly record struct Visit(Cell Cell, Direction Heading);

/// <summary>
/// The cells of a drawn run: straight legs between From, the bend cells in order and To. A cell the run passes twice
/// keeps one entry and leaves every way the run takes through it (a self-crossing gives a cross there).
/// </summary>
internal sealed class RunPath
{
    private readonly Dictionary<Cell, Directions> _ways = [];
    private readonly List<Visit> _visits = [];
    private readonly List<Cell> _cells = [];

    private RunPath() { }

    /// <summary>Every step in drawn order, a cell passed twice listed twice.</summary>
    public IReadOnlyList<Visit> Visits => _visits;

    /// <summary>The cells, each once, in the order the run first reaches them.</summary>
    public IReadOnlyList<Cell> Cells => _cells;

    /// <summary>The ways the run leaves a cell.</summary>
    public Directions WaysAt(Cell cell) => _ways.TryGetValue(cell, out var ways) ? ways : Directions.None;

    /// <summary>
    /// The run's cells; refused (<see cref="ReasonCodes.InvalidValue"/>) when a point is off the kit's grid
    /// (<c>off-grid</c>) or a leg is not along one axis (<c>not-straight</c>): finding a path is the route planner's.
    /// </summary>
    public static Result<RunPath, Reason> Walk(RunRequest request, KitShapes kit, IGrid grid)
    {
        var points = new List<Cell>(request.Via.Count + 2) { request.From };
        points.AddRange(request.Via);
        points.Add(request.To);
        foreach (var point in points)
            if (grid.Snap(grid.CentreOf(point, kit.Grid), kit.Grid) != point)
                return Refused(point, "off-grid");

        var path = new RunPath();
        path.Reach(request.From, Direction.ZPlus);
        for (var leg = 1; leg < points.Count; leg++)
        {
            var (from, to) = (points[leg - 1], points[leg]);
            var delta = new CellOffset(to.X - from.X, to.Y - from.Y, to.Z - from.Z);
            var axes = (delta.X != 0 ? 1 : 0) + (delta.Y != 0 ? 1 : 0) + (delta.Z != 0 ? 1 : 0);
            if (axes == 0)
                continue;
            if (axes > 1)
                return Refused(to, "not-straight");

            var heading = DirectionMath.FromUnit(new CellOffset(System.Math.Sign(delta.X), System.Math.Sign(delta.Y), System.Math.Sign(delta.Z)));
            if (path._visits.Count == 1)
                path._visits[0] = new Visit(from, heading);
            for (var cell = from; cell != to;)
            {
                var next = grid.Neighbour(cell, heading, kit.Grid);
                path.Join(cell, heading);
                path.Reach(next, heading);
                path.Join(next, heading.Opposite());
                cell = next;
            }
        }

        return Result<RunPath, Reason>.Ok(path);
    }

    private void Reach(Cell cell, Direction heading)
    {
        _visits.Add(new Visit(cell, heading));
        if (_ways.ContainsKey(cell))
            return;
        _ways[cell] = Directions.None;
        _cells.Add(cell);
    }

    private void Join(Cell cell, Direction way) => _ways[cell] = WaysAt(cell).With(way);

    private static Result<RunPath, Reason> Refused(Cell cell, string problem) =>
        Result<RunPath, Reason>.Error(Reason.Of(ReasonCodes.InvalidValue, RunCodes.Runs, ValueMap
            .Of("cell", $"{cell.X},{cell.Y},{cell.Z}")
            .With("problem", problem)));
}
