using System;
using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Render.Marks;

/// <summary>One port of a drawn piece: the small cell it leaves from, the way it points, and whether nothing joins it.</summary>
internal readonly record struct EndMark(Cell From, Face Toward, bool Open);

/// <summary>A flow arrow across a port's face: the port's small cell, the way the port points, and whether the flow goes into the piece there.</summary>
internal readonly record struct FlowMark(Cell At, Face Toward, bool Inward);

/// <summary>
/// What Render's marks are drawn from: ports and joins for <see cref="Style.Ports"/> and <see cref="Style.OpenEnds"/>,
/// flow for <see cref="Style.FlowArrows"/>, and where a waited-for ghost stands for <see cref="Style.WaitsLinks"/>.
/// Main thread.
/// </summary>
internal interface IMarkFacts
{
    /// <summary>Rises whenever joins, flow, findings or ghosts' places may have changed; marks drawn at an older stamp are worked out again.</summary>
    long Stamp { get; }

    /// <summary>Adds the ports of a piece at a placement to <paramref name="into"/>, in the piece's port order; none for a piece without ports.</summary>
    void Ends(Placement placement, List<EndMark> into);

    /// <summary>Adds the flow arrows of a piece at a placement to <paramref name="into"/>; none where the flow is not known.</summary>
    void Flow(Placement placement, List<FlowMark> into);

    /// <summary>The cell a ghost the viewer sees stands at: its new piece's, else the structure it works on.</summary>
    bool CellOf(GhostId ghost, out Cell cell);
}

/// <summary>
/// Marks from a running world, as its viewer sees it.
/// <para>
/// A port is joined when a piece at the cell it joins has a port leaving that cell back toward it with a network kind
/// in common: a build or swap ghost's new piece, or a built structure no remove or swap ghost takes away. The pieces are
/// found in the port's joining cell and the large cell around it, so a device that fills that cell is found too.
/// </para>
/// <para>
/// Flow follows the catalogue's port roles: into a piece at an input port, out of it at an output or waste port. A run
/// piece (two ports, neither with a role) takes its flow from the run it is in, followed through joined run pieces up to
/// <see cref="MostTraced"/> each way: away from a device output it reaches, toward a device input. A run that reaches
/// both or neither ways, or a junction, has no known flow and no arrow.
/// </para>
/// Worked out on first ask per placement and kept until <see cref="Stamp"/> rises: on a board change, when validation
/// has new answers (new findings come with them, and it asks again after the built world changes near ghosts), and when
/// structures move. A repeated ask at an unchanged stamp allocates nothing.
/// </summary>
internal sealed class WorldMarkFacts : IMarkFacts, IDisposable
{
    public const int MostTraced = 64;

    private readonly IPieceCatalogue _catalogue;
    private readonly IGhostBoard _board;
    private readonly IWorldQuery _world;
    private readonly IGhostEvents _events;
    private readonly Dictionary<Placement, EndMark[]> _ends = new(64);
    private readonly Dictionary<Placement, FlowMark[]> _flows = new(64);
    private long _bumps;
    private long _knownAt = -1;

    public WorldMarkFacts(IPieceCatalogue catalogue, IGhostBoard board, IWorldQuery world, IGhostEvents events)
    {
        _catalogue = catalogue;
        _board = board;
        _world = world;
        _events = events;
        events.AnswersReady += OnAnswers;
        events.StructuresMoved += OnMoved;
    }

    public long Stamp => _board.Revision.Value + _bumps;

    public void Ends(Placement placement, List<EndMark> into)
    {
        Fresh();
        if (!_ends.TryGetValue(placement, out var marks))
        {
            marks = WorkEnds(placement);
            _ends[placement] = marks;
        }

        for (var i = 0; i < marks.Length; i++)
            into.Add(marks[i]);
    }

    public void Flow(Placement placement, List<FlowMark> into)
    {
        Fresh();
        if (!_flows.TryGetValue(placement, out var marks))
        {
            marks = WorkFlow(placement);
            _flows[placement] = marks;
        }

        for (var i = 0; i < marks.Length; i++)
            into.Add(marks[i]);
    }

    public bool CellOf(GhostId ghost, out Cell cell)
    {
        cell = default;
        if (!_board.Find(ghost).TryGet(out var found))
            return false;
        switch (found.Intent)
        {
            case Intent.Build build:
                cell = build.Target.At;
                return true;
            case Intent.Swap swap:
                cell = swap.New.At;
                return true;
            case Intent.Remove remove:
                cell = remove.Target.At;
                return true;
            case Intent.FinishOnly finish:
                cell = finish.Target.At;
                return true;
            default:
                return false;
        }
    }

    public void Dispose()
    {
        _events.AnswersReady -= OnAnswers;
        _events.StructuresMoved -= OnMoved;
    }

    private void OnAnswers(Revision revision) => _bumps++;

    private void OnMoved(ValueList<StructureMove> moves) => _bumps++;

    private void Fresh()
    {
        var stamp = Stamp;
        if (_knownAt == stamp)
            return;
        _ends.Clear();
        _flows.Clear();
        _knownAt = stamp;
    }

    private EndMark[] WorkEnds(Placement placement)
    {
        if (!_catalogue.PortsAt(placement).TryGet(out var ports) || ports.Count == 0)
            return Array.Empty<EndMark>();
        var marks = new EndMark[ports.Count];
        for (var i = 0; i < marks.Length; i++)
        {
            var port = ports[i];
            marks[i] = new EndMark(port.From, port.Toward, !Meet(port, placement, out _, out _));
        }

        return marks;
    }

    private FlowMark[] WorkFlow(Placement placement)
    {
        if (!_catalogue.PortsAt(placement).TryGet(out var ports) || ports.Count == 0)
            return Array.Empty<FlowMark>();
        if (HasRole(ports))
        {
            var marks = new List<FlowMark>(ports.Count);
            foreach (var port in ports)
                if (port.Role.TryGet(out var role))
                    marks.Add(new FlowMark(port.From, port.Toward, Inward: !IsOutput(role)));
            return marks.ToArray();
        }

        if (ports.Count != 2)
            return Array.Empty<FlowMark>();
        var a = Trace(ports[0], placement);
        var b = Trace(ports[1], placement);
        // Flow leaves through the port on the drain side, or the port opposite the feed side.
        var outAtB = b == Side.Drain && a != Side.Drain || a == Side.Feed && b != Side.Feed;
        var outAtA = a == Side.Drain && b != Side.Drain || b == Side.Feed && a != Side.Feed;
        if (outAtA == outAtB)
            return Array.Empty<FlowMark>();
        var exit = outAtB ? ports[1] : ports[0];
        return new[] { new FlowMark(exit.From, exit.Toward, Inward: false) };
    }

    private enum Side
    {
        Unknown,
        Feed,
        Drain,
    }

    // Follows a run from one of its pieces' ports through joined run pieces to the first port with a role.
    private Side Trace(Port start, Placement self)
    {
        var port = start;
        var at = self;
        for (var step = 0; step < MostTraced; step++)
        {
            if (!Meet(port, at, out var next, out var theirs))
                return Side.Unknown;
            if (theirs.Role.TryGet(out var role))
                return IsOutput(role) ? Side.Feed : Side.Drain;
            if (!_catalogue.PortsAt(next).TryGet(out var ports) || ports.Count != 2 || HasRole(ports))
                return Side.Unknown;
            port = ports[0].Index == theirs.Index ? ports[1] : ports[0];
            at = next;
        }

        return Side.Unknown;
    }

    private bool Meet(Port port, Placement self, out Placement other, out Port theirs)
    {
        var cell = port.Joins;
        var large = Cell.FromWorld(new WorldPoint(cell.X * 0.1f, cell.Y * 0.1f, cell.Z * 0.1f), PieceGrid.Large);
        return MeetAt(cell, port, self, out other, out theirs) || large != cell && MeetAt(large, port, self, out other, out theirs);
    }

    private bool MeetAt(Cell cell, Port port, Placement self, out Placement other, out Port theirs)
    {
        foreach (var ghost in _board.At(cell))
            if (NewPiece(ghost.Intent) is { } placement && placement != self && Meets(placement, port, out theirs))
            {
                other = placement;
                return true;
            }

        foreach (var structure in _world.At(cell))
        {
            var placement = new Placement(structure.Ref.Piece, structure.Ref.At, structure.Turn);
            if (placement != self && !TakenAway(structure.Ref) && Meets(placement, port, out theirs))
            {
                other = placement;
                return true;
            }
        }

        other = null!;
        theirs = null!;
        return false;
    }

    // A port of the other piece leaves the cell this port joins, back into this port's cell, on a shared network.
    private bool Meets(Placement other, Port port, out Port theirs)
    {
        theirs = null!;
        if (!_catalogue.PortsAt(other).TryGet(out var all))
            return false;
        foreach (var their in all)
            if (their.From == port.Joins && their.Joins == port.From && Shares(their.Kinds, port.Kinds))
            {
                theirs = their;
                return true;
            }

        return false;
    }

    private bool TakenAway(StructureRef structure)
    {
        foreach (var ghost in _board.On(structure))
            if (ghost.Intent is Intent.Remove or Intent.Swap)
                return true;
        return false;
    }

    private static Placement? NewPiece(Intent intent) => intent switch
    {
        Intent.Build build => build.Target,
        Intent.Swap swap => swap.New,
        _ => null,
    };

    private static bool HasRole(ValueList<Port> ports)
    {
        foreach (var port in ports)
            if (port.Role.IsSome)
                return true;
        return false;
    }

    private static bool IsOutput(PortRole role) => role is PortRole.Output or PortRole.Output2 or PortRole.Waste;

    private static bool Shares(ValueSet<PortKind> a, ValueSet<PortKind> b)
    {
        foreach (var kind in a)
            if (b.Contains(kind))
                return true;
        return false;
    }
}
