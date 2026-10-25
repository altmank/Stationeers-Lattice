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
/// Finished plans live on the host in this version: a client keeps no plan files, so its <see cref="IPlans.Finished"/> is
/// empty and its <see cref="IPlans.AsTemplate"/> of a finished plan is refused module-missing; it still submits
/// <see cref="Command.Repair"/>, <see cref="Command.TakeDown"/> and <see cref="Command.Reopen"/>, which the host works out.
/// </summary>
public sealed class FinishedOnClientTests
{
    [Fact]
    public void A_client_lists_no_finished_plans_and_refuses_a_finished_plans_template_as_module_missing()
    {
        var client = new PlansView(new TestBoard().Board, TestBoard.Alice);

        var why = Prefabs(client.AsTemplate(new PlanId(41), TestKitIds.Must(TemplateName.Create("Copy"))));

        Assert.Empty(client.Finished);
        Assert.Equal(ReasonCodes.ModuleMissing, why.Code);
        Assert.Equal("saving", why.Facts["module"]);
    }

    [Fact]
    public void Repair_take_down_and_reopen_sent_by_a_client_are_worked_out_on_the_host_and_reach_the_client()
    {
        using var b = new FinishedBench();
        var plan = b.NewPlan("Hall");
        var gone = b.Construct(b.Plan(plan, "ModTruss", Large(1)));
        b.Construct(b.Plan(plan, "ModTruss", Large(2)));
        b.Plan(plan, "ModTruss", Large(3));
        b.Finish(plan);
        b.TakeApart(gone);
        var host = new Host(b);
        var (client, replica) = host.Join(1, 7);

        var sent = new[]
        {
            new Command.Repair(plan, Asker),
            new Command.TakeDown(plan, Asker),
            (Command)new Command.Reopen(plan),
        };
        for (var i = 0; i < sent.Length; i++)
        {
            client.Submit(new CommandId(new System.Guid(i + 1, 3, 3, new byte[8])), sent[i], TestBoard.Alice);
            host.Frames(8);
        }

        Assert.Equal(3, host.Answers.Count);
        Assert.All(host.Answers, outcome => Assert.IsType<Outcome.Applied>(outcome));
        var opened = new List<PlanId>();
        foreach (var set in replica.Mirrored)
            foreach (var p in set.PlansOpened)
                opened.Add(p.Id);
        Assert.Contains(plan, opened);
        Assert.Equal(3, opened.Count);
        Assert.Contains(replica.Ghosts, g => g.Plan == plan);
        Assert.Empty(host.Reports);
    }

    private static Reason Prefabs(Result<Template, Reason> result) =>
        result.TryGetError(out var why) ? why : throw new Xunit.Sdk.XunitException("read");

    /// <summary>The bench's board, with saving and the finished-plans expanders, as a host over a sync wire.</summary>
    private sealed class Host
    {
        private readonly FinishedBench _bench;
        private readonly FrameScheduler _scheduler = new(new FakeClock(), FrameBudgets.Default);
        private readonly HostSync _sync;
        private readonly Wire _wire = new();
        private readonly Dictionary<ConnectionId, (ClientSync Sync, FakeReplica Board, ClientId Player)> _clients = new();

        public Host(FinishedBench bench)
        {
            _bench = bench;
            var expanders = new CommandExpanders();
            Assert.True(expanders.RegisterSliced(bench.Expanders.Repair).IsOk);
            Assert.True(expanders.RegisterSliced(bench.Expanders.TakeDown).IsOk);
            var checks = new ClientChecks(CommandLimits.Default, bench.T.Catalogue, new AttachmentLimits());
            var service = new CommandService(bench.Board, expanders, new FakeWorldQuery(bench.World), checks, _scheduler);
            _sync = new HostSync(service, bench.Board, _scheduler, _wire.Host, SyncLimits.Default, Reports.Add);
        }

        public List<Reason> Reports { get; } = new();

        public List<Outcome> Answers { get; } = new();

        public (ClientSync Sync, FakeReplica Board) Join(long connection, ulong player)
        {
            var id = new ConnectionId(connection);
            var board = new FakeReplica();
            board.Events.Answered += (_, outcome) => Answers.Add(outcome);
            var sync = new ClientSync(board, _wire.Client(id), SyncLimits.Default, Reports.Add);
            _clients[id] = (sync, board, new ClientId(player));
            _sync.Joined(id);
            Frames(1);
            return (sync, board);
        }

        public void Frames(int count)
        {
            for (var i = 0; i < count; i++)
            {
                _scheduler.RunFrame();
                _bench.Flush();
                while (_wire.ToHost.Count > 0)
                {
                    var (from, text) = _wire.ToHost.Dequeue();
                    _sync.Receive(from, _clients[from].Player, text);
                }

                foreach (var (connection, queue) in _wire.ToClients)
                    while (queue.Count > 0)
                    {
                        var (kind, text) = queue.Dequeue();
                        if (_clients.TryGetValue(connection, out var client))
                            client.Sync.Receive(kind, text);
                    }

                foreach (var client in _clients.Values)
                    client.Board.Events.Flush();
            }
        }
    }
}
