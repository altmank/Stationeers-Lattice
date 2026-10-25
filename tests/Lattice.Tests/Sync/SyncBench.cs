using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Sync;
using Lattice.TestKit.Catalogue;
using Lattice.Tests.Commands;
using TestBoard = Lattice.Tests.Board.TestBoard;

namespace Lattice.Tests.Sync;

/// <summary>A client's board as a plain store: it takes host change sets in revision order and resets from a join.</summary>
internal sealed class FakeReplica : IReplicaBoard
{
    private readonly Dictionary<GhostId, Ghost> _ghosts = new();
    private readonly Dictionary<PlanId, Plan> _plans = new();

    public Revision Revision { get; private set; }

    public BoardEvents Events { get; } = new();

    public List<ChangeSet> Mirrored { get; } = new();

    public int Resets { get; private set; }

    public IReadOnlyCollection<Ghost> Ghosts => _ghosts.Values;

    public IReadOnlyCollection<Plan> Plans => _plans.Values;

    public Option<Ghost> Ghost(GhostId id) => _ghosts.TryGetValue(id, out var ghost) ? Option.Some(ghost) : Option<Ghost>.None;

    public Option<Plan> Plan(PlanId id) => _plans.TryGetValue(id, out var plan) ? Option.Some(plan) : Option<Plan>.None;

    public Result<ChangeSet, Reason> Mirror(ChangeSet set)
    {
        if (set.Revision.Value != Revision.Value + 1)
            return Result<ChangeSet, Reason>.Error(SyncRules.Problem("not the next revision"));
        foreach (var plan in set.PlansOpened)
            _plans[plan.Id] = plan;
        foreach (var change in set.PlansChanged)
            _plans[change.After.Id] = change.After;
        foreach (var ghost in set.Added)
            _ghosts[ghost.Id] = ghost;
        foreach (var change in set.Changed)
            _ghosts[change.After.Id] = change.After;
        foreach (var removal in set.Removed)
            _ghosts.Remove(removal.Ghost.Id);
        foreach (var plan in set.PlansFinished)
            _plans.Remove(plan);
        Revision = set.Revision;
        Mirrored.Add(set);
        Events.Queue(new ChangedEvent(set));
        return Result<ChangeSet, Reason>.Ok(set);
    }

    public void Reset(BoardSeed seed)
    {
        _ghosts.Clear();
        _plans.Clear();
        foreach (var plan in seed.Plans)
            _plans[plan.Id] = plan;
        foreach (var ghost in seed.Ghosts)
            _ghosts[ghost.Id] = ghost;
        Revision = seed.Revision;
        Resets++;
        Events.Queue(new BoardResetEvent());
    }
}

/// <summary>The network between one host and its clients, holding messages until a test delivers them.</summary>
internal sealed class Wire
{
    public Queue<(ConnectionId From, string Text)> ToHost { get; } = new();

    public Dictionary<ConnectionId, Queue<(SyncKind Kind, string Text)>> ToClients { get; } = new();

    public List<(ConnectionId To, SyncKind Kind, string Text)> SentByHost { get; } = new();

    /// <summary>Runs on every message the host sends (a test makes sending cost time with it).</summary>
    public Action? OnHostSend { get; set; }

    public ISyncLink Host => new HostEnd(this);

    public ISyncLink Client(ConnectionId connection) => new ClientEnd(this, connection);

    private sealed class HostEnd(Wire wire) : ISyncLink
    {
        public void ToHost(SyncKind kind, string text) => throw new InvalidOperationException("the host sends to clients");

        public void ToClient(ConnectionId to, SyncKind kind, string text)
        {
            wire.OnHostSend?.Invoke();
            wire.SentByHost.Add((to, kind, text));
            if (!wire.ToClients.TryGetValue(to, out var queue))
                wire.ToClients[to] = queue = new Queue<(SyncKind, string)>();
            queue.Enqueue((kind, text));
        }
    }

    private sealed class ClientEnd(Wire wire, ConnectionId connection) : ISyncLink
    {
        public void ToHost(SyncKind kind, string text)
        {
            Assert.Equal(SyncKind.Command, kind);
            wire.ToHost.Enqueue((connection, text));
        }

        public void ToClient(ConnectionId to, SyncKind kind, string text) => throw new InvalidOperationException("a client sends to the host");
    }
}

/// <summary>A host (a test board with its command service and sync) and clients on fake replicas, over a wire.</summary>
internal sealed class SyncBench
{
    private readonly Dictionary<ConnectionId, (ClientSync Sync, FakeReplica Board, ClientId Player)> _clients = new();

    public SyncBench(SyncLimits? limits = null, NamedPiece? modded = null)
    {
        Limits = limits ?? SyncLimits.Default;
        Host = modded is null ? new TestCommands() : new TestCommands(null, modded);
        HostSync = new HostSync(Host.Service, Host.T.Board, Host.Scheduler, Wire.Host, Limits, HostReports.Add);
    }

    public SyncLimits Limits { get; }

    public TestCommands Host { get; }

    public HostSync HostSync { get; }

    public Wire Wire { get; } = new();

    public List<Reason> HostReports { get; } = new();

    public List<Reason> ClientReports { get; } = new();

    public List<(Ticket Ticket, Outcome Outcome)> Answers { get; } = new();

    /// <summary>A client on its own connection, the game's player <paramref name="player"/>, joined.</summary>
    public (ClientSync Sync, FakeReplica Board) Join(long connection, ulong player)
    {
        var id = new ConnectionId(connection);
        var board = new FakeReplica();
        board.Events.Answered += (ticket, outcome) => Answers.Add((ticket, outcome));
        var sync = new ClientSync(board, Wire.Client(id), Limits, ClientReports.Add);
        _clients[id] = (sync, board, new ClientId(player));
        HostSync.Joined(id);
        return (sync, board);
    }

    /// <summary>One frame everywhere: the host's slices and events, the wire both ways, the clients' events.</summary>
    public void Frame()
    {
        Host.Frame();
        while (Wire.ToHost.Count > 0)
        {
            var (from, text) = Wire.ToHost.Dequeue();
            HostSync.Receive(from, _clients[from].Player, text);
        }

        foreach (var (connection, queue) in Wire.ToClients)
            while (queue.Count > 0)
            {
                var (kind, text) = queue.Dequeue();
                if (_clients.TryGetValue(connection, out var client))
                    client.Sync.Receive(kind, text);
            }

        foreach (var client in _clients.Values)
            client.Board.Events.Flush();
    }

    public void Frames(int count)
    {
        for (var i = 0; i < count; i++)
            Frame();
    }

    /// <summary>The host board's ghosts and plans, by id.</summary>
    public (List<Ghost> Ghosts, List<Plan> Plans) HostState()
    {
        var ghosts = new List<Ghost>();
        foreach (var entry in Host.T.Board.Index.Ghosts.Values)
            ghosts.Add(entry.Ghost);
        var plans = new List<Plan>();
        foreach (var entry in Host.T.Board.Index.Plans.Values)
            plans.Add(entry.Plan);
        return (Sorted(ghosts), Sorted(plans));
    }

    public static (List<Ghost> Ghosts, List<Plan> Plans) State(FakeReplica board) =>
        (Sorted(new List<Ghost>(board.Ghosts)), Sorted(new List<Plan>(board.Plans)));

    public GhostDraft Draft(string piece, Cell at) => Host.T.Draft(piece, at);

    private static List<Ghost> Sorted(List<Ghost> ghosts)
    {
        ghosts.Sort(static (a, b) => a.Id.Value.CompareTo(b.Id.Value));
        return ghosts;
    }

    private static List<Plan> Sorted(List<Plan> plans)
    {
        plans.Sort(static (a, b) => a.Id.Value.CompareTo(b.Id.Value));
        return plans;
    }

    public static CommandId Id(int n) => new(new Guid(n, 39, 39, new byte[8]));

    public static Actor Player(ulong id) => new Actor.Player(new ClientId(id));

    public static readonly Actor HostPlayer = TestBoard.Alice;
}
