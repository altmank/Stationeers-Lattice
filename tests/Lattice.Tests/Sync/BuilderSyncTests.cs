using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Sync;
using Lattice.Tests.Board;
using Lattice.Tests.Executors;
using Lattice.TestKit;

namespace Lattice.Tests.Sync;

/// <summary>
/// Builders on every seat: the host's builders and what each says reach a client with its claims (the whole list at a
/// join, then one line per changed builder per frame, and a gone line when a handle is disposed), and the client works
/// out each builder's state from its mirrored claims and the builder's word, as the host does. Plan colour and the
/// assigned set travel with the plan; the assign and recolour edits are commands like any plan edit.
/// </summary>
public sealed class BuilderSyncTests
{
    private static readonly Reason NoPower = Reason.NoPower(Builders.Dock);

    private static ExecutorId Dock => Builders.Id("dock");

    private static Builder ReadOn(ExecutorSyncBench.Seat seat, ExecutorId id) =>
        seat.Executors.Find(id).Reduce(() => throw new Xunit.Sdk.XunitException($"{id} is not on the client"));

    [Fact]
    public void A_joining_client_reads_every_builder_and_what_each_says()
    {
        var bench = new ExecutorSyncBench();
        var dock = bench.Register("dock");
        var area = new BuilderArea.Sphere(new WorldPoint(1.5f, 2, -3.25f), TestKitIds.Must(Metres.Create(12)), Option<Metres>.None);
        dock.Locate(Option.Some<BuilderArea>(area));
        dock.Say(new BuilderWord.Stuck(NoPower));
        TestKitIds.Must(dock.Offer(Builders.Supply(ValueList.Of(Builders.Item(3, 40)))));
        bench.HostClock.Advance(10);

        var client = bench.Join(1, clock: 5000);
        bench.Settle();

        var seen = ReadOn(client, Dock);
        Assert.Equal(Option.Some<BuilderArea>(area), seen.Area);
        Assert.Equal(new BuilderState.Stuck(NoPower), seen.State);
        var supply = seen.Supply.Reduce(() => throw new Xunit.Sdk.XunitException("no supply"));
        Assert.Equal(ValueList.Of(Builders.Item(3, 40)), supply.Report.Reaches);
        Assert.Equal(4990, supply.At.Seconds, 2);
        Assert.Empty(client.Reports);
    }

    [Fact]
    public void A_change_reaches_the_client_once_and_a_gone_builder_leaves_it()
    {
        var bench = new ExecutorSyncBench();
        var client = bench.Join(1);
        var told = new List<ExecutorId>();
        client.T.Events.BuilderChanged += told.Add;
        var dock = bench.Register("dock");
        bench.Settle();
        Assert.Equal(new[] { Dock }, told);

        dock.Say(new BuilderWord.Stuck(NoPower));
        dock.Locate(Option<BuilderArea>.None);
        bench.Settle();
        Assert.Equal(new[] { Dock, Dock }, told);
        Assert.IsType<BuilderState.Stuck>(ReadOn(client, Dock).State);

        dock.Dispose();
        bench.Settle();
        Assert.Empty(client.Executors.All);
        Assert.Equal(3, told.Count);
    }

    [Fact]
    public void The_client_works_a_builder_s_state_out_from_its_mirrored_claims()
    {
        var bench = new ExecutorSyncBench();
        var client = bench.Join(1);
        var dock = bench.Register("dock");
        dock.Say(new BuilderWord.Stuck(NoPower));
        var ghost = bench.Ghost();
        bench.Settle();
        Assert.IsType<BuilderState.Stuck>(ReadOn(client, Dock).State);

        ExecutorSyncBench.Claim(dock, ghost);
        bench.Settle();

        Assert.IsType<BuilderState.Working>(ReadOn(client, Dock).State);
        Assert.Equal(bench.Executors.Find(Dock), client.Executors.Find(Dock));
    }

    [Fact]
    public void An_assignment_and_its_answer_read_the_same_on_the_client()
    {
        var bench = new ExecutorSyncBench();
        var client = bench.Join(1);
        var dock = bench.Register("dock");
        var plan = TestBoard.Applied(bench.Host.T.Apply(new Command.CreatePlan(new PlanDraft(TestKitIds.Must(PlanName.Create("Hall")), TestBoard.TestMaker,
            new Visibility.Everyone(), ValueMap<AttachmentKey, Attachment>.Empty) { Colour = Option.Some(new Tint(1, 2, 3, 255)) }))).PlansOpened[0].Id;
        TestBoard.Applied(bench.Host.T.Apply(new Command.EditPlan(plan, new PlanEdit.Assign(Dock), Option<Revision>.None)));
        TestKitIds.Must(dock.Answer(plan, new AssignmentAnswer.Refused(NoPower)));
        bench.Settle();

        Assert.Equal(bench.Executors.AssignedTo(plan), client.Executors.AssignedTo(plan));
        Assert.Equal(new[] { plan }, client.Executors.PlansOf(Dock));
        Assert.Equal(Option.Some(new Tint(1, 2, 3, 255)), client.T.Board.Index.Plans[plan].Plan.Colour);
    }

    [Fact]
    public void The_new_plan_edits_are_written_as_commands_and_read_back()
    {
        foreach (var edit in new PlanEdit[]
        {
            new PlanEdit.Recolour(Option.Some(new Tint(9, 8, 7, 6))),
            new PlanEdit.Recolour(Option<Tint>.None),
            new PlanEdit.Assign(Dock),
            new PlanEdit.Unassign(Dock),
        })
        {
            var id = new CommandId(System.Guid.NewGuid());
            Command command = new Command.EditPlan(new PlanId(4), edit, Option<Revision>.None);

            var read = TestKitIds.Must(CommandForm.Read(TestKitIds.Must(CommandForm.Write(id, command))));

            Assert.Equal((id, command), read);
        }
    }

    [Fact]
    public void A_status_message_with_builder_lines_reads_back_as_written()
    {
        var info = ExecutorsBench.Info("dock") with
        {
            Owner = Option.Some(TestBoard.Bob),
            Builds = new BuildScope.Families(ValueSet.Of(TestKitIds.Must(PieceFamily.Create(TestKitIds.Mod, "frames")))),
        };
        var says = new Lattice.Core.Executors.BuilderSays(
            info,
            Option.Some<BuilderArea>(new BuilderArea.Sphere(new WorldPoint(0.1f, 0.2f, 0.3f), TestKitIds.Must(Metres.Create(5)), Option.Some(TestKitIds.Must(Metres.Create(9))))),
            new BuilderWord.Stuck(NoPower),
            Option.Some(new Supply(Builders.Supply(ValueList.Of(Builders.Item(1, 2)), (new PlanId(3), new[] { Builders.Item(4, 5) }), (new PlanId(6), System.Array.Empty<ItemQuantity>())), new GameTime(40))),
            ValueMap<PlanId, AssignmentAnswer>.Empty.With(new PlanId(3), new AssignmentAnswer.Accepted()).With(new PlanId(6), new AssignmentAnswer.Refused(NoPower)));
        var lines = new BuilderLine[] { new BuilderLine.Says(says), new BuilderLine.Gone(Builders.Id("gone")) };

        var read = TestKitIds.Must(StatusForm.Read(StatusForm.Write(false, System.Array.Empty<Lattice.Core.Executors.ClaimState>(), lines, new GameTime(100)), new GameTime(100)));

        Assert.Equal(lines, read.Builders);
    }
}
