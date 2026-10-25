using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Grid;
using Lattice.Core.Ports;
using Lattice.Core.Slices;

namespace Lattice.Core.Routes;

/// <summary>Whether a route may step into a cell travelling one way. The search's only question about the world.</summary>
internal interface IRouteCells
{
    bool Enters(Cell cell, Direction heading);
}

/// <summary>What a route search found: the cells end to end and the bends among them, or why it found none.</summary>
internal abstract record RouteOutcome
{
    private RouteOutcome() { }

    /// <param name="Cells">Every cell from one end to the other, in order.</param>
    /// <param name="Bends">How many times the route turns.</param>
    /// <param name="Expanded">The search states taken.</param>
    public sealed record Found(ValueList<Cell> Cells, int Bends, int Expanded) : RouteOutcome;

    /// <param name="Why"><see cref="ReasonCodes.NoRoute"/> or <see cref="ReasonCodes.SearchLimit"/>, with the nearest cell reached in its facts.</param>
    /// <param name="Nearest">The cell reached closest to the far end (fewest cells left; then the cheapest).</param>
    /// <param name="Expanded">The search states taken.</param>
    public sealed record NotFound(Reason Why, Cell Nearest, int Expanded) : RouteOutcome;
}

/// <summary>
/// The cheapest route between two cells of a kit's grid, one cell step at a time along the six axis directions (A*
/// over cell and heading). A route's cost is two counts compared in order: cells then bends by default, so the
/// shortest route wins and fewer bends break ties (fewer bends also means longer straights); bends then cells under
/// <c>FewestBends</c>. The heuristic (cells left; bends at least needed) never overestimates either count, and
/// states are reopened when reached cheaper, so the route found is the cheapest. Equal costs are taken in the order
/// they were reached, so one request always gives one route. Every cell entered but the far end must be inside the
/// search box and pass <see cref="IRouteCells"/>; the first cell is where the route stands.
/// Resumable: each <see cref="Step"/> takes up to <see cref="RouteLimits.PerStep"/> states, so the search runs inside
/// a frame's slice; <see cref="Find"/> runs it to the end. Allocates its tables once per search.
/// </summary>
internal sealed class RouteSearch : ISlicedWork
{
    private readonly IGrid _grid;
    private readonly GridSize _size;
    private readonly int _step;
    private readonly Cell _from;
    private readonly Cell _to;
    private readonly IRouteCells _cells;
    private readonly bool _fewestBends;
    private readonly Cell _min;
    private readonly Cell _max;
    private readonly int _bound;
    private readonly int _perStep;
    private readonly int _margin;

    private readonly Dictionary<(Cell Cell, int Heading), int> _index = [];
    private readonly List<State> _states = [];
    private readonly Heap _open = new();
    private int _expanded;
    private int _nearest;

    public RouteSearch(IGrid grid, GridSize size, Cell from, Cell to, IRouteCells cells, bool fewestBends, RouteLimits limits)
    {
        _grid = grid;
        _size = size;
        _step = grid.Neighbour(default, Direction.XPlus, size).X;
        _from = from;
        _to = to;
        _cells = cells;
        _fewestBends = fewestBends;
        _margin = limits.Margin;
        var reach = _step * limits.Margin;
        _min = new Cell(Math.Min(from.X, to.X) - reach, Math.Min(from.Y, to.Y) - reach, Math.Min(from.Z, to.Z) - reach);
        _max = new Cell(Math.Max(from.X, to.X) + reach, Math.Max(from.Y, to.Y) + reach, Math.Max(from.Z, to.Z) + reach);
        _bound = limits.ExpansionsFor(Distance(from));
        _perStep = Math.Max(1, limits.PerStep);
        Reach(new State(from, Heading: 0, Cells: 1, Bends: 0, From: -1));
    }

    /// <summary>The outcome once the search has ended; none while it runs.</summary>
    public Option<RouteOutcome> Outcome { get; private set; }

    /// <summary>The search states taken so far.</summary>
    public int Expanded => _expanded;

    /// <summary>Takes up to <see cref="RouteLimits.PerStep"/> states; done once the route is found or the search has ended without one.</summary>
    public StepOutcome Step()
    {
        for (var taken = 0; taken < _perStep && Outcome.IsNone; taken++)
            Expand();
        return Outcome.IsSome ? StepOutcome.Done : StepOutcome.More;
    }

    /// <summary>Runs the search to its end in one call.</summary>
    public RouteOutcome Find()
    {
        while (Step() == StepOutcome.More)
        {
        }

        return Outcome.TryGet(out var outcome) ? outcome : throw new InvalidOperationException("search ended without an outcome");
    }

    private void Expand()
    {
        if (!_open.TryPop(out var at))
        {
            Outcome = Option.Some(NotFound(ReasonCodes.NoRoute));
            return;
        }

        var state = _states[at];
        if (state.Closed)
            return;
        _states[at] = state with { Closed = true };
        if (state.Cell == _to)
        {
            var cells = Trace(at);
            Outcome = Option.Some<RouteOutcome>(new RouteOutcome.Found(cells, BendsOf(cells), _expanded));
            return;
        }

        if (_expanded >= _bound)
        {
            Outcome = Option.Some(NotFound(ReasonCodes.SearchLimit));
            return;
        }

        _expanded++;
        for (var direction = Direction.XPlus; direction <= Direction.ZMinus; direction++)
        {
            var heading = (int)direction;
            if (state.Heading != 0 && heading == (int)((Direction)state.Heading).Opposite())
                continue;
            var next = _grid.Neighbour(state.Cell, direction, _size);
            if (next != _to && (!Inside(next) || !_cells.Enters(next, direction)))
                continue;
            var bends = state.Bends + (state.Heading != 0 && state.Heading != heading ? 1 : 0);
            Reach(new State(next, heading, state.Cells + 1, bends, at));
        }
    }

    private void Reach(State state)
    {
        var key = (state.Cell, state.Heading);
        if (_index.TryGetValue(key, out var known))
        {
            var old = _states[known];
            if (Compare(state, old) >= 0)
                return;
            _states[known] = state;
            Push(known, state);
            return;
        }

        var index = _states.Count;
        _states.Add(state);
        _index[key] = index;
        if (_states.Count == 1 || Closer(state, _states[_nearest]))
            _nearest = index;
        Push(index, state);
    }

    private void Push(int index, State state)
    {
        var left = Distance(state.Cell);
        var bendsLeft = BendsLeft(state.Cell, state.Heading);
        var cells = state.Cells + left;
        var bends = state.Bends + bendsLeft;
        _open.Push(_fewestBends ? bends : cells, _fewestBends ? cells : bends, index);
    }

    private int Compare(State a, State b)
    {
        var (a1, a2) = _fewestBends ? (a.Bends, a.Cells) : (a.Cells, a.Bends);
        var (b1, b2) = _fewestBends ? (b.Bends, b.Cells) : (b.Cells, b.Bends);
        return a1 != b1 ? a1.CompareTo(b1) : a2.CompareTo(b2);
    }

    private bool Closer(State state, State best)
    {
        var (mine, theirs) = (Distance(state.Cell), Distance(best.Cell));
        return mine != theirs ? mine < theirs : Compare(state, best) < 0;
    }

    /// <summary>Cells from the cell to the far end along the axes.</summary>
    private int Distance(Cell cell) => (Math.Abs(_to.X - cell.X) + Math.Abs(_to.Y - cell.Y) + Math.Abs(_to.Z - cell.Z)) / _step;

    /// <summary>
    /// The fewest bends still needed: one fewer than the axes left to cover, plus one when the route heads along none
    /// of them the right way (it must turn before it can close in).
    /// </summary>
    private int BendsLeft(Cell cell, int heading)
    {
        var (dx, dy, dz) = (_to.X - cell.X, _to.Y - cell.Y, _to.Z - cell.Z);
        var axes = (dx != 0 ? 1 : 0) + (dy != 0 ? 1 : 0) + (dz != 0 ? 1 : 0);
        if (axes == 0)
            return 0;
        if (heading == 0)
            return axes - 1;
        var unit = ((Direction)heading).Unit();
        var closing = unit.X * dx > 0 || unit.Y * dy > 0 || unit.Z * dz > 0;
        return axes - 1 + (closing ? 0 : 1);
    }

    private bool Inside(Cell cell) =>
        cell.X >= _min.X && cell.X <= _max.X && cell.Y >= _min.Y && cell.Y <= _max.Y && cell.Z >= _min.Z && cell.Z <= _max.Z;

    private ValueList<Cell> Trace(int end)
    {
        var cells = new List<Cell>(_states[end].Cells);
        for (var at = end; at >= 0; at = _states[at].From)
            cells.Add(_states[at].Cell);
        cells.Reverse();
        return ValueList.From(cells);
    }

    private static int BendsOf(ValueList<Cell> cells)
    {
        var bends = 0;
        for (var i = 2; i < cells.Count; i++)
        {
            var (a, b, c) = (cells[i - 2], cells[i - 1], cells[i]);
            if (Math.Sign(b.X - a.X) != Math.Sign(c.X - b.X) || Math.Sign(b.Y - a.Y) != Math.Sign(c.Y - b.Y)
                || Math.Sign(b.Z - a.Z) != Math.Sign(c.Z - b.Z))
                bends++;
        }

        return bends;
    }

    private RouteOutcome NotFound(ReasonCode code)
    {
        var nearest = _states[_nearest].Cell;
        var why = Reason.Of(code, RouteCodes.Routes, ValueMap
            .Of("from", Written(_from))
            .With("to", Written(_to))
            .With("nearest", Written(nearest))
            .With("cells-left", Distance(nearest).ToString(System.Globalization.CultureInfo.InvariantCulture))
            .With("expanded", _expanded.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .With("bound", _bound.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .With("margin", _margin.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return new RouteOutcome.NotFound(why, nearest, _expanded);
    }

    private static string Written(Cell cell) => $"{cell.X},{cell.Y},{cell.Z}";

    /// <summary>A reached state: its cell, the way it was entered (0 at the first cell), its counts so far, the state before it.</summary>
    private readonly record struct State(Cell Cell, int Heading, int Cells, int Bends, int From)
    {
        public bool Closed { get; init; }
    }

    /// <summary>A binary min-heap on two counts, then the order pushed.</summary>
    private sealed class Heap
    {
        private readonly List<(int First, int Second, long Order, int Index)> _items = new(256);
        private long _pushed;

        public void Push(int first, int second, int index)
        {
            _items.Add((first, second, _pushed++, index));
            var at = _items.Count - 1;
            while (at > 0)
            {
                var parent = (at - 1) / 2;
                if (!Less(at, parent))
                    break;
                (_items[at], _items[parent]) = (_items[parent], _items[at]);
                at = parent;
            }
        }

        public bool TryPop(out int index)
        {
            if (_items.Count == 0)
            {
                index = -1;
                return false;
            }

            index = _items[0].Index;
            var last = _items.Count - 1;
            _items[0] = _items[last];
            _items.RemoveAt(last);
            var at = 0;
            while (true)
            {
                var (left, right, smallest) = (2 * at + 1, 2 * at + 2, at);
                if (left < _items.Count && Less(left, smallest))
                    smallest = left;
                if (right < _items.Count && Less(right, smallest))
                    smallest = right;
                if (smallest == at)
                    break;
                (_items[at], _items[smallest]) = (_items[smallest], _items[at]);
                at = smallest;
            }

            return true;
        }

        private bool Less(int a, int b)
        {
            var (x, y) = (_items[a], _items[b]);
            return x.First != y.First ? x.First < y.First : x.Second != y.Second ? x.Second < y.Second : x.Order < y.Order;
        }
    }
}
