using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Executors;
using Lattice.Core.Sync;
using Lattice.Tests.Board;
using Lattice.Tests.Commands;
using Lattice.Tests.Executors;
using Lattice.TestKit;

namespace Lattice.Tests.Sync;

/// <summary>
/// A host with executors and its sync, and clients that each run a real board with the client seat's executors, over
/// a wire. A host frame runs as the world's does: lapsed claims, the slices, the board's events, then the status
/// placed after the frame's lines. Each client keeps its own game clock and a log of the events its board raises.
/// </summary>
internal sealed class ExecutorSyncBench
{
    private readonly Dictionary<ConnectionId, Seat> _seats = new();
    private int _nextCell;

    public ExecutorSyncBench(SyncLimits? limits = null)
    {
        Limits = limits ?? SyncLimits.Default;
        Executors = new WorldExecutors(Host.T.Board, HostClock, WorldSeat.Host);
        Sync = new HostSync(Host.Service, Host.T.Board, Host.Scheduler, Wire.Host, Limits, HostReports.Add, Executors);
    }

    public SyncLimits Limits { get; }

    public TestCommands Host { get; } = new();

    public ManualGameClock HostClock { get; } = new();

    public WorldExecutors Executors { get; }

    public HostSync Sync { get; }

    public Wire Wire { get; } = new();

    public List<Reason> HostReports { get; } = new();

    /// <summary>A client on its own connection with its game clock at <paramref name="clock"/> seconds, joined.</summary>
    public Seat Join(long connection, double clock = 5000)
    {
        var id = new ConnectionId(connection);
        var seat = new Seat(id, Wire, Limits, clock);
        _seats[id] = seat;
        Sync.Joined(id);
        return seat;
    }

    /// <summary>One frame: the host's frame, then every message on the wire delivered and each client's events raised.</summary>
    public void Frame()
    {
        HostFrame();
        Deliver();
    }

    public void Frames(int count)
    {
        for (var i = 0; i < count; i++)
            Frame();
    }

    /// <summary>Frames until the host has nothing queued and the wire is empty.</summary>
    public void Settle(int most = 100)
    {
        for (var i = 0; i < most; i++)
        {
            Frame();
            if (Idle())
                return;
        }

        throw new Xunit.Sdk.XunitException("sync never settled");
    }

    /// <summary>The host's frame alone: lapse, slices (sending), events, then the frame's status placed.</summary>
    public void HostFrame()
    {
        Executors.Lapse();
        Host.Frame();
        Sync.PlaceStatus();
    }

    /// <summary>Delivers what the wire holds and raises each client's queued events.</summary>
    public void Deliver()
    {
        foreach (var (connection, queue) in Wire.ToClients)
            while (queue.Count > 0)
            {
                var (kind, text) = queue.Dequeue();
                if (_seats.TryGetValue(connection, out var seat))
                    seat.Sync.Receive(kind, text);
            }

        foreach (var seat in _seats.Values)
            seat.T.Events.Flush();
    }

    /// <summary>A frame ghost in a fresh large cell on the host, made by the maker given (the board test maker by default).</summary>
    public GhostId Ghost(Maker? maker = null)
    {
        var at = new Cell(10 + 2 * _nextCell++, 10, 10);
        var draft = Host.T.Draft("StructureFrame", at) with { MadeBy = maker ?? TestBoard.TestMaker };
        return TestBoard.Applied(Host.T.Place(draft)).Added[0].Id;
    }

    public void Remove(GhostId ghost) => TestBoard.Applied(Host.T.Apply(new Command.Remove(TestBoard.Ids(ghost))));

    public IExecutorHandle Register(string name) => TestKitIds.Must(Executors.Register(ExecutorsBench.Info(name)));

    public static Claim Claim(IExecutorHandle handle, GhostId ghost, double seconds = 30) =>
        handle.Claim(ghost, ExecutorsBench.Seconds(seconds)).Match(static c => c, static why => throw new Xunit.Sdk.XunitException($"refused: {why.Why}"));

    public static void Report(IExecutorHandle handle, GhostId ghost, ExecutorStatus status) =>
        TestKitIds.Must(handle.Report(ghost, status));

    /// <summary>The host's messages to a connection, by kind, in the order sent.</summary>
    public List<SyncKind> SentTo(ConnectionId connection)
    {
        var kinds = new List<SyncKind>();
        foreach (var (to, kind, _) in Wire.SentByHost)
            if (to == connection)
                kinds.Add(kind);
        return kinds;
    }

    private bool Idle()
    {
        if (Sync.Queued > 0 || Sync.StatusWaiting > 0)
            return false;
        foreach (var queue in Wire.ToClients.Values)
            if (queue.Count > 0)
                return false;
        return true;
    }

    /// <summary>One client: its board, game clock, executors (client seat) and sync, and what its board raised.</summary>
    internal sealed class Seat
    {
        public Seat(ConnectionId connection, Wire wire, SyncLimits limits, double clock)
        {
            Connection = connection;
            Clock.Advance(clock - Clock.Now.Seconds);
            Executors = new WorldExecutors(T.Board, Clock, WorldSeat.Client);
            Sync = new ClientSync(T.Board, wire.Client(connection), limits, Reports.Add, Executors.Mirror);
            T.Events.Changed += set => Log.Add("changed " + set.Revision.Value);
            T.Events.BoardReset += () => Log.Add("reset");
            T.Events.StatusChanged += ghost =>
            {
                Log.Add("status " + ghost.Value);
                StatusChanges.Add(ghost);
            };
            T.Events.Blocked += (maker, ghosts) =>
            {
                Log.Add("blocked " + ghosts.Count);
                Blocked.Add((maker, ghosts));
            };
        }

        public ConnectionId Connection { get; }

        public TestBoard T { get; } = new();

        public ManualGameClock Clock { get; } = new();

        public WorldExecutors Executors { get; }

        public ClientSync Sync { get; }

        public List<Reason> Reports { get; } = new();

        public List<string> Log { get; } = new();

        public List<GhostId> StatusChanges { get; } = new();

        public List<(Maker Maker, ValueList<BlockedGhost> Ghosts)> Blocked { get; } = new();
    }
}

/// <summary>Styles over one seat's board reading that seat's executors, with one layer that marks working ghosts.</summary>
internal sealed class StyledSeat
{
    private readonly TestBoard _board;
    private readonly Lattice.Core.Slices.FrameScheduler _scheduler;
    private readonly Lattice.Core.Styles.GhostStyles _styles;

    public StyledSeat(TestBoard board, WorldExecutors statuses)
    {
        _board = board;
        var clock = new Lattice.Tests.Styles.SteppingClock();
        _scheduler = new Lattice.Core.Slices.FrameScheduler(clock, Lattice.Core.Slices.FrameBudgets.Default);
        _styles = new Lattice.Core.Styles.GhostStyles(new Lattice.Core.Styles.StyleParts(
            board.Board, TestBoard.Alice, new Lattice.Core.Board.Shapes(board.Catalogue, board.Grid), board.Grid, _scheduler, clock,
            new Lattice.Tests.Styles.FakePieceLooks(board.Catalogue))
        {
            Statuses = statuses,
            Drawing = true,
            Paints = new Lattice.Core.Styles.NoPaintColours(),
            World = new Lattice.TestKit.World.FakeWorldQuery(new Lattice.TestKit.World.FakeWorld(board.Catalogue)),
        });
        Lattice.Tests.Styles.StyleBench.Must(_styles.Add(Lattice.Tests.Styles.StyleBench.LayerOf(
            "working", StyleBand.Theme, 0,
            Lattice.Tests.Styles.StyleBench.Rule(
                new StyleMatch.Status(StatusKind.Working, Option<ExecutorId>.None),
                Lattice.Tests.Styles.StyleBench.Edge(LineStyle.Dashed, LibraryPalette.Fail)))));
    }

    /// <summary>The ghost's resolved style and edge colour once the styles have nothing left to work out.</summary>
    public (Style Style, Rgba Edge)? Look(GhostId ghost)
    {
        for (var i = 0; i < 1000; i++)
        {
            _scheduler.RunFrame();
            _board.Events.Flush();
            _styles.Frame();
            if (!_styles.Busy && _board.Events.Waiting == 0)
                return _styles.LookOf(ghost).TryGet(out var look) ? (look.Style, look.Edge) : null;
        }

        throw new Xunit.Sdk.XunitException("the styles never settled");
    }
}
