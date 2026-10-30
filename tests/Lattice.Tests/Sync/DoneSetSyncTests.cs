using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Commands;
using Lattice.Core.Slices;
using Lattice.Core.Sync;
using Lattice.TestKit;
using Lattice.TestKit.World;
using Lattice.Tests.Board;
using Lattice.Tests.FinishedPlans;
using Lattice.Tests.Slices;
using static Lattice.Tests.FinishedPlans.FinishedBench;

namespace Lattice.Tests.Sync;

/// <summary>
/// A change set that gives a plan its done count outright (a catch-up, a reopen) carries the count, so a joined
/// client's plan counts what the host's does after that same set; ghosts done after it are counted once on each seat.
/// </summary>
public sealed class DoneSetSyncTests
{
    private static readonly ConnectionId Connection = new(1);

    /// <summary>A plan of two trusses built and one planned.</summary>
    private static (PlanId Plan, Ghost Left) TwoBuiltOneLeft(FinishedBench b)
    {
        var plan = b.NewPlan("Hall");
        b.Construct(b.Plan(plan, "ModTruss", Large(0)));
        b.Construct(b.Plan(plan, "ModTruss", Large(1)));
        return (plan, b.Plan(plan, "ModTruss", Large(2)));
    }

    [Fact]
    public void A_catch_up_that_sets_a_plans_done_count_reaches_a_joined_client_in_the_same_change_set()
    {
        using var b = new FinishedBench();
        var plan = b.NewPlan("Hall");
        b.Construct(b.Plan(plan, "ModTruss", Large(0)));
        var save = b.Save();
        b.Construct(b.Plan(plan, "ModTruss", Large(1)));
        b.Reload(Option.Some(save));
        var seats = new Seats(b);
        Assert.Equal(1, seats.ClientDone(plan));

        seats.Apply(new Command.CatchUp(plan));

        Assert.Equal(2, Done(b.Board, plan));
        Assert.Equal(2, seats.ClientDone(plan));
        Assert.Equal(seats.HostGhosts(plan), seats.ClientGhosts(plan));
        Assert.Empty(seats.Reports);
    }

    [Fact]
    public void A_reopened_plan_reaches_a_joined_client_with_its_done_count()
    {
        using var b = new FinishedBench();
        var (plan, _) = TwoBuiltOneLeft(b);
        b.Finish(plan);
        var seats = new Seats(b);

        seats.Apply(new Command.Reopen(plan));

        Assert.Equal(2, Done(b.Board, plan));
        Assert.Equal(2, seats.ClientDone(plan));
        Assert.Equal(1, seats.ClientGhosts(plan));
        Assert.Empty(seats.Reports);
    }

    [Fact]
    public void A_ghost_done_after_a_set_count_counts_once_on_each_seat()
    {
        using var b = new FinishedBench();
        var (plan, left) = TwoBuiltOneLeft(b);
        b.Finish(plan);
        var seats = new Seats(b);
        seats.Apply(new Command.Reopen(plan));

        b.Construct(left);
        seats.Frames();

        Assert.Equal(3, Done(b.Board, plan));
        Assert.Equal(3, seats.ClientDone(plan));
        Assert.Empty(seats.Reports);
    }

    [Fact]
    public void A_resync_after_a_set_count_keeps_it()
    {
        using var b = new FinishedBench();
        var (plan, _) = TwoBuiltOneLeft(b);
        b.Finish(plan);
        var seats = new Seats(b);
        seats.Apply(new Command.Reopen(plan));

        seats.Host.Rejoin(Connection, RejoinForm.Write([]));
        seats.Frames();

        Assert.Equal(2, seats.ClientDone(plan));
        Assert.Empty(seats.Reports);
    }

    [Fact]
    public void A_reopen_inside_a_batch_keeps_its_done_count_on_the_host_and_the_client()
    {
        using var b = new FinishedBench();
        var (plan, _) = TwoBuiltOneLeft(b);
        b.Finish(plan);
        var seats = new Seats(b);

        seats.Apply(new Command.Batch(ValueList.Of<Command>(new Command.Reopen(plan))));

        Assert.Equal(2, Done(b.Board, plan));
        Assert.Equal(2, seats.ClientDone(plan));
        Assert.Empty(seats.Reports);
    }

    private static int Done(GhostBoard board, PlanId plan) => board.Index.Plans[plan].Done;

    /// <summary>The bench's board as the host, with a client seat's own board joined over one sync wire.</summary>
    private sealed class Seats
    {
        private readonly FinishedBench _bench;
        private readonly FrameScheduler _scheduler = new(new FakeClock(), FrameBudgets.Default);
        private readonly Wire _wire = new();
        private readonly ClientSync _client;
        private readonly GhostBoard _clientBoard;

        public Seats(FinishedBench bench)
        {
            _bench = bench;
            var checks = new ClientChecks(CommandLimits.Default, bench.T.Catalogue, new AttachmentLimits());
            var service = new CommandService(bench.Board, new CommandExpanders(), new FakeWorldQuery(bench.World), checks, _scheduler);
            Host = new HostSync(service, bench.Board, _scheduler, _wire.Host, SyncLimits.Default, Reports.Add);
            _clientBoard = new TestBoard(bench.T.Catalogue).Board;
            _client = new ClientSync(_clientBoard, _wire.Client(Connection), SyncLimits.Default, Reports.Add);
            Host.Joined(Connection);
            Frames();
        }

        public HostSync Host { get; }

        public List<Reason> Reports { get; } = new();

        public int ClientDone(PlanId plan) => _clientBoard.Index.Plans[plan].Done;

        public int ClientGhosts(PlanId plan) => _clientBoard.Index.Plans[plan].Ghosts.Count;

        public int HostGhosts(PlanId plan) => _bench.Board.Index.Plans[plan].Ghosts.Count;

        public void Apply(Command command)
        {
            TestBoard.Applied(_bench.Apply(command));
            Frames();
        }

        public void Frames(int count = 8)
        {
            for (var i = 0; i < count; i++)
            {
                _scheduler.RunFrame();
                _bench.Flush();
                if (_wire.ToClients.TryGetValue(Connection, out var queue))
                    while (queue.Count > 0)
                    {
                        var (kind, text) = queue.Dequeue();
                        _client.Receive(kind, text);
                    }

                _clientBoard.Events.Flush();
            }
        }
    }
}
