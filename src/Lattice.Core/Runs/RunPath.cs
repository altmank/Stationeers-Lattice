using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Grid;
using Lattice.Core.Ports;

namespace Lattice.Core.Runs;

/// <summary>One step of a run: the cell, and the way the run travels through it (the way it entered, for the last cell).</summary>
internal readonly record struct Visit(Cell Cell, Direction Heading);

/// <summary>
/// The cells of a drawn run: straight legs between From, the bend cells in order and To. A cell the run passes twice
/// keeps one entry and leaves every way the run takes through it (a self-crossing gives a cross there). Items flow from
/// From to To: each cell's <see cref="FlowAt"/> enters the way the run came from and leaves the way it goes on.
/// </summary>
internal sealed class RunPath
{
    private readonly Dictionary<Cell, Directions> _ways = [];
    private readonly Dictionary<Cell, Flow> _flows = [];
    private readonly List<Visit> _visits = [];
    private readonly List<Cell> _cells = [];

    private RunPath() { }

    /// <summary>Every step in drawn order, a cell passed twice listed twice.</summary>
    public IReadOnlyList<Visit> Visits => _visits;

    /// <summary>The cells, each once, in the order the run first reaches them.</summary>
    public IReadOnlyList<Cell> Cells => _cells;

    /// <summary>The ways the run leaves a cell.</summary>
    public Directions WaysAt(Cell cell) => _ways.TryGetValue(cell, out var ways) ? ways : Directions.None;

    /// <summary>The ways items enter and leave a cell along the run.</summary>
    public Flow FlowAt(Cell cell) => _flows.TryGetValue(cell, out var flow) ? flow : Flow.None;

    /// <summary>Every cell's flow along the run.</summary>
    public IReadOnlyDictionary<Cell, Flow> Flows => _flows;

    /// <summary>
    /// The run's cells; refused (<see cref="ReasonCodes.InvalidValue"/>) when a point is off the kit's grid
    /// (<c>off-grid</c>) or a leg is not along one axis (<c>not-straight</c>): finding a path is the route planner's.
    /// An end at a port also leaves back into the port, so its piece joins it whatever lies around. Items come out of
    /// a port at From and go into one at To, unless the port's role says otherwise (an output gives, an input takes).
    /// </summary>
    public static Result<RunPath, Reason> Walk(RunRequest request, KitShapes kit, IGrid grid, IReadOnlyList<PortTarget>? ports = null)
    {
        var walked = Walk(request, kit, grid);
        if (ports is null || !walked.TryGet(out var path))
            return walked;
        foreach (var port in ports)
        {
            path.Join(port.Cell, port.Back);
            var gives = port.Port.Role.TryGet(out var role)
                ? role is not (PortRole.Input or PortRole.Input2)
                : port.Cell == request.From;
            path.Flowing(port.Cell, port.Back, entering: gives);
        }

        return walked;
    }

    private static Result<RunPath, Reason> Walk(RunRequest request, KitShapes kit, IGrid grid)
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
                path.Flowing(cell, heading, entering: false);
                path.Reach(next, heading);
                path.Join(next, heading.Opposite());
                path.Flowing(next, heading.Opposite(), entering: true);
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

    private void Flowing(Cell cell, Direction way, bool entering) =>
        _flows[cell] = entering ? FlowAt(cell).Entering(way) : FlowAt(cell).Leaving(way);

    private static Result<RunPath, Reason> Refused(Cell cell, string problem) =>
        Result<RunPath, Reason>.Error(Reason.Of(ReasonCodes.InvalidValue, RunCodes.Runs, ValueMap
            .Of("cell", $"{cell.X},{cell.Y},{cell.Z}")
            .With("problem", problem)));
}
