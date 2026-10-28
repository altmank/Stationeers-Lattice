using System;
using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Commands;
using Lattice.Core.Slices;
using Lattice.Core.Sync;
using Lattice.TestKit;

namespace Lattice.Tests.Sync;

public sealed class ResyncTests
{
    private static Command Place(SyncBench bench, int x) =>
        new Command.Place(ValueList.Of(bench.Draft("StructureCableStraight", new Cell(x, 5, 5))));

    private static void BoardsEqual(SyncBench bench, FakeReplica client)
    {
        var host = bench.HostState();
        var mirror = SyncBench.State(client);
        Assert.Equal(host.Ghosts, mirror.Ghosts);
        Assert.Equal(host.Plans, mirror.Plans);
        Assert.Equal(bench.Host.T.Board.Revision, client.Revision);
    }

    private static List<ChangeSet> HostSets(SyncBench bench)
    {
        var sets = new List<ChangeSet>();
        bench.Host.T.Events.Changed += sets.Add;
        return sets;
    }

    [Fact]
    public void A_dropped_set_then_later_sets_then_the_rejoin_give_the_hosts_board()
    {
        var bench = new SyncBench();
        var (sync, board) = bench.Join(connection: 1, player: 7);
        bench.Frame();
        board.RefuseOnce = bench.Host.T.Board.Revision.Value + 1;

        bench.Host.Submit(Place(bench, 1));
        bench.Host.Submit(Place(bench, 2));
        bench.Host.Submit(Place(bench, 3));
        bench.Frames(6);

        BoardsEqual(bench, board);
        Assert.False(sync.Rejoining);
        Assert.Equal(1, board.Resets - 1);
        var report = Assert.Single(bench.ClientReports);
        Assert.Equal("rejoin", report.Facts["then"]);
        Assert.Equal("refused for the test", report.Facts["problem"]);
    }

    [Fact]
    public void Sets_after_a_drop_are_skipped_without_a_report_until_the_join()
    {
        var bench = new SyncBench { HostTakesRejoins = false };
        var (sync, board) = bench.Join(connection: 1, player: 7);
        bench.Frame();
        board.RefuseOnce = bench.Host.T.Board.Revision.Value + 1;
        var before = board.Revision;

        for (var x = 1; x <= 5; x++)
        {
            bench.Host.Submit(Place(bench, x));
            bench.Frames(2);
        }

        Assert.True(sync.Rejoining);
        Assert.Equal(before, board.Revision);
        Assert.Single(bench.ClientReports);
        Assert.Single(bench.Wire.SentByHost, sent => sent.Kind == SyncKind.Join);
    }

    [Fact]
    public void A_rejoin_from_a_connection_that_never_joined_is_reported_and_changes_nothing()
    {
        var bench = new SyncBench();

        bench.HostSync.Rejoin(new ConnectionId(9), RejoinForm.Write([]));

        var report = Assert.Single(bench.HostReports);
        Assert.Equal("9", report.Facts["connection"]);
        Assert.Equal(0, bench.HostSync.Queued);
    }

    [Fact]
    public void A_rejoin_while_its_join_is_still_queued_queues_no_second_join()
    {
        var bench = new SyncBench();
        bench.Join(connection: 1, player: 7);

        bench.HostSync.Rejoin(new ConnectionId(1), RejoinForm.Write([]));

        Assert.Equal(1, bench.HostSync.Queued);
    }

    [Fact]
    public void An_applied_command_whose_set_was_dropped_settles_once_with_the_hosts_change_set()
    {
        var bench = new SyncBench();
        var hostSets = HostSets(bench);
        var (sync, board) = bench.Join(connection: 1, player: 7);
        bench.Frame();
        board.RefuseOnce = bench.Host.T.Board.Revision.Value + 1;

        sync.Submit(SyncBench.Id(1), Place(bench, 1), SyncBench.HostPlayer);
        bench.Frames(6);

        BoardsEqual(bench, board);
        var (ticket, outcome) = Assert.Single(bench.Answers);
        Assert.Equal(new Ticket(SyncBench.Id(1)), ticket);
        Assert.Equal(Assert.Single(hostSets), Assert.IsType<Outcome.Applied>(outcome).Changes);
    }

    [Fact]
    public void A_set_that_changes_and_removes_ghosts_comes_back_whole_after_the_drop()
    {
        var bench = new SyncBench();
        var hostSets = HostSets(bench);
        var (sync, board) = bench.Join(connection: 1, player: 7);
        bench.Host.Submit(Place(bench, 1));
        bench.Host.Submit(Place(bench, 2));
        bench.Frames(3);
        var ghosts = bench.HostState().Ghosts;
        board.RefuseOnce = bench.Host.T.Board.Revision.Value + 1;

        sync.Submit(SyncBench.Id(1), new Command.Remove(new Selector.Ids(ValueList.Of(ghosts[0].Id))), SyncBench.HostPlayer);
        bench.Frames(6);

        BoardsEqual(bench, board);
        var (_, outcome) = Assert.Single(bench.Answers);
        var changes = Assert.IsType<Outcome.Applied>(outcome).Changes;
        Assert.Equal(hostSets.Last(), changes);
        Assert.Equal(ghosts[0], Assert.Single(changes.Removed).Ghost);
    }

    [Fact]
    public void A_refused_command_answered_before_the_drop_settles_once()
    {
        var bench = new SyncBench();
        var (sync, board) = bench.Join(connection: 1, player: 7);
        bench.Frame();

        sync.Submit(SyncBench.Id(1), new Command.Remove(new Selector.Ids(ValueList.Of(new GhostId(4040)))), SyncBench.HostPlayer);
        board.RefuseOnce = bench.Host.T.Board.Revision.Value + 1;
        bench.Host.Submit(Place(bench, 1));
        bench.Frames(6);

        BoardsEqual(bench, board);
        Assert.IsType<Outcome.Refused>(Assert.Single(bench.Answers).Outcome);
    }

    [Fact]
    public void A_command_the_host_never_got_settles_outcome_lost_after_the_rejoin()
    {
        var bench = new SyncBench();
        var (sync, board) = bench.Join(connection: 1, player: 7);
        bench.Frame();
        bench.Wire.LoseCommands = true;
        sync.Submit(SyncBench.Id(1), Place(bench, 9), SyncBench.HostPlayer);
        bench.Frame();
        bench.Wire.LoseCommands = false;
        board.RefuseOnce = bench.Host.T.Board.Revision.Value + 1;

        bench.Host.Submit(Place(bench, 1));
        bench.Frames(6);

        BoardsEqual(bench, board);
        var (ticket, outcome) = Assert.Single(bench.Answers);
        Assert.Equal(new Ticket(SyncBench.Id(1)), ticket);
        var why = Assert.Single(Assert.IsType<Outcome.Refused>(outcome).Why).Why;
        Assert.Equal(ReasonCodes.OutcomeLost, why.Code);
        Assert.Equal("lattice.sync", why.From.Value);
        Assert.Equal(SyncBench.Id(1).Value.ToString("D"), why.Facts["command"]);
    }

    [Fact]
    public void A_command_the_host_has_not_finished_stays_pending_and_settles_from_its_answer()
    {
        var bench = new SyncBench();
        var routes = TestKitIds.Must(ModuleName.Create("routes"));
        Assert.True(bench.Host.Expanders.RegisterSliced(new SlicedExpander<Command.PlanRun>(routes, input => new SlowWork(bench, input.Command))).IsOk);
        var (sync, board) = bench.Join(connection: 1, player: 7);
        bench.Frame();
        var run = new Command.PlanRun(new RunRequest(bench.Host.T.Catalogue.Named("StructureCableStraight").Kits[0].Kit, new Cell(100, 100, 100), ValueList<Cell>.Empty, new Cell(200, 100, 100), Option<PlanId>.None, Board.TestBoard.TestMaker));
        sync.Submit(SyncBench.Id(1), run, SyncBench.HostPlayer);
        bench.Frame();
        board.RefuseOnce = bench.Host.T.Board.Revision.Value + 1;

        bench.Host.Submit(Place(bench, 1));
        bench.Frames(3);
        Assert.True(bench.Answers.Count == 0, bench.Answers.Count == 0 ? "" : string.Join("; ", Assert.IsType<Outcome.Refused>(bench.Answers[0].Outcome).Why.Select(r => r.Why.ToString())));
        for (var i = 0; i < 200 && bench.Answers.Count == 0; i++)
            bench.Frame();

        bench.Frames(3);
        BoardsEqual(bench, board);
        var (ticket, outcome) = Assert.Single(bench.Answers);
        Assert.Equal(new Ticket(SyncBench.Id(1)), ticket);
        Assert.Equal(2, Assert.IsType<Outcome.Applied>(outcome).Changes.Added.Count);
    }

    [Fact]
    public void A_throwing_changed_handler_before_sync_still_lets_clients_get_the_set()
    {
        var bench = new SyncBench(before: _ => throw new InvalidOperationException("subscriber bug"));
        var (_, board) = bench.Join(connection: 1, player: 7);
        bench.Frame();

        bench.Host.Submit(Place(bench, 1));
        bench.Frames(3);

        BoardsEqual(bench, board);
        Assert.Empty(bench.ClientReports);
        var (source, error) = Assert.Single(bench.HostFaults);
        Assert.Equal("subscriber bug", error.Message);
        Assert.Contains("ResyncTests", source);
    }

    /// <summary>A search that takes many frames, then finds a straight at each end.</summary>
    private sealed class SlowWork(SyncBench bench, Command.PlanRun run) : IExpansionWork
    {
        private int _steps;

        public Result<Command, Reason> Found { get; private set; } =
            Result<Command, Reason>.Error(Reason.Of(ReasonCodes.UnknownKind, LatticeIdentity.Board, ValueMap<string, string>.Empty));

        public StepOutcome Step()
        {
            bench.Host.Clock.Advance(TimeSpan.FromMilliseconds(5));
            if (++_steps < 40)
                return StepOutcome.More;
            Found = Result<Command, Reason>.Ok(new Command.Batch(ValueList.From<Command>([
                Place(bench, (int)run.Request.From.X),
                Place(bench, (int)run.Request.To.X),
            ])));
            return StepOutcome.Done;
        }
    }
}
