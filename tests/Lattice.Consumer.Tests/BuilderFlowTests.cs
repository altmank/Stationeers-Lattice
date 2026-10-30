using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Testing;

namespace Lattice.Consumer.Tests;

/// <summary>
/// The builders contract from both sides, written against the contract alone over a Lattice.Testing world: a dock-like
/// builder that describes itself, answers the plans assigned to it, claims and builds their pieces and offers what it
/// has set aside; and a panel-like planner that colours a plan, assigns the dock, and reads its state, answer,
/// progress and supply.
/// </summary>
public sealed class BuilderFlowTests
{
    private static readonly ModId Mod = Must(ModId.Create("dockmod"));
    private static readonly RuleId Rule = Must(RuleId.Create(Mod, "dock"));

    private static T Must<T>(Result<T, Reason> result) where T : notnull =>
        result.Match(static v => v, static why => throw new Xunit.Sdk.XunitException($"refused: {why}"));

    private static PrefabKey Frame => TestCatalogue.Hand.Key("StructureFrame").Reduce(() => throw new InvalidOperationException("no frame"));

    private static Outcome Submit(TestWorld world, Command command) =>
        Assert.IsType<Submission.Answered>(world.System.Commands.Submit(CommandId.New(), command, world.Viewer)).Outcome;

    private static ChangeSet Applied(Outcome outcome) => Assert.IsType<Outcome.Applied>(outcome).Changes;

    /// <summary>A builder as a mod writes one: it says who it is, where it works, and answers what it is asked to build.</summary>
    private sealed class Dock(IExecutors executors, IExecutorHandle handle, ExecutorId id)
    {
        public ExecutorId Id => id;

        public static Dock Register(IExecutors executors, Actor owner)
        {
            var id = Must(ExecutorId.Create(Mod, "dock-1"));
            var info = new ExecutorInfo(id, Must(DisplayName.Create("Dock 1"))) { Owner = Option.Some(owner), Builds = new BuildScope.Any() };
            var handle = Must(executors.Register(info));
            handle.Locate(Option.Some<BuilderArea>(new BuilderArea.Sphere(new WorldPoint(0, 0, 0), Must(Metres.Create(40)), Option.Some(Must(Metres.Create(60))))));
            return new Dock(executors, handle, id);
        }

        private readonly IExecutorHandle _handle = handle;

        /// <summary>Answers every plan assigned to it, as the docs ask a builder to in the tick it hears of one.</summary>
        public void AnswerAll()
        {
            foreach (var plan in executors.PlansOf(id))
                Must(_handle.Answer(plan, new AssignmentAnswer.Accepted()));
        }

        public void Offer(PlanId plan, ItemKey item, int count) =>
            Must(_handle.Offer(new SupplyReport(
                ValueList.Of(Must(ItemQuantity.Create(item, count * 2))),
                ValueMap<PlanId, ValueList<ItemQuantity>>.Of(plan, ValueList.Of(Must(ItemQuantity.Create(item, count)))))));

        public Claim Claim(GhostId ghost) =>
            _handle.Claim(ghost, Must(Lease.Create(60))).Match(static c => c, static why => throw new Xunit.Sdk.XunitException(why.Why.ToString()));

        public void Stuck() => _handle.Say(new BuilderWord.Stuck(Reason.NoPower(Rule)));
    }

    [Fact]
    public void A_planner_assigns_a_dock_and_reads_its_answer_state_progress_and_supply()
    {
        using var world = Must(TestWorld.Start(TestScenario.Empty));
        var lattice = world.System;
        var changed = new List<ExecutorId>();
        lattice.Events.BuilderChanged += changed.Add;
        var dock = Dock.Register(lattice.Executors, world.Viewer);

        var plan = Applied(Submit(world, new Command.CreatePlan(new PlanDraft(Must(PlanName.Create("North wall")), Must(Maker.Create(Mod, "panel")),
            new Visibility.Everyone(), ValueMap<AttachmentKey, Attachment>.Empty) { Colour = Option.Some(new Tint(0, 160, 160, 255)) }))).PlansOpened[0];
        Applied(Submit(world, new Command.EditPlan(plan.Id, new PlanEdit.Assign(dock.Id), Option<Revision>.None)));
        var ghost = Applied(Submit(world, new Command.Place(ValueList.Of(new GhostDraft(
            new Intent.Build(new Placement(Frame, new Cell(100, 100, 100), default), Option<Finish>.None), Option.Some(plan.Id), Must(Maker.Create(Mod, "panel")),
            new Visibility.Everyone(), Option<Label>.None, Option<Tint>.None, ValueSet<GroupName>.Empty, ValueMap<AttachmentKey, Attachment>.Empty))))).Added[0].Id;
        Must(world.Settle());

        Assert.Equal(new Assignment[] { new Assignment.Asked(dock.Id) }, lattice.Executors.AssignedTo(plan.Id));
        dock.AnswerAll();
        dock.Offer(plan.Id, new ItemKey(77), 4);
        dock.Claim(ghost);
        dock.Stuck();
        Must(world.Settle());

        var read = lattice.Executors.Find(dock.Id).Reduce(() => throw new Xunit.Sdk.XunitException("not listed"));
        Assert.IsType<BuilderState.Working>(read.State);
        Assert.Equal(new Assignment[] { new Assignment.Accepted(dock.Id) }, lattice.Executors.AssignedTo(plan.Id));
        Assert.Equal(ValueList.Of(Must(ItemQuantity.Create(new ItemKey(77), 4))), lattice.Executors.SupplyToward(plan.Id));
        Assert.Equal(new BuilderProgress(0, 1, 0), lattice.Executors.ProgressOf(plan.Id, dock.Id));
        Assert.Equal(0, lattice.Executors.UnclaimedIn(plan.Id));
        Assert.Contains(dock.Id, changed);

        var built = Must(world.Build(Frame, new Cell(100, 100, 100), default));
        for (var i = 0; i < 8 && lattice.Executors.ProgressOf(plan.Id, dock.Id).Built == 0; i++)
        {
            Must(world.Advance(built));
            Must(world.Settle());
        }

        Assert.Equal(new BuilderProgress(1, 0, 0), lattice.Executors.ProgressOf(plan.Id, dock.Id));
        Assert.Equal(new BuilderProgress(1, 0, 0), lattice.Executors.ProgressOf(plan.Id));
        Assert.Equal(new BuilderState.Stuck(Reason.NoPower(Rule)), lattice.Executors.Find(dock.Id).Reduce(() => throw new Xunit.Sdk.XunitException("gone")).State);
    }
}
