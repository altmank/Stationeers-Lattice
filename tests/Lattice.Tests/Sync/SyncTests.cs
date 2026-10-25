using System;
using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Ports;
using Lattice.Core.Slices;
using Lattice.Core.Sync;
using Lattice.TestKit;
using Lattice.TestKit.Catalogue;
using TestBoard = Lattice.Tests.Board.TestBoard;

namespace Lattice.Tests.Sync;

public sealed class SyncTests
{
    private static Command Place(params GhostDraft[] drafts) => new Command.Place(ValueList.From(drafts));

    private static void BoardsEqual(SyncBench bench, FakeReplica client)
    {
        var host = bench.HostState();
        var mirror = SyncBench.State(client);
        Assert.Equal(host.Ghosts, mirror.Ghosts);
        Assert.Equal(host.Plans, mirror.Plans);
        Assert.Equal(bench.Host.T.Board.Revision, client.Revision);
    }

    [Fact]
    public void A_client_command_reaches_the_host_and_its_lines_reach_both_boards()
    {
        var bench = new SyncBench();
        var (alice, aliceBoard) = bench.Join(connection: 1, player: 7);
        var (_, bobBoard) = bench.Join(connection: 2, player: 8);
        bench.Frame();

        var sent = alice.Submit(SyncBench.Id(1), Place(bench.Draft("StructureCableStraight", new Cell(5, 5, 5))), SyncBench.HostPlayer);
        bench.Frames(3);

        Assert.IsType<Submission.Sent>(sent);
        Assert.Single(bench.HostState().Ghosts);
        BoardsEqual(bench, aliceBoard);
        BoardsEqual(bench, bobBoard);
        var (ticket, outcome) = Assert.Single(bench.Answers);
        Assert.Equal(new Ticket(SyncBench.Id(1)), ticket);
        Assert.Equal(aliceBoard.Mirrored.Single(), Assert.IsType<Outcome.Applied>(outcome).Changes);
        Assert.Empty(bench.HostReports);
        Assert.Empty(bench.ClientReports);
    }

    [Fact]
    public void The_host_acts_as_the_player_of_the_connection_whatever_the_client_names()
    {
        var bench = new SyncBench();
        var (client, board) = bench.Join(connection: 1, player: 7);
        bench.Frame();

        client.Submit(SyncBench.Id(1), Place(bench.Draft("StructureCableStraight", new Cell(5, 5, 5))), SyncBench.Player(99));
        bench.Frames(3);

        var ghost = Assert.Single(bench.HostState().Ghosts);
        Assert.Equal(SyncBench.Player(7), ghost.PlacedBy);
        Assert.Equal(SyncBench.Player(7), Assert.Single(board.Mirrored).By);
    }

    [Fact]
    public void A_refused_client_command_leaves_both_boards_unchanged_and_answers_the_client()
    {
        var bench = new SyncBench();
        var (client, board) = bench.Join(connection: 1, player: 7);
        bench.Host.Submit(Place(bench.Draft("StructureCableStraight", new Cell(5, 5, 5))));
        bench.Frames(2);
        var before = bench.HostState();
        var revision = board.Revision;

        client.Submit(SyncBench.Id(2), Place(TestBoard.Draft(new Intent.Build(new Placement(PrefabHash.Piece("NoSuchPiece"), new Cell(0, 0, 0), Turn.None), Option<Finish>.None))), SyncBench.HostPlayer);
        bench.Frames(3);

        Assert.Equal(before, bench.HostState(), new StateComparer());
        Assert.Equal(revision, board.Revision);
        BoardsEqual(bench, board);
        var (ticket, outcome) = Assert.Single(bench.Answers);
        Assert.Equal(new Ticket(SyncBench.Id(2)), ticket);
        Assert.Equal(ReasonCodes.UnknownPiece, Assert.Single(Assert.IsType<Outcome.Refused>(outcome).Why).Why.Code);
    }

    [Fact]
    public void A_resent_command_applies_once_and_is_answered_again()
    {
        var bench = new SyncBench();
        var (client, board) = bench.Join(connection: 1, player: 7);
        bench.Frame();
        var place = Place(bench.Draft("StructureCableStraight", new Cell(5, 5, 5)));

        client.Submit(SyncBench.Id(1), place, SyncBench.HostPlayer);
        bench.Frames(3);
        client.Submit(SyncBench.Id(1), place, SyncBench.HostPlayer);
        bench.Frames(3);

        Assert.Single(bench.HostState().Ghosts);
        Assert.Single(board.Mirrored);
        BoardsEqual(bench, board);
    }

    [Fact]
    public void A_joining_client_gets_the_board_as_it_is_then_the_changes_after_it()
    {
        var bench = new SyncBench(SyncLimits.Default with { JoinGhostsPerPart = 2 });
        for (var i = 0; i < 5; i++)
            bench.Host.Submit(Place(bench.Draft("StructureCableStraight", new Cell(5 * i, 0, 0))));
        bench.Host.Submit(new Command.CreatePlan(new PlanDraft(TestKitIds.Must(PlanName.Create("Later")), TestBoard.TestMaker, new Visibility.Everyone(), ValueMap<AttachmentKey, Attachment>.Empty)));

        var (_, board) = bench.Join(connection: 3, player: 9);
        bench.Host.Submit(Place(bench.Draft("StructureCableStraight", new Cell(50, 0, 0))));
        bench.Frames(4);

        Assert.Equal(3, bench.Wire.SentByHost.Count(m => m.Kind == SyncKind.Join));
        Assert.Equal(1, board.Resets);
        Assert.Single(board.Mirrored);
        BoardsEqual(bench, board);
    }

    [Fact]
    public void Changes_before_the_join_are_not_sent_again()
    {
        var bench = new SyncBench();
        bench.Host.T.Board.Apply(SyncBench.Id(5), Place(bench.Draft("StructureCableStraight", new Cell(0, 0, 0))), SyncBench.HostPlayer);

        var (_, board) = bench.Join(connection: 1, player: 7);
        bench.Frames(2);

        Assert.Empty(board.Mirrored);
        Assert.Empty(bench.ClientReports);
        BoardsEqual(bench, board);
    }

    [Fact]
    public void A_ghost_moved_to_another_plan_and_a_removal_mirror_exactly()
    {
        var bench = new SyncBench();
        var (client, board) = bench.Join(connection: 1, player: 7);
        bench.Frame();
        var placed = TestBoard.Applied(bench.Host.Submit(Place(
            bench.Draft("StructureCableStraight", new Cell(0, 0, 0)),
            bench.Draft("StructureCableStraight", new Cell(5, 0, 0)))));
        var plan = TestBoard.Applied(bench.Host.Submit(new Command.CreatePlan(new PlanDraft(
            TestKitIds.Must(PlanName.Create("East")), TestBoard.TestMaker, new Visibility.Everyone(), ValueMap<AttachmentKey, Attachment>.Empty)))).PlansOpened[0];

        client.Submit(SyncBench.Id(1), new Command.MoveToPlan(TestBoard.Ids(placed.Added[0].Id), plan.Id), SyncBench.HostPlayer);
        client.Submit(SyncBench.Id(2), new Command.Remove(TestBoard.Ids(placed.Added[1].Id)), SyncBench.HostPlayer);
        bench.Frames(4);

        BoardsEqual(bench, board);
        Assert.Equal(2, bench.Answers.Count(a => a.Outcome is Outcome.Applied));
    }

    [Fact]
    public void A_client_command_over_the_size_limit_is_refused_without_sending()
    {
        var bench = new SyncBench(SyncLimits.Default with { MaxMessageBytes = 300 });
        var (client, board) = bench.Join(connection: 1, player: 7);
        bench.Frame();
        var drafts = new GhostDraft[20];
        for (var i = 0; i < drafts.Length; i++)
            drafts[i] = bench.Draft("StructureCableStraight", new Cell(5 * i, 0, 0));

        client.Submit(SyncBench.Id(1), Place(drafts), SyncBench.HostPlayer);
        bench.Frame();

        Assert.Empty(bench.Wire.ToHost);
        Assert.Empty(bench.HostState().Ghosts);
        Assert.Equal(ReasonCodes.TooLarge, Assert.Single(Assert.IsType<Outcome.Refused>(Assert.Single(bench.Answers).Outcome).Why).Why.Code);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("lattice-command 1 id=KQAAAAMACQABAgMEBQYHCA\nexplode now=1\n")]
    public void A_message_the_host_cannot_read_is_reported_and_changes_nothing(string text)
    {
        var bench = new SyncBench();
        bench.Join(connection: 1, player: 7);
        bench.Frame();

        bench.HostSync.Receive(new ConnectionId(1), new ClientId(7), text);
        bench.Frames(2);

        var report = Assert.Single(bench.HostReports);
        Assert.Equal(ReasonCodes.InvalidValue, report.Code);
        Assert.Equal("1", report.Facts["connection"]);
        Assert.Empty(bench.HostState().Ghosts);
    }

    [Fact]
    public void A_message_over_the_host_limit_is_reported_before_it_is_read()
    {
        var bench = new SyncBench(SyncLimits.Default with { MaxMessageBytes = 100 });

        bench.HostSync.Receive(new ConnectionId(4), new ClientId(7), new string('x', 101));

        Assert.Equal(ReasonCodes.TooLarge, Assert.Single(bench.HostReports).Code);
    }

    [Fact]
    public void Sending_stays_in_the_sending_budget_and_finishes_over_later_frames()
    {
        var bench = new SyncBench(SyncLimits.Default with { JoinGhostsPerPart = 1 });
        for (var i = 0; i < 12; i++)
            bench.Host.Submit(Place(bench.Draft("StructureCableStraight", new Cell(5 * i, 0, 0))));
        bench.Wire.OnHostSend = () => bench.Host.Clock.Advance(TimeSpan.FromMilliseconds(0.4));
        var (_, board) = bench.Join(connection: 1, player: 7);

        var perFrame = new List<int>();
        for (var frame = 0; frame < 10 && bench.HostSync.Queued > 0; frame++)
        {
            var before = bench.Wire.SentByHost.Count;
            bench.Frame();
            perFrame.Add(bench.Wire.SentByHost.Count - before);
        }

        Assert.All(perFrame, sent => Assert.InRange(sent, 0, 3));
        Assert.True(perFrame.Count >= 4, $"frames: {perFrame.Count}");
        Assert.Equal(0, bench.HostSync.Queued);
        BoardsEqual(bench, board);
    }

    [Fact]
    public void A_change_set_over_the_message_size_goes_in_frames_and_arrives_whole()
    {
        var bench = new SyncBench(SyncLimits.Default with { MaxMessageBytes = 400 });
        var (_, board) = bench.Join(connection: 1, player: 7);
        bench.Frame();
        var drafts = new GhostDraft[10];
        for (var i = 0; i < drafts.Length; i++)
            drafts[i] = bench.Draft("StructureCableStraight", new Cell(5 * i, 0, 0));

        bench.Host.Submit(Place(drafts));
        bench.Frames(3);

        Assert.True(bench.Wire.SentByHost.Count(m => m.Kind == SyncKind.Lines) > 1);
        Assert.All(bench.Wire.SentByHost, m => Assert.True(SyncLimits.Bytes(m.Text) <= 400));
        BoardsEqual(bench, board);
    }

    [Fact]
    public void A_client_that_left_gets_nothing_more()
    {
        var bench = new SyncBench();
        bench.Join(connection: 1, player: 7);
        bench.Frame();
        bench.HostSync.Left(new ConnectionId(1));
        var sent = bench.Wire.SentByHost.Count;

        bench.Host.Submit(Place(bench.Draft("StructureCableStraight", new Cell(0, 0, 0))));
        bench.Frames(2);

        Assert.Equal(sent, bench.Wire.SentByHost.Count);
        Assert.Equal(0, bench.HostSync.Queued);
    }

    [Fact]
    public void A_command_for_a_modded_piece_syncs_like_any_other()
    {
        var modded = new NamedPiece("ModFancyCableStraight", ValueList.Of("ModKitFancyCable"),
            FakeCatalogue.Hand().Named("StructureCableStraight") with { Key = PrefabHash.Piece("ModFancyCableStraight"), Kits = ValueList<KitPlace>.Empty },
            ValueMap<ItemKey, string>.Empty);
        var bench = new SyncBench(modded: modded);
        var (client, board) = bench.Join(connection: 1, player: 7);
        bench.Frame();

        client.Submit(SyncBench.Id(1), Place(bench.Draft("ModFancyCableStraight", new Cell(0, 0, 0))), SyncBench.HostPlayer);
        bench.Frames(3);

        Assert.Equal(PrefabHash.Piece("ModFancyCableStraight"), Assert.IsType<Intent.Build>(Assert.Single(SyncBench.State(board).Ghosts).Intent).Target.Piece);
        BoardsEqual(bench, board);
    }

    private sealed class StateComparer : IEqualityComparer<(List<Ghost> Ghosts, List<Plan> Plans)>
    {
        public bool Equals((List<Ghost> Ghosts, List<Plan> Plans) x, (List<Ghost> Ghosts, List<Plan> Plans) y) =>
            x.Ghosts.SequenceEqual(y.Ghosts) && x.Plans.SequenceEqual(y.Plans);

        public int GetHashCode((List<Ghost> Ghosts, List<Plan> Plans) obj) => obj.Ghosts.Count;
    }
}
