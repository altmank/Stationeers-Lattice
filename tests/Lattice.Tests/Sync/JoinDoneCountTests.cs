using System;
using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Commands;
using Lattice.Core.Sync;
using Lattice.TestKit;
using Lattice.TestKit.World;
using Lattice.Tests.Board;
using Lattice.Tests.Plans;
using Xunit;
using static Lattice.Tests.Validation.ValidationBench;

namespace Lattice.Tests.Sync;

/// <summary>
/// A client that joins after ghosts were done gets each plan's done count with the board, so its plan facts and
/// states give the host's answer from the first frame; done ghosts after the join count once.
/// </summary>
public sealed class JoinDoneCountTests
{
    private static Completion Seen => new(Option<StructureRef>.None, DateTimeOffset.UnixEpoch);

    [Fact]
    public void A_client_joining_after_ghosts_were_done_shows_the_hosts_done_counts_and_states()
    {
        var seats = new Seats();
        var loose = seats.Host.V.Ghosts(("StructureFrameIron", Large(0, 0, 0)), ("StructureFrameIron", Large(3, 0, 0)), ("StructureFrameIron", Large(6, 0, 0)));
        var hall = seats.NewPlan("Hall", Large(0, 0, 9), Large(3, 0, 9));
        var shed = seats.NewPlan("Shed", Large(0, 0, 15), Large(3, 0, 15));
        var yard = seats.NewPlan("Yard", Large(0, 0, 21));
        seats.Done(loose[0], loose[1]);
        seats.Done(shed.Ghosts.ToArray());

        seats.Join();

        foreach (var plan in new[] { GhostBoard.DefaultPlan, hall.Plan, shed.Plan, yard.Plan })
            Assert.Equal(seats.Host.Facts(plan).Progress, seats.Client.Facts(plan).Progress);
        Assert.Equal(new Progress(2, 0, 1), seats.Client.Facts(GhostBoard.DefaultPlan).Progress);
        Assert.Equal(new Progress(0, 0, 2), seats.Client.Facts(hall.Plan).Progress);
        Assert.Equal(new Progress(2, 0, 0), seats.Client.Facts(shed.Plan).Progress);
        Assert.IsType<PlanState.InProgress>(seats.Client.State(GhostBoard.DefaultPlan));
        Assert.IsType<PlanState.Complete>(seats.Client.State(shed.Plan));
        Assert.Equal(seats.Host.State(hall.Plan).GetType(), seats.Client.State(hall.Plan).GetType());
        Assert.Empty(seats.Reports);
    }

    [Fact]
    public void A_ghost_done_after_the_join_counts_once_on_the_client()
    {
        var seats = new Seats();
        var loose = seats.Host.V.Ghosts(("StructureFrameIron", Large(0, 0, 0)), ("StructureFrameIron", Large(3, 0, 0)), ("StructureFrameIron", Large(6, 0, 0)));
        seats.Done(loose[0]);
        seats.Join();

        seats.Done(loose[1]);

        Assert.Equal(new Progress(2, 0, 1), seats.Host.Facts().Progress);
        Assert.Equal(new Progress(2, 0, 1), seats.Client.Facts().Progress);
        Assert.Empty(seats.Reports);
    }

    [Fact]
    public void A_resync_join_keeps_the_done_counts_without_counting_twice()
    {
        var seats = new Seats();
        var loose = seats.Host.V.Ghosts(("StructureFrameIron", Large(0, 0, 0)), ("StructureFrameIron", Large(3, 0, 0)));
        seats.Done(loose[0]);
        seats.Join();

        seats.HostSync.Rejoin(Seats.Connection, RejoinForm.Write([]));
        seats.Frames();

        Assert.Equal(new Progress(1, 0, 1), seats.Client.Facts().Progress);
        Assert.Equal(seats.Host.Facts().Progress, seats.Client.Facts().Progress);
        Assert.Empty(seats.Reports);
    }

    [Fact]
    public void The_first_join_part_writes_and_reads_each_plans_done_count()
    {
        var plans = new List<Plan> { NewPlan(1), NewPlan(5), NewPlan(6) };
        var done = new Dictionary<PlanId, int> { [new PlanId(1)] = 3, [new PlanId(6)] = 12 };

        var text = JoinParts.Write(new Revision(9), plans, done, [], part: 0, perPart: 10);
        var read = TestKitIds.Must(JoinParts.Read(text));

        Assert.Equal(plans, read.Plans);
        Assert.Equal(2, read.Done.Count);
        Assert.Equal(3, read.Done[new PlanId(1)]);
        Assert.Equal(12, read.Done[new PlanId(6)]);
        Assert.Contains("done=3", text);
        Assert.DoesNotContain("done=0", text);
    }

    [Theory]
    [InlineData("done=x")]
    [InlineData("done=0")]
    [InlineData("done=-2")]
    [InlineData("built=4")]
    public void A_plan_line_with_a_bad_done_count_or_another_field_is_refused(string field)
    {
        var text = JoinParts.Write(new Revision(9), [NewPlan(1)], new Dictionary<PlanId, int> { [new PlanId(1)] = 7 }, [], part: 0, perPart: 10)
            .Replace("done=7", field);

        Assert.True(JoinParts.Read(text).TryGetError(out var why));
        Assert.Equal("unreadable plan", why.Facts["problem"]);
    }

    private static Plan NewPlan(long id) => new(
        new PlanId(id), new Revision(id), TestKitIds.Must(PlanName.Create("P" + id)), TestBoard.Alice, TestBoard.TestMaker,
        new Visibility.Everyone(), ValueMap<AttachmentKey, Attachment>.Empty);

    /// <summary>A host and a client, each a board with validation and plan state, over one sync wire.</summary>
    private sealed class Seats
    {
        public static readonly ConnectionId Connection = new(1);

        private readonly Wire _wire = new();
        private ClientSync? _client;

        public Seats()
        {
            var checks = new ClientChecks(CommandLimits.Default, Pieces, new AttachmentLimits());
            var commands = new CommandService(Host.V.Board, new CommandExpanders(), new FakeWorldQuery(Host.V.World), checks, Host.V.Scheduler);
            HostSync = new HostSync(commands, Host.V.Board, Host.V.Scheduler, _wire.Host, SyncLimits.Default, Reports.Add);
        }

        public PlanBench Host { get; } = new();

        public PlanBench Client { get; } = new();

        public HostSync HostSync { get; }

        public List<Reason> Reports { get; } = new();

        public (PlanId Plan, ValueList<GhostId> Ghosts) NewPlan(string name, params Cell[] at)
        {
            var draft = new PlanDraft(TestKitIds.Must(PlanName.Create(name)), TestBoard.TestMaker, new Visibility.Everyone(), ValueMap<AttachmentKey, Attachment>.Empty);
            var create = new Command.CreatePlan(draft)
            {
                Ghosts = ValueList.From(at.Select(cell => TestBoard.Draft(new Intent.Build(new Placement(Pieces.Named("StructureFrameIron").Key, cell, default), Option<Finish>.None)))),
            };
            var set = TestBoard.Applied(Host.V.Apply(create));
            return (set.PlansOpened[0].Id, ValueList.From(set.Added.Select(g => g.Id)));
        }

        public void Done(params GhostId[] ghosts)
        {
            foreach (var ghost in ghosts)
                TestKitIds.Must(Host.V.Board.Complete(ghost, Seen));
            Frames();
        }

        public void Join()
        {
            _client = new ClientSync(Client.V.Board, _wire.Client(Connection), SyncLimits.Default, Reports.Add);
            HostSync.Joined(Connection);
            Frames();
        }

        /// <summary>Frames on both seats, the host's messages delivered to the client between them.</summary>
        public void Frames(int count = 8)
        {
            for (var i = 0; i < count; i++)
            {
                Host.V.Frame();
                if (_client is not null && _wire.ToClients.TryGetValue(Connection, out var queue))
                    while (queue.Count > 0)
                    {
                        var (kind, text) = queue.Dequeue();
                        _client.Receive(kind, text);
                    }

                Client.V.Frame();
            }
        }
    }
}
